#!/usr/bin/env bash
#
# Выкладка собранного выпуска Linux на канал обновлений и проверка того, что
# канал отдаёт. По образцу shared/tools/publish.ps1.
#
#   tools/publish.sh --version 0.1.2
#   tools/publish.sh --version 0.1.2 --dry-run       сверить файлы, ничего не класть
#   tools/publish.sh --version 0.1.2 --verify-only   только проверить канал
#
# Отдельно от release.sh по той же причине, что на Windows и macOS: выпуск
# делается один раз и требует ключа подписи, выкладка — сколько угодно раз и
# требует доступа к каналу. Слитые вместе, они означали бы пересборку ради
# повторной выкладки, то есть другой пакет под тем же номером.
#
# Что делает, по порядку:
#
#   1. сверяет манифест с пакетом до всякой сети: версия, адрес в каталоге
#      releases/linux/ этого же канала, sha256 и размер;
#   2. кладёт пакет, потом манифест (PUT /internal/objects/<ключ>, Bearer).
#      Наоборот — это машины, которые пошли за файлом, которого ещё нет;
#   3. забирает с канала то, что он теперь отдаёт, с парой Basic: манифест
#      байт в байт и с подписью, сходящейся с ключом заводской настройки,
#      пакет по sha256 и размеру, Range, и отказ 401 без пары.
#
# Канал один — собственный сервер (update). Замороженного R2 (legacy) у
# Linux не было никогда.
#
# Доступы — в ~/.config/EliteSIP-release/publish.local.json, вне Git, тот же
# формат, что у Windows (раздел update: baseURL, basicAuth или
# basicUser/basicPassword, adminToken). Секреты в командную строку curl не
# попадают: они уходят в его настройки через стандартный ввод (-K -), а
# аргументы процесса видны любому, кто смотрит список процессов.
#
# Метку выпуска ставит release.sh; отсюда метки не трогаются.

set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
artifacts="$root/artifacts/linux"

version=
config="$HOME/.config/EliteSIP-release/publish.local.json"
provisioning="$HOME/.config/EliteSIP-release/provisioning.linux.json"
dry_run=false
verify_only=false

step() {
    echo
    echo "== $*"
}

ok() {
    echo "  ок: $*"
}

warn() {
    echo "  ВНИМАНИЕ: $*"
}

fail() {
    echo
    echo "выкладка остановлена: $*" >&2
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --config) config=$2; shift 2 ;;
        --provisioning) provisioning=$2; shift 2 ;;
        --artifacts) artifacts=$2; shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        --verify-only) verify_only=true; shift ;;
        *) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
    esac
done

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "версия — три числа через точку: --version X.Y.Z"
command -v curl >/dev/null || fail "нет curl"
command -v python3 >/dev/null || fail "нет python3"

# --- Доступы ------------------------------------------------------------------

[ -f "$config" ] || fail "нет $config — заведите его по образцу Windows (shared/docs/RELEASES.md), раздел update"

json() {
    python3 -I -c 'import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8-sig"))
for part in sys.argv[2].split("."):
    data = data.get(part) if isinstance(data, dict) else None
print("" if data is None else data)' "$1" "$2"
}

base_url=$(json "$config" update.baseURL)
base_url=${base_url%/}
basic_auth=$(json "$config" update.basicAuth)
if [ -z "$basic_auth" ]; then
    basic_user=$(json "$config" update.basicUser)
    basic_password=$(json "$config" update.basicPassword)
    [ -n "$basic_user" ] && [ -n "$basic_password" ] && basic_auth="$basic_user:$basic_password"
fi

[ -n "$base_url" ] || fail "в $config нет update.baseURL"
[ -n "$basic_auth" ] || fail "в $config нет update.basicAuth (или basicUser и basicPassword)"

# Значение для файла настроек curl: в кавычках, с экранированными \ и ".
quote() {
    local value=${1//\\/\\\\}
    printf '"%s"' "${value//\"/\\\"}"
}

# Запрос с секретом в настройках через стандартный ввод. Печатает код ответа.
request() {
    local secret=$1
    shift
    printf '%s\n' "$secret" | curl -sS -K - -w '%{http_code}' "$@" || fail "curl завершился с кодом $?"
}

basic_secret="user = $(quote "$basic_auth")"

# --- 1. Что выкладываем -------------------------------------------------------

step "канал update → $base_url"

package="$artifacts/EliteSIP-$version.deb"
envelope="$artifacts/current.json"
expected_url="$base_url/releases/linux/EliteSIP-$version.deb"

[ -f "$package" ] || fail "нет пакета $package — его собирает tools/release.sh"
[ -f "$envelope" ] || fail "нет манифеста $envelope"

payload=$(python3 -I -c 'import base64, json, sys
envelope = json.load(open(sys.argv[1], encoding="utf-8"))
print(base64.b64decode(envelope["payload"]).decode("utf-8"))' "$envelope") \
    || fail "манифест $envelope не разбирается: это не конверт releasekit sign"

field() {
    python3 -I -c 'import json, sys
print(json.loads(sys.argv[1]).get(sys.argv[2], ""))' "$payload" "$1"
}

digest=$(sha256sum "$package" | cut -d' ' -f1)
size=$(stat -c %s "$package")

[ "$(field version)" = "$version" ] || fail "в манифесте версия $(field version), а выкладывается $version"
[ "$(field url)" = "$expected_url" ] \
    || fail "в манифесте url $(field url), а нужен $expected_url: клиент качает пакет только с хоста манифеста и только из releases/linux/"
[ "$(field sha256 | tr '[:upper:]' '[:lower:]')" = "$digest" ] || fail "sha256 в манифесте не совпадает с пакетом"
[ "$(field size)" = "$size" ] || fail "размер в манифесте $(field size), у пакета $size"

ok "манифест $version сходится с пакетом: sha256 $digest, $size байт"

# --- 2. Выкладка --------------------------------------------------------------

publish_object() {
    local source=$1 key=$2 type=$3

    if $dry_run; then
        echo "  [сухой прогон] положил бы $(basename "$source") → $key"
        return
    fi

    local token
    token=$(json "$config" update.adminToken)
    [ -n "$token" ] || fail "в $config нет update.adminToken"

    local code
    code=$(request "header = $(quote "Authorization: Bearer $token")" \
        -o /dev/null -X PUT -H "Content-Type: $type" --data-binary "@$source" "$base_url/internal/objects/$key")
    [ "$code" = 204 ] || fail "сервер отверг $key: HTTP $code"

    ok "выложено: $key"
}

if ! $verify_only; then
    step "выкладка: сперва пакет, потом манифест"
    publish_object "$package" "releases/linux/EliteSIP-$version.deb" application/vnd.debian.binary-package
    publish_object "$envelope" "releases/linux/current.json" application/json
fi

if $dry_run; then
    echo
    echo "сухой прогон: на канале ничего не изменилось"
    exit 0
fi

# --- 3. Что канал отдаёт ------------------------------------------------------

step "проверка канала с парой Basic"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

served_manifest="$work/current.json"
code=$(request "$basic_secret" -o "$served_manifest" "$base_url/releases/linux/current.json")
[ "$code" = 200 ] || fail "манифест не отдан: HTTP $code"
cmp -s "$served_manifest" "$envelope" || fail "канал отдаёт не тот манифест (кэш или чужая выкладка)"
ok "манифест отдаётся, байт в байт тот, что подписан"

# Подпись — кодом клиента и ключом из заводской настройки Linux: тем, что
# рабочие места получат вместе с пакетом.
kit="$root/shared/tools/ReleaseKit/bin/Release/net10.0/releasekit.dll"
if [ -f "$kit" ] && [ -f "$provisioning" ]; then
    json "$provisioning" releasesPublicKey > "$work/releases.pub"
    dotnet "$kit" verify "$served_manifest" "$work/releases.pub" >/dev/null \
        || fail "подпись отданного манифеста не сходится с ключом клиента ($provisioning)"
    ok "подпись отданного манифеста сходится с ключом клиента"
else
    warn "подпись не проверена — нет releasekit ($kit) или заводской настройки ($provisioning)"
fi

served_package="$work/EliteSIP-$version.deb"
code=$(request "$basic_secret" -o "$served_package" "$expected_url")
[ "$code" = 200 ] || fail "пакет не отдан: HTTP $code"
[ "$(sha256sum "$served_package" | cut -d' ' -f1)" = "$digest" ] && [ "$(stat -c %s "$served_package")" = "$size" ] \
    || fail "скачанный пакет не сходится с выпуском по sha256 или размеру"
ok "пакет скачивается: sha256 и размер совпадают"

# Докачка: сервер обещает 206, и прокси между ним и машиной ломают именно это.
code=$(request "$basic_secret" -o /dev/null -r 0-1023 "$expected_url")
if [ "$code" = 206 ]; then ok "Range отдаёт 206"; else warn "Range ответил $code, а не 206"; fi

code=$(curl -sS -o /dev/null -w '%{http_code}' "$base_url/releases/linux/current.json") || fail "curl завершился с кодом $?"
[ "$code" = 401 ] || fail "без пары манифест отдаётся с кодом $code — канал открыт"
ok "без пары — 401"

echo
echo "Канал отдаёт Linux $version. Дальше — поставить выпуск на одну машину и"
echo "убедиться, что она обновилась сама (/var/lib/elitesip/updater/updater.log)."

#!/usr/bin/env bash
#
# Выпуск Linux-версии EliteSIP: одна команда вместо десяти шагов — по образцу
# shared/tools/release.ps1, и по той же причине: ручной выпуск рано или поздно
# выходит без одного из шагов, и обычно это версия или подпись.
#
#   tools/release.sh --version 0.1.2 --signing-key ~/.config/EliteSIP-release/linux-releases.key
#
# Шаги идут в этом порядке и не переставляются:
#
#   1. дерево чистое, подмодуль shared/ без правок и стоит на метке v<версия>
#      Windows-репозитория (правила подмодуля 1 и 3), версия растёт;
#   2. версия проставляется в Directory.Build.props;
#   3. сборка и все проверки; провалилась хоть одна — выпуска нет;
#   4. заводская настройка сверяется с каналом и ключами;
#   5. пакет .deb (tools/build-deb.sh);
#   6. манифест releases/linux/current.json и подпись Ed25519;
#   7. проверка манифеста кодом клиента (releasekit verify);
#   8. что и куда выкладывать — печатается, а не делается.
#
# Шаг 8 не автоматизирован намеренно, как и на Windows: выкладка на канал —
# место, где ошибка видна всем машинам сразу, и делает её человек глазами.
#
# Ключ подписи — СВОЙ у линии Linux, не ключ Windows (план, W-L7, «Канал»):
# с общим ключом подписанный манифест Linux, подложенный в
# releases/current.json, прошёл бы проверку Windows-клиента. Новая пара —
# `dotnet shared/tools/ReleaseKit/bin/Release/net10.0/releasekit.dll keygen <каталог>`,
# открытая половина — в releasesPublicKey заводской настройки Linux.
#
# Ключи:
#   --version X.Y.Z         версия выпуска
#   --signing-key ФАЙЛ      закрытый ключ линии Linux; рядом — ФАЙЛ.pub
#   --provisioning ФАЙЛ     заводская настройка (по умолчанию
#                           ~/.config/EliteSIP-release/provisioning.linux.json)
#   --base-url URL          корень канала (по умолчанию https://update.elitesip.vip:8081)
#   --panel-public-key КЛЮЧ ключ Spark, которым подписаны предустановки
#   --windows-public-key ФАЙЛ  открытый ключ линии Windows: если задан,
#                           скрипт откажется подписывать тем же ключом
#   --notes ТЕКСТ           заметки выпуска (поле notes манифеста)
#   --source ИСТОЧНИК       дополнительный источник NuGet для публикации
#   --dry-run               не трогать дерево: грязное дерево и подмодуль не
#                           на метке — предупреждение, версия не пишется

set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)
artifacts="$root/artifacts/linux"
props="$root/Directory.Build.props"

version=
signing_key=
provisioning="$HOME/.config/EliteSIP-release/provisioning.linux.json"
base_url="https://update.elitesip.vip:8081"
panel_public_key="P8XaNYhYwDSSS+805amNpc84GGrnnWV+mgbvyPiWBIs="
windows_public_key=
notes=
source=
dry_run=false
jobs=${ELITESIP_BUILD_JOBS:-2}

step() {
    echo
    echo "== $*"
}

fail() {
    echo
    echo "выпуск остановлен: $*" >&2
    exit 1
}

warn() {
    echo "  ВНИМАНИЕ: $*"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --signing-key) signing_key=$2; shift 2 ;;
        --provisioning) provisioning=$2; shift 2 ;;
        --base-url) base_url=${2%/}; shift 2 ;;
        --panel-public-key) panel_public_key=$2; shift 2 ;;
        --windows-public-key) windows_public_key=$2; shift 2 ;;
        --notes) notes=$2; shift 2 ;;
        --source) source=$2; shift 2 ;;
        --dry-run) dry_run=true; shift ;;
        *) sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
    esac
done

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "версия — три числа через точку: --version X.Y.Z"

export DOTNET_CLI_TELEMETRY_OPTOUT=1

# --- 1. Дерево, подмодуль, версия ---------------------------------------------

step "проверка дерева, подмодуля и версии"

cd "$root"

if [ -n "$(git status --porcelain)" ]; then
    # Выпуск из грязного дерева невоспроизводим: собранное невозможно
    # сопоставить с коммитом.
    $dry_run && warn "в рабочем дереве есть несохранённые правки" \
        || fail "в рабочем дереве есть несохранённые правки"
fi

# Правило подмодуля 1: общий код здесь не правится. Незакоммиченная правка в
# shared/ — это выпуск из кода, которого нет ни в одном репозитории.
if [ -n "$(git -C shared status --porcelain)" ]; then
    $dry_run && warn "в shared/ есть несохранённые правки" \
        || fail "в shared/ есть несохранённые правки — общий код правится в Win-EliteSIP"
fi

# Правило подмодуля 3: указатель стоит на выпущенной метке Windows-
# репозитория. Иначе выпуск Linux собран из общего кода, который не прошёл
# пилот Windows.
shared_tag=$(git -C shared describe --tags --exact-match --match 'v*' 2>/dev/null || true)
if [ -z "$shared_tag" ]; then
    $dry_run && warn "подмодуль shared/ не на метке v<версия> ($(git -C shared describe --tags 2>/dev/null || git -C shared rev-parse --short HEAD))" \
        || fail "подмодуль shared/ не стоит на метке v<версия> Windows-репозитория"
fi

current=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$props" | head -1)
[ -n "$current" ] || fail "в $props нет <Version>"

# Версия обязана расти: линия обновлений предлагает только вперёд.
if [ "$(printf '%s\n%s\n' "$current" "$version" | sort -V | tail -1)" != "$version" ] || [ "$current" = "$version" ]; then
    fail "версия $version не больше текущей $current"
fi

echo "  версия: $current -> $version"
echo "  общий код: ${shared_tag:-не на метке} ($(git -C shared rev-parse --short HEAD))"

# --- 2. Версия в Directory.Build.props ----------------------------------------

if ! $dry_run; then
    sed -i "s:<Version>[^<]*</Version>:<Version>$version</Version>:" "$props"
fi

# --- 3. Сборка и проверки -----------------------------------------------------

step "сборка и проверки"

# Параллельность ограничена: ReadyToRun и проверки на всех ядрах съедали
# память машины разработки целиком (22 октября 2026 она зависла).
dotnet build EliteSIP.Linux.slnx --configuration Release --nologo -maxcpucount:"$jobs" \
    -p:Version="$version" -p:NuGetAudit=false \
    || fail "сборка не прошла"

dotnet test EliteSIP.Linux.slnx --configuration Release --nologo --no-build -maxcpucount:"$jobs" \
    || fail "проверки не прошли"

kit="$root/shared/tools/ReleaseKit/bin/Release/net10.0/releasekit.dll"
[ -f "$kit" ] || fail "releasekit не собран: $kit"

releasekit() {
    dotnet "$kit" "$@"
}

# --- 4. Заводская настройка ---------------------------------------------------

step "заводская настройка"

[ -f "$provisioning" ] || fail "нет заводской настройки $provisioning. Без неё линия панели, активация и обновления выключены, и заметить это можно только на чистой машине"

field() {
    python3 -c 'import json,sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
for part in sys.argv[2].split("."):
    data = data.get(part) if isinstance(data, dict) else None
print("" if data is None else data)' "$provisioning" "$1"
}

# Те же правила, что у release.ps1: канал один, без releasesURL; канал
# настройки совпадает с каналом манифеста; оба ключа на месте; ключ
# предустановок — ключ Spark.
[ -z "$(field updates.releasesURL)" ] || fail "в заводской настройке есть updates.releasesURL — уберите его, канал задаётся одним baseURL"

factory_base=$(field updates.baseURL)
[ "${factory_base%/}" = "$base_url" ] || fail "канал в заводской настройке ($factory_base) не совпадает с --base-url ($base_url)"

factory_releases_key=$(field releasesPublicKey)
factory_presets_key=$(field presetsPublicKey)
[ -n "$factory_releases_key" ] && [ -n "$factory_presets_key" ] || fail "в заводской настройке нет releasesPublicKey или presetsPublicKey"
[ "$factory_presets_key" = "$panel_public_key" ] || fail "presetsPublicKey ($factory_presets_key) — не ключ Spark ($panel_public_key)"

echo "  канал: $factory_base"

# Ключ подписи — до сборки пакета: без него сборка была бы зря.
[ -n "$signing_key" ] || fail "не задан --signing-key: манифест нечем подписать"
[ -f "$signing_key" ] || fail "ключ подписи не найден: $signing_key"
public_key="${signing_key%.key}.pub"
[ -f "$public_key" ] || fail "рядом с ключом нет открытой половины: $public_key"

# Открытая половина обязана быть тем ключом, который клиент получит в
# заводской настройке. Иначе манифест подписан «верно», а машины его молча
# отвергнут — и правильно сделают.
[ "$(tr -d '[:space:]' < "$public_key")" = "$factory_releases_key" ] \
    || fail "открытый ключ $public_key не совпадает с releasesPublicKey заводской настройки"
[ "$(releasekit public "$signing_key")" = "$factory_releases_key" ] \
    || fail "закрытый ключ $signing_key не от пары $public_key"

if [ -n "$windows_public_key" ]; then
    [ "$(tr -d '[:space:]' < "$windows_public_key")" != "$factory_releases_key" ] \
        || fail "ключ линии Linux совпадает с ключом линии Windows — у линий ключи свои"
fi

# --- 5. Пакет -----------------------------------------------------------------

step "пакет"

mkdir -p "$artifacts"
build_args=(--version "$version" --provisioning "$provisioning" --output "$artifacts")
[ -n "$source" ] && build_args+=(--source "$source")
ELITESIP_BUILD_JOBS=$jobs "$root/tools/build-deb.sh" "${build_args[@]}" || fail "пакет не собрался"

package="$artifacts/EliteSIP-$version.deb"
[ -f "$package" ] || fail "нет пакета $package"

# --- 6–7. Манифест, подпись, проверка кодом клиента ---------------------------

step "манифест обновления"

digest=$(releasekit hash "$package")
size=$(stat -c %s "$package")
published_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
manifest="$artifacts/manifest.json"
envelope="$artifacts/current.json"

# Адрес — на тот же хост, с которого манифест будет отдан
# (ReleaseManifest.IsServedFrom), и в каталог линии Linux (ReleaseLine.Holds).
VERSION=$version URL="$base_url/releases/linux/EliteSIP-$version.deb" SHA=$digest SIZE=$size \
PUBLISHED=$published_at NOTES=$notes python3 - "$manifest" <<'EOF'
import json, os, sys
manifest = {
    "format": 1,
    "version": os.environ["VERSION"],
    "url": os.environ["URL"],
    "sha256": os.environ["SHA"],
    "size": int(os.environ["SIZE"]),
    "published_at": os.environ["PUBLISHED"],
    "notes": os.environ["NOTES"],
}
# Без BOM: метка попала бы внутрь подписанного содержимого.
with open(sys.argv[1], "w", encoding="utf-8") as out:
    json.dump(manifest, out, ensure_ascii=False, indent=2)
EOF

releasekit sign "$manifest" "$signing_key" "$envelope" || fail "манифест не подписался"

# Тем же ReleaseManifest.Verified, которым его прочитает рабочее место.
releasekit verify "$envelope" "$public_key" || fail "манифест не прошёл проверку кодом клиента"

# --- 8. Что выкладывать -------------------------------------------------------

step "выпуск собран"

cat <<EOF

  пакет:     $package
  sha256:    $digest
  размер:    $size
  манифест:  $envelope ($base_url)

Выложить на канал (PUT /internal/objects/<ключ>, как publish.ps1):
  releases/linux/EliteSIP-$version.deb  <- $package
  releases/linux/current.json           <- $envelope

Порядок значим: сперва пакет, потом манифест. Наоборот — это машины, которые
пошли за файлом, которого ещё нет.

После выкладки: поставить выпуск на одну машину и убедиться, что она
обновилась сама (журнал /var/lib/elitesip/updater/updater.log и
journalctl -u elitesip-updater). Проверка «манифест разобрался» этого не
заменяет — пакет ставит apt, а не мы.
EOF

$dry_run && echo && echo "проверочный прогон: версия в Directory.Build.props не менялась."
exit 0

#!/usr/bin/env bash
#
# Пакет .deb из исходников: публикация, раскладка по системе, сборка пакета.
#
# Отдельно от release.sh, потому что пакет нужен не только в день выпуска:
# проверить установку на чистой машине, замерить запуск с ReadyToRun или
# размер выпуска можно и без подписи, версии и чистого дерева. Выкладывать
# собранное этим скриптом напрямую нельзя — выпуск собирает release.sh.
#
#   tools/build-deb.sh --version 0.1.1 --provisioning ~/.config/EliteSIP-release/provisioning.json
#
# Что где ляжет (docs/PORT-PLAN.md, W-L7):
#
#   /opt/elitesip/                        программа (root, оператор не пишет)
#     EliteSIP                            приложение — его зовут ярлык, автозапуск, служба
#     elitesip-updater                    обновляльщик — его зовёт elitesip-updater.service
#     provisioning.json                   заводская настройка: канал и ключи
#   /usr/share/applications/elitesip.desktop
#   /etc/xdg/autostart/elitesip.desktop   автозапуск для всех, выключается у себя
#   /usr/lib/systemd/user/elitesip.service
#   /usr/lib/systemd/system/elitesip-updater.{service,timer,path}
#   /usr/share/icons/hicolor/*/apps/elitesip.png
#   /var/lib/elitesip/{updates,updater}   создаёт postinst

set -euo pipefail

root=$(cd "$(dirname "$0")/.." && pwd)

version=
provisioning=
output="$root/artifacts"
source=
maintainer="EliteSIP"

# Параллельность сборки. По умолчанию два узла: ReadyToRun на всех ядрах съедал
# память машины разработки (7,6 ГБ без подкачки) целиком — 22 октября 2026 она
# зависла посреди публикации.
jobs=${ELITESIP_BUILD_JOBS:-2}

usage() {
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    echo "ключи: --version X.Y.Z --provisioning ФАЙЛ [--output КАТАЛОГ] [--source ИСТОЧНИК_NUGET] [--maintainer ИМЯ]"
    exit 2
}

fail() {
    echo "пакет не собран: $*" >&2
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) version=$2; shift 2 ;;
        --provisioning) provisioning=$2; shift 2 ;;
        --output) output=$2; shift 2 ;;
        --source) source=$2; shift 2 ;;
        --maintainer) maintainer=$2; shift 2 ;;
        *) usage ;;
    esac
done

[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage
[ -n "$provisioning" ] || usage

# Заводская настройка обязательна, как и в release.ps1: собранный без неё
# пакет ставится и работает, а мёртвую линию панели видно только на чистой
# машине.
[ -f "$provisioning" ] || fail "нет заводской настройки $provisioning"

command -v dpkg-deb >/dev/null || fail "нет dpkg-deb"

stage="$root/packaging/build/elitesip_$version"
program="$stage/opt/elitesip"
rm -rf "$stage"
mkdir -p "$program" "$output"

# --- Публикация -------------------------------------------------------------
#
# Самодостаточная, с ReadyToRun (план, W-L7, «Рантайм»): на Mint 22 зависимая
# публикация означала бы репозиторий Microsoft или PPA с .NET 10 на каждой
# машине — ещё один источник пакетов, который обновляется мимо нашей линии.
#
# Версия — ключом для всего дерева, а не только из Directory.Build.props: так
# её получают и общие проекты из подмодуля (у них свой Directory.Build.props
# с версией Windows). Версию приложения общий код берёт у входной сборки
# (ProductVersion), но у всех сборок в /opt/elitesip она должна быть одна.
#
# DisableTransitiveFrameworkReferenceDownloads: иначе публикация под linux-x64
# тянет пакет рантайма ASP.NET, которого здесь никто не использует.
#
# --disable-build-servers: сервер компилятора остался бы жить после сборки
# и держать сотни мегабайт.

publish() {
    local project=$1
    local args=(
        publish "$root/$project"
        --configuration Release
        --runtime linux-x64
        --self-contained true
        --output "$program"
        --nologo
        --disable-build-servers
        -maxcpucount:"$jobs"
        -p:Version="$version"
        -p:PublishReadyToRun=true
        -p:DisableTransitiveFrameworkReferenceDownloads=true
        -p:NuGetAudit=false
    )
    if [ -n "$source" ]; then
        args+=(--source "$source")
    fi

    dotnet "${args[@]}" || fail "не опубликовался $project"
}

echo "== публикация $version"
publish src/EliteSIP.App.Linux/EliteSIP.App.Linux.csproj

# Обновляльщик — в тот же каталог: служба зовёт его по пути внутри
# /opt/elitesip, рядом с заводской настройкой и ключом линии. Рантайм у них
# общий и одной версии.
publish src/EliteSIP.Updater.Linux/EliteSIP.Updater.Linux.csproj

# Короткие имена. Исполняемый файл .NET знает свою сборку по имени, вписанному
# при сборке, а не по своему, — переименовать его можно. Имя процесса ядро
# берёт у файла: «EliteSIP» по нему ищет обновляльщик (аварийный путь).
mv "$program/EliteSIP.App.Linux" "$program/EliteSIP"
mv "$program/EliteSIP.Updater.Linux" "$program/elitesip-updater"

install -m 0644 "$provisioning" "$program/provisioning.json"

# --- Раскладка по системе ---------------------------------------------------

echo "== раскладка"
install -D -m 0644 "$root/packaging/applications/elitesip.desktop" "$stage/usr/share/applications/elitesip.desktop"
install -D -m 0644 "$root/packaging/autostart/elitesip.desktop" "$stage/etc/xdg/autostart/elitesip.desktop"
install -D -m 0644 "$root/packaging/systemd/elitesip.service" "$stage/usr/lib/systemd/user/elitesip.service"
for unit in elitesip-updater.service elitesip-updater.timer elitesip-updater.path; do
    install -D -m 0644 "$root/packaging/systemd/$unit" "$stage/usr/lib/systemd/system/$unit"
done

# Значок — тот же, что у Windows-версии, всех размеров, что есть.
for size in 16 32 128 256 512; do
    install -D -m 0644 "$root/shared/src/EliteSIP.App/Assets/AppIcon/icon_${size}x${size}.png" \
        "$stage/usr/share/icons/hicolor/${size}x${size}/apps/elitesip.png"
done

# Права — явно и на всё дерево: публикация и mkdir оставляют то, что дал
# Документация пакета: copyright и changelog — то, что dpkg и администратор
# ищут в /usr/share/doc/<пакет>. В журнале изменений — из какого коммита и
# какого общего кода собран пакет: по нему же видно, что именно стоит на
# машине, без доступа к Git.
doc="$stage/usr/share/doc/elitesip"
install -D -m 0644 "$root/packaging/debian/copyright" "$doc/copyright"
{
    echo "elitesip ($version) noble; urgency=medium"
    echo
    echo "  * Выпуск EliteSIP $version для Linux Mint 22 / Ubuntu 24.04."
    echo "  * Коммит: $(git -C "$root" describe --tags --always --dirty 2>/dev/null || echo неизвестен)."
    echo "  * Общий код: $(git -C "$root/shared" describe --tags --long --always --dirty 2>/dev/null || echo неизвестен)."
    echo
    echo " -- $maintainer  $(LC_ALL=C date -R)"
} | gzip -9n > "$doc/changelog.gz"
chmod 0644 "$doc/changelog.gz"

# umask сборщика (0775 на /opt в пакете — это /opt, открытый группе, на
# машине, где каталога ещё не было). createdump рантайм запускает при падении.
find "$stage" -type d -exec chmod 0755 {} +
find "$program" -type f -exec chmod 0644 {} +
chmod 0755 "$program/EliteSIP" "$program/elitesip-updater" "$program/createdump"

# --- Пакет ------------------------------------------------------------------

echo "== пакет"
mkdir -p "$stage/DEBIAN"
install -m 0755 "$root/packaging/debian/postinst" "$root/packaging/debian/prerm" "$root/packaging/debian/postrm" "$stage/DEBIAN/"
install -m 0644 "$root/packaging/debian/conffiles" "$stage/DEBIAN/"

size=$(du -sk --exclude=DEBIAN "$stage" | cut -f1)
sed -e "s/@VERSION@/$version/" -e "s/@MAINTAINER@/$maintainer/" -e "s/@SIZE@/$size/" \
    "$root/packaging/debian/control" > "$stage/DEBIAN/control"

package="$output/EliteSIP-$version.deb"

# --root-owner-group: всё в пакете — root, без fakeroot. xz: пакет едет по
# каналу конторы на каждую машину, и каждый мегабайт там — секунды.
dpkg-deb --root-owner-group -Zxz --build "$stage" "$package" >/dev/null || fail "dpkg-deb отказал"

echo "  пакет:   $package"
echo "  размер:  $(du -h "$package" | cut -f1) (распакованный — $((size / 1024)) МБ)"

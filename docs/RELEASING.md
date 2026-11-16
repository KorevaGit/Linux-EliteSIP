# Выпуск независимого Linux-клиента

Все исходники, ресурсы и ReleaseKit находятся в Linux-репозитории. Для сборки
достаточно его обычного клона, .NET SDK из global.json и зависимостей Linux.
Доступ к Windows-репозиторию, подмодули и Windows-метки не требуются.

- Сборка: `dotnet build EliteSIP.Linux.slnx -c Release`.
- Тесты: `dotnet test EliteSIP.Linux.slnx -c Release`.
- Пакет: `tools/build-deb.sh --version X.Y.Z --provisioning /путь/provisioning.linux.json`.
- Выпуск: `tools/release.sh --version X.Y.Z --signing-key /путь/linux-releases.key`.
  Скрипт проверяет чистоту Linux-дерева, рост версии, сборку, тесты, подпись и
  создаёт локальные коммит/метку. Ничего автоматически не отправляет.
- Выкладка выполняется отдельно: `tools/publish.sh --version X.Y.Z --dry-run`,
  затем без `--dry-run`; `--verify-only` проверяет опубликованный канал.

## Настройка публикации

Файл вне Git: `~/.config/EliteSIP-release/publish.local.json` (либо `--config`).
В объекте `update` нужны `baseURL`, `basicUser`, `basicPassword`, `adminToken`.
Настоящие пароли и ключи в репозиторий не копируются. Файл заводской настройки
и отдельный ключ Linux передаются существующим скриптам выпуска. Параметры
скриптов и проверка разделения ключей Windows/Linux сохраняются.

Историческое происхождение исходников: `core/ORIGIN.md`. Последующие выпуски
идентифицируются версией и ревизией Linux-репозитория.

## Пакет для отладки

`tools/build-deb.sh --version 0.1.7 --configuration Debug --provisioning /путь/provisioning.linux.json`
создаёт `artifacts/EliteSIP-0.1.7-Debug.deb`: Debug-сборки приложения и
обновляльщика, переносимые PDB, без ReadyToRun. По умолчанию скрипт продолжает
собирать Release. Пакет использует обычные пути установки EliteSIP и заменяет
установленный клиент; это не параллельная установка. Автоматическая публикация
не выполняется. Заводская настройка содержит секреты: пакет предназначен для
доверенного тестового рабочего места.

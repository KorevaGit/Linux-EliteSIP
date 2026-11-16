# EliteSIP для Linux

Самостоятельный клиент на .NET 10 и Avalonia, со звуком PulseAudio/PipeWire.
Все исходники, ресурсы, тесты и инструменты находятся в этом репозитории.
Подмодулей и зависимости сборки от Windows-репозитория нет.

## Сборка и проверки

Нужен .NET SDK из `global.json`. Версия Linux задаётся в корневом
`Directory.Build.props`, настройки наследуются всеми локальными проектами.

```bash
git clone <адрес Linux-репозитория>
cd <каталог клона>
dotnet build EliteSIP.Linux.slnx -c Release
dotnet test EliteSIP.Linux.slnx -c Release
```

GUI-тесты используют Avalonia Headless, аудиопроверки не требуют настоящей
гарнитуры. Проверки на реальном рабочем месте выполняются отдельно.
Зависимости установленного приложения перечислены в `packaging/debian/control`.
Выпуск и публикация описаны в [docs/RELEASING.md](docs/RELEASING.md).

## Структура

- `src/` — приложение Avalonia, звук PulseAudio, оболочка Linux и обновляльщик.
- `core/src/` — собственные библиотеки SIP, RTP, аудио, истории, настроек и Spark.
- `tests/` и `core/tests/` — проверки приложения, платформы и библиотек.
- `tools/` — Linux-стенды и скрипты сборки, выпуска и публикации.
- `core/tools/ReleaseKit/` — локальная подпись и проверка манифестов.
- `packaging/` — `.deb`, systemd и ярлыки.

`core/` — обычные исходники Linux, их можно менять здесь. Историческое имя
`core/src/EliteSIP.App/` обозначает только сохранённые ресурсы, а не WPF-проект.
Происхождение импорта записано в [core/ORIGIN.md](core/ORIGIN.md).
Автоматическая синхронизация кода с Windows не выполняется.

Архитектурное решение: [ADR-0001](docs/adr/0001-independent-linux-codebase.md).
План функционального переноса: [матрица требований](docs/migration/2026-11-14-windows-parity.md).
Результат отделения: [отчёт этапа 2](docs/migration/2026-11-14-independent-code.md).
Результат переноса backend: [отчёт этапа 3](docs/migration/2026-11-16-backend-port.md).
Результат переноса Spark: [отчёт этапа 4](docs/migration/2026-11-16-spark-port.md).
Результат переноса интерфейса: [отчёт этапа 5](docs/migration/2026-11-16-ui-port.md).

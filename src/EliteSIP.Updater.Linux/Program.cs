using EliteSIP.Updater;

// Обновляльщик Linux-версии EliteSIP: ставит выпуск от имени root.
//
// ------------------------------------------------------------------------------
// Зачем он вообще нужен
// ------------------------------------------------------------------------------
//
// Оператор — обычный пользователь без root, программу ставит администратор
// (`sudo apt install`) в `/opt/elitesip`. Писать туда оператор не может, и
// приложение не может обновить себя само ни при каком раскладе. Ставит этот
// код: служба `elitesip-updater.service` от root, которую будят таймер раз в
// десять минут и путь на отметку о согласии (`packaging/systemd/`). Аргументов
// у службы нет, как и у задачи планировщика на Windows: аргументы от
// вызывающего означали бы выбор, что делать с правами root.
//
// Сценарий и его правила — `UpdaterRun` и общий `EliteSIP.Updates`; разбор,
// кому здесь верят, — у `Layout` и `SharedFile`.

if (!Environment.IsPrivilegedProcess)
{
    Console.Error.WriteLine("elitesip-updater работает от root, службой elitesip-updater.service");
    return 1;
}

var layout = Layout.Installed;

// Свой каталог — до журнала: журнал лежит в нём. Права ставятся явно, а не
// только при создании: каталог, созданный когда-то с другими правами, иначе
// таким бы и остался. Обычно его уже создал systemd (`StateDirectory=`).
try
{
    Directory.CreateDirectory(layout.Trusted);
    File.SetUnixFileMode(layout.Trusted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"свой каталог {layout.Trusted} недоступен: {error.Message}");
    return 1;
}

var file = new UpdaterLog(layout.Log);

// И в свой журнал, и в журнал службы: `journalctl -u elitesip-updater`
// — первое, куда посмотрит администратор, а файл переживает ротацию журнала.
void log(string message)
{
    file.Write(message);
    Console.WriteLine(message);
}

// Вторая копия посреди первой — два решения об одной установке. systemd и
// так не запустит службу, которая уже работает; замок — вторая линия. Он в
// `/run`, куда пишет только root, — оператор не займёт его заранее, как
// занимал мьютекс на Windows до 0.1.67.
FileStream instance;
try
{
    instance = new FileStream(layout.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
catch (IOException)
{
    return 0;
}

using (instance)
{
    try
    {
        return await new UpdaterRun(layout, new SystemdMachine(layout), Http.Client, log, () => DateTime.UtcNow)
            .RunAsync()
            .ConfigureAwait(false);
    }
    catch (Exception error)
    {
        log($"неожиданный отказ: {error}");
        return 1;
    }
}

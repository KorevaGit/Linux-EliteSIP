using Avalonia;
using EliteSIP.App.Settings;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App;

/// <summary>
/// Точка входа: одна копия и передача запуска службе — до Avalonia.
/// </summary>
///
/// <remarks>
/// <para>
/// Вторая копия только будит первую и выходит, и делать это стоит раньше,
/// чем поднимется X-соединение и Skia: двойной щелчок по ярлыку не должен
/// стоить полсекунды и мигания окна.
/// </para>
/// <para>
/// Запущенный не службой, а служба установлена (ярлык меню, автозапуск XDG),
/// — отпускает замок и просит systemd запустить её: работать должна копия
/// под присмотром, которую поднимут после падения (<see cref="UserService"/>).
/// </para>
/// </remarks>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var instance = SingleInstance.Claim(EarlyLog);
        if (instance is null)
        {
            EarlyLog("запуск: софтфон уже работает — поднят его экран, этот экземпляр выходит");
            return 0;
        }

        if (!args.Contains("--no-service"))
        {
            instance.Dispose();
            if (UserService.TryHandOver(EarlyLog))
            {
                EarlyLog("запуск: передан службе elitesip.service");
                return 0;
            }

            // Службы нет или systemd отказал — работаем здесь. Замок берётся
            // снова: между отпусканием и этим местом мог успеть кто-то ещё.
            instance = SingleInstance.Claim(EarlyLog);
            if (instance is null)
            {
                return 0;
            }
        }

        return BuildAvaloniaApp()
            .AfterSetup(builder => ((App)builder.Instance!).Instance = instance)
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Построитель приложения; его же берёт дизайнер разметки.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    /// <summary>Строка в журнал до настроек — так же, как EarlyLog у Windows.</summary>
    private static void EarlyLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(AppSettings.DefaultPath)!;
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "elitesip.log"),
                DateTimeOffset.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)
                    + " " + message + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

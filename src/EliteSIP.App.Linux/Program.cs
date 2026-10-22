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
        // Каталог профиля — до первой строки журнала: иначе её запись создала
        // бы каталог открытым (ProfilePrivacy).
        var narrowed = ProfilePrivacy.Secure(Path.GetDirectoryName(AppSettings.DefaultPath)!);
        if (narrowed.Count > 0)
        {
            EarlyLog($"запуск: профиль закрыт от других пользователей — сужены права: {string.Join(", ", narrowed)}");
        }

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
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new X11PlatformOptions
        {
            EnableDrawnDecorations = DrawsOwnFrame,
            ForceDrawnDecorations = DrawsOwnFrame,
        });

    /// <summary>
    /// Рамку и полосу заголовка рисует само приложение, а не диспетчер окон.
    /// </summary>
    ///
    /// <remarks>
    /// <para>
    /// Полосу заголовка Muffin рисует своей темой (Mint-Y — тёмная), и окно с
    /// палитрой продукта выглядело собранным из двух половин — то, что на
    /// Windows лечит <c>SystemCaption</c>. Перекрасить чужую полосу на X11
    /// нечем: свойство <c>_GTK_THEME_VARIANT</c> выбирает только светлый или
    /// тёмный вариант темы диспетчера, а не цвет. Поэтому рамку рисует
    /// Avalonia: полоса — цвет фона окна (кисти <c>TitleBarBackgroundBrush</c> и
    /// <c>CaptionButton*</c> в палитре), кнопки свернуть, развернуть и закрыть —
    /// её, перетаскивание и изменение размера отдаются диспетчеру
    /// (<c>_NET_WM_MOVERESIZE</c>), так что прилипание к краям экрана остаётся.
    /// </para>
    /// <para>
    /// <b>В Avalonia 12.1 это помечено экспериментальным</b>
    /// (<c>AVALONIA_X11_CSD</c>), поэтому есть выключатель: переменная
    /// <c>ELITESIP_SYSTEM_FRAME=1</c> возвращает рамку диспетчера окон — без
    /// пересборки, если на каком-то рабочем столе рисованная рамка поведёт себя
    /// не так. Для службы — <c>systemctl --user set-environment</c> или
    /// <c>Environment=</c> в <c>elitesip.service.d</c>.
    /// </para>
    /// </remarks>
    internal static bool DrawsOwnFrame { get; } =
        Environment.GetEnvironmentVariable("ELITESIP_SYSTEM_FRAME") is not "1";

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

using Avalonia;

namespace WindowProbe;

// Стенд W-L0Б/В. Без ключей — только показ окна и показания, курсор не
// трогается. --inject — синтетические движения и щелчок через XTest (курсор
// сдвинется и вернётся). --human N — N секунд ждать настоящего щелчка.
internal static class Program
{
    internal static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        _ = Xlib.XInitThreads();
        return AppBuilder.Configure<ProbeApp>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
    }
}

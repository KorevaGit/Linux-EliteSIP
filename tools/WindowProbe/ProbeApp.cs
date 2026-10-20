using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Themes.Simple;
using Avalonia.Threading;

namespace WindowProbe;

internal sealed class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var probe = new Probe(desktop);
            Dispatcher.UIThread.Post(probe.Run);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

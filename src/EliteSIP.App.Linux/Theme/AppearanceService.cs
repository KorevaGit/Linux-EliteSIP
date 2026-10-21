using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Theme;

/// <summary>
/// Светлая или тёмная тема и акцент — Linux-половина <c>Theme/AppearanceService</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Выбор оператора — тот же: «как в системе», светлая, тёмная. «Как в
/// системе» читается у портала (<see cref="DesktopAppearance"/>, W-L4); нет
/// портала или предпочтения — светлая, как решено в плане.
/// </para>
/// <para>
/// Акцент системы перекрашивает те же четыре кисти, что на Windows:
/// <c>AccentBrush</c>, <c>SelectionBrush</c> (тот же цвет на 0x4C),
/// <c>StatusInCallBrush</c>, <c>AccentFillBrush</c>. В тёмной теме акцент
/// осветляется на 35 % — то, что Windows делала с цветом DWM без светлой
/// ступени (<c>SystemAccent.Mix</c>): портал отдаёт один цвет на обе темы.
/// Кисти кладутся прямо в ресурсы приложения — они перекрывают оба варианта
/// палитры.
/// </para>
/// </remarks>
internal sealed class AppearanceService : IDisposable
{
    private readonly Application _application;
    private readonly DesktopAppearance _desktop;
    private Appearance _appearance;

    internal AppearanceService(Application application, DesktopAppearance desktop)
    {
        _application = application;
        _desktop = desktop;
        _desktop.Changed += OnDesktopChanged;
    }

    internal Appearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value;
            Apply();
        }
    }

    internal bool IsDark => _appearance switch
    {
        Appearance.Dark => true,
        Appearance.Light => false,
        _ => _desktop.PrefersDark,
    };

    internal void Apply()
    {
        _application.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Tint();
    }

    public void Dispose() => _desktop.Changed -= OnDesktopChanged;

    private void OnDesktopChanged() => Dispatcher.UIThread.Post(Apply);

    private void Tint()
    {
        var resources = _application.Resources;
        string[] keys = ["AccentBrush", "SelectionBrush", "StatusInCallBrush", "AccentFillBrush"];

        if (_desktop.Accent is not { } accent)
        {
            foreach (var key in keys)
            {
                resources.Remove(key);
            }

            return;
        }

        var fill = Color.FromRgb(accent.Red, accent.Green, accent.Blue);
        var shown = IsDark ? Mix(fill, Colors.White, 0.35) : fill;

        resources["AccentBrush"] = new SolidColorBrush(shown);
        resources["SelectionBrush"] = new SolidColorBrush(Color.FromArgb(0x4C, shown.R, shown.G, shown.B));
        resources["StatusInCallBrush"] = new SolidColorBrush(shown);
        resources["AccentFillBrush"] = new SolidColorBrush(fill);
    }

    private static Color Mix(Color under, Color over, double amount) => Color.FromRgb(
        (byte)Math.Round(under.R + ((over.R - under.R) * amount)),
        (byte)Math.Round(under.G + ((over.G - under.G) * amount)),
        (byte)Math.Round(under.B + ((over.B - under.B) * amount)));
}

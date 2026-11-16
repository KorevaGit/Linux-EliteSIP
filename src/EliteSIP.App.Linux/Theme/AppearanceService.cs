using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using EliteSIP.Platform.Linux;
namespace EliteSIP.App.Theme;
internal sealed class AppearanceService : IDisposable
{
    private readonly Application _application;
    private readonly DesktopAppearance _desktop;
    private Appearance _appearance;
    private bool _applied;
    internal AppearanceService(Application application, DesktopAppearance desktop)
    { _application=application;_desktop=desktop;_desktop.Changed+=OnDesktopChanged; }
    internal Appearance Appearance
    {
        get=>_appearance;
        set { if(_applied && _appearance==value) { return; }_appearance=value;Apply(); }
    }
    internal bool IsDark=>_appearance switch { Appearance.Dark=>true,Appearance.Light=>false,_=>_desktop.PrefersDark };
    internal void Apply()
    {
        var variant=IsDark?ThemeVariant.Dark:ThemeVariant.Light;
        if(_application.RequestedThemeVariant!=variant) { _application.RequestedThemeVariant=variant; }
        _applied=true;WindowBackgroundService.Current?.Apply();
    }
    private void OnDesktopChanged()=>Dispatcher.UIThread.Post(Apply);
    public void Dispose()=>_desktop.Changed-=OnDesktopChanged;
}

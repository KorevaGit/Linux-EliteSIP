using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EliteSIP.App.Theme;

/// <summary>Windows splash artwork shown before settings, desktop probes and audio initialization.</summary>
internal sealed class StartupSplash : Window, IDisposable
{
    private readonly Bitmap _art;
    private bool _disposed;
    internal StartupSplash(string? settingsJson = null, bool systemDark = false)
    {
        Width=300;Height=210;CanResize=false;ShowInTaskbar=false;ShowActivated=false;
        WindowDecorations=WindowDecorations.None;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Brushes.Transparent;TransparencyLevelHint=[WindowTransparencyLevel.Transparent];
        using var stream=AssetLoader.Open(new Uri("avares://EliteSIP.App.Linux/"+ResourceFor(settingsJson,systemDark)));
        _art=new Bitmap(stream);
        Content=new Image {Source=_art,Stretch=Stretch.Fill};
        Closed+=(_,_)=>Dispose();
    }
    internal static StartupSplash Begin(Action initialize, string? settingsJson = null, bool? systemDark = null)
    {
        string? json=settingsJson;
        try
        {
            var file=new FileInfo(Settings.AppSettings.DefaultPath);
            if(json is null&&file.Exists&&file.Length<=1024*1024) {json=File.ReadAllText(file.FullName);}
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { }
        var dark=systemDark ?? Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant==PlatformThemeVariant.Dark;
        var splash=new StartupSplash(json,dark);
        splash.Show();
        // Start after the first compositor frame. A normal-priority fallback
        // avoids idle-priority starvation (or a hidden/unavailable compositor).
        var started=false;
        DispatcherTimer? fallback=null;
        void InitializeOnce()
        {
            if(started) {return;}
            started=true;fallback?.Stop();
            try {initialize();}
            catch {splash.Close();throw;}
        }
        fallback=new DispatcherTimer(TimeSpan.FromMilliseconds(250),DispatcherPriority.Normal,(_,_)=>InitializeOnce());
        fallback.Start();
        splash.RequestAnimationFrame(_=>Dispatcher.UIThread.Post(InitializeOnce,DispatcherPriority.Normal));
        return splash;
    }
    internal void DismissWhenShown(Window destination)
    {
        if(destination.IsVisible) {Close();return;}
        void Ready(object? sender,EventArgs args) {destination.Opened-=Ready;Close();}
        destination.Opened+=Ready;
    }
    internal void UseWallpaper()
    {
        if(WindowBackgroundService.Current?.Image is null) {return;}
        Width=440;Height=280;
        var stack=new StackPanel {Spacing=12,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
        stack.Children.Add(new BrandMark {Width=108,Height=108,Foreground=Brush.Parse("#D8C6A5")});
        var label=new TextBlock {Text="EliteSIP",FontSize=25,FontWeight=FontWeight.SemiBold,HorizontalAlignment=HorizontalAlignment.Center};
        label.Bind(TextBlock.ForegroundProperty,this.GetResourceObservable("TextPrimaryBrush"));stack.Children.Add(label);
        var frame=new Border {Child=stack};frame.Bind(Border.BackgroundProperty,this.GetResourceObservable("WindowBackgroundBrush"));Content=frame;
    }
    public void Dispose()
    {
        if (_disposed) {return;}
        _disposed=true;
        if(IsVisible) {Close();}
        _art.Dispose();GC.SuppressFinalize(this);
    }
    internal static string ResourceFor(string? json,bool systemDark)
    {
        var dark=systemDark;
        try
        {
            if(json is not null)
            {
                using var doc=JsonDocument.Parse(json);
                if(doc.RootElement.ValueKind==JsonValueKind.Object&&doc.RootElement.TryGetProperty("Appearance",out var appearance)&&appearance.ValueKind==JsonValueKind.Object&&appearance.TryGetProperty("Theme",out var theme))
                {dark=theme.ToString().ToUpperInvariant() switch {"DARK" or "2" or "WALLPAPER" or "3"=>true,"LIGHT" or "1"=>false,_=>systemDark};}
            }
        }
        catch(JsonException) { }
        return dark?"Assets/Splash.png":"Assets/Splash.Light.png";
    }
}

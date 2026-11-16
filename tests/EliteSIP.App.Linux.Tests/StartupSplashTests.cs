using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

public sealed class StartupSplashTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Splash_renders_before_initialization_and_stays_until_destination_opens(bool dark)
    {
        StartupSplash? splash=null;
        var initialized=false;var visibleDuringInitialization=false;
        splash=StartupSplash.Begin(()=> {initialized=true;visibleDuringInitialization=splash!.IsVisible;},dark?"{\"Appearance\":{\"Theme\":\"Dark\"}}":"{\"Appearance\":{\"Theme\":\"Light\"}}",!dark);
        using var lifetime=splash;
        Assert.True(splash.IsVisible);Assert.False(initialized);
        await Task.Delay(350);Dispatcher.UIThread.RunJobs();
        Assert.True(initialized);Assert.True(visibleDuringInitialization);Assert.True(splash.IsVisible);
        Assert.False(splash.ShowInTaskbar);Assert.Equal(WindowDecorations.None,splash.WindowDecorations);
        Snapshot.Save(splash.CaptureRenderedFrame()!,"startup-splash-"+(dark?"dark":"light"));
        var destination=new Window();splash.DismissWhenShown(destination);
        Assert.True(splash.IsVisible);destination.Show();Dispatcher.UIThread.RunJobs();
        Assert.False(splash.IsVisible);Assert.True(destination.IsVisible);destination.Close();
    }

    [Theory]
    [InlineData("{\"Appearance\":{\"Theme\":3}}",false,"Assets/Splash.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"Wallpaper\"}}",false,"Assets/Splash.png")]
    [InlineData("invalid",true,"Assets/Splash.png")]
    [InlineData("{\"Appearance\":{\"Theme\":\"Light\"}}",true,"Assets/Splash.Light.png")]
    public void Early_theme_matches_Windows_without_loading_full_settings(string json,bool systemDark,string expected)
        =>Assert.Equal(expected,StartupSplash.ResourceFor(json,systemDark));
}

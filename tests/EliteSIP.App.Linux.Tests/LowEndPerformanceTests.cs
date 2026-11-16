using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EliteSIP.App.Platform;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;
public sealed class LowEndPerformanceTests
{
    [AvaloniaFact]
    public async Task Appearance_saves_coalesce_and_flush_on_exit()
    {
        var writes=0;
        using var save=new DeferredSettingsSave();
        for(var i=0;i<50;i++) {save.Schedule(()=>writes++);}
        Assert.Equal(0,writes);
        await Task.Delay(450);Dispatcher.UIThread.RunJobs();Assert.Equal(1,writes);
        save.Schedule(()=>writes++);save.Dispose();Assert.Equal(2,writes);
        await Task.Delay(450);Dispatcher.UIThread.RunJobs();Assert.Equal(2,writes);
    }

    [AvaloniaFact]
    public async Task Minimized_settings_stop_meters_without_aborting_self_test_and_resume_on_restore()
    {
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        using var appearance=new AppearanceService(app,new DesktopAppearance(_=>{})) {Appearance=Appearance.Dark};
        var polls=0;var stops=0;
        var audio=new TestAudioPlatform();
        var model=new SettingsViewModel(new AppSettings(),new AvaloniaUiThread(),audio,new TestAutoStart())
        {Section=SettingsSectionKind.Audio,ReadMeters=()=>{polls++;return new(true,false,null);},OnStopSelfTest=()=>stops++};
        var window=new SettingsWindow(model,appearance,audio.CreateSignalSounds(_=>{}));
        try
        {
            window.Show();await Task.Delay(150);Dispatcher.UIThread.RunJobs();Assert.True(polls>0);
            window.WindowState=WindowState.Minimized;var before=polls;var stopped=stops;
            await Task.Delay(180);Dispatcher.UIThread.RunJobs();Assert.Equal(before,polls);Assert.Equal(stopped,stops);
            window.WindowState=WindowState.Normal;await Task.Delay(150);Dispatcher.UIThread.RunJobs();Assert.True(polls>before);
        }
        finally {window.Close();app.RequestedThemeVariant=old;}
    }
}

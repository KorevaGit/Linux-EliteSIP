using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Platform;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

public sealed class AudioDesignTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Processing_and_self_test_match_Windows_geometry_and_record_stop_state(bool dark)
    {
        using var errors=new BindingErrors();
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        using var appearance=new AppearanceService(app,new DesktopAppearance(_=>{})) { Appearance=dark?Appearance.Dark:Appearance.Light };
        var settings=new AppSettings();settings.Audio.NoiseSuppression=true;settings.Audio.BackgroundVoiceSuppression=true;
        var model=new SettingsViewModel(settings,new AvaloniaUiThread(),new TestAudioPlatform(),new TestAutoStart()) { Section=SettingsSectionKind.Audio };
        var window=new SettingsWindow(model,appearance,new TestAudioPlatform().CreateSignalSounds(_=>{}));window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var illustrations=window.GetVisualDescendants().OfType<ProcessingPreview>().ToArray();
            Assert.Equal(4,illustrations.Length);
            Assert.All(illustrations,preview=> { Assert.Equal(102,preview.Width);Assert.Equal(24,preview.Height); });
            Assert.Equal("Noise",illustrations[2].Kind);Assert.Equal("Voices",illustrations[3].Kind);
            var sections=window.GetVisualDescendants().OfType<SettingsSection>().ToArray();
            var processing=sections.Single(section=>Equals(section.Header,Strings.Get("SettingsProcessing")));
            processing.BringIntoView();Dispatcher.UIThread.RunJobs();Capture(processing,"processing-"+dark);
            var self=sections.Single(section=>Equals(section.Header,Strings.Get("SettingsSelfTest")));
            self.BringIntoView();Dispatcher.UIThread.RunJobs();
            var trace=Assert.Single(self.GetVisualDescendants().OfType<RecordingWaveform>());
            Assert.Equal(25,trace.Height);Assert.NotNull(trace.Foreground);Assert.NotNull(trace.TrackBrush);
            var record=window.FindControl<Button>("SelfTestRecordButton")!;
            var icon=Assert.Single(record.GetVisualDescendants().OfType<Icon>());
            Assert.Equal("settings.circle",icon.Glyph);Capture(self,"self-test-idle-"+dark);
            model.ReportSelfTestRunning(true);Dispatcher.UIThread.RunJobs();Assert.Equal("settings.square",icon.Glyph);
            for(var sample=0;sample<24;sample++) {trace.Level=(sample%7+1)/10.0;}
            Capture(self,"self-test-recording-"+dark);
            model.ReportSelfTestRunning(false);Dispatcher.UIThread.RunJobs();Assert.Equal("settings.circle",icon.Glyph);
            Assert.All(self.GetVisualDescendants().OfType<LevelMeter>(),meter=> { Assert.NotNull(meter.ActiveBrush);Assert.NotNull(meter.TrackBrush); });
            Assert.Empty(errors.Errors);
        }
        finally {window.Close();app.RequestedThemeVariant=old;}
    }

    private static void Capture(Control control,string name)
    {
        using var image=new RenderTargetBitmap(PixelSize.FromSize(control.Bounds.Size,1),new Vector(96,96));
        image.Render(control);Snapshot.Save(image,name);
    }
}

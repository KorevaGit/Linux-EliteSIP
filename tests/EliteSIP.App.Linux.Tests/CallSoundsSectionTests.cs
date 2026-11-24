using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Platform;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

/// <summary>
/// Звуки набора и разговора — своим разделом сразу под мелодией, как на macOS,
/// а не строкой в разделе «Звук». Выключатель включён, когда звуки звучат.
/// </summary>
public sealed class CallSoundsSectionTests
{
    [AvaloniaFact]
    public void Раздел_стоит_под_мелодией_и_выключатель_гасит_служебные_звуки()
    {
        using var errors = new BindingErrors();
        var oldTheme = Application.Current!.RequestedThemeVariant;
        using var appearance = new AppearanceService(Application.Current!, new DesktopAppearance(_ => { }));
        var settings = new AppSettings();
        var model = new SettingsViewModel(settings, new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart()) { Section = SettingsSectionKind.Audio };
        var window = new SettingsWindow(model, appearance, new TestAudioPlatform().CreateSignalSounds(_ => { }));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var sections = window.GetVisualDescendants().OfType<SettingsSection>().Where(section => section.IsEffectivelyVisible).ToList();
            var melody = sections.FindIndex(section => Equals(section.Header, LinuxStrings.Get("SettingsMelody")));
            Assert.True(melody >= 0);
            var sounds = sections[melody + 1];
            Assert.Equal(LinuxStrings.Get("SettingsCallSounds"), sounds.Header);

            var toggle = Assert.Single(sounds.GetVisualDescendants().OfType<CheckBox>());
            // По умолчанию звуки звучат, как на macOS.
            Assert.True(toggle.IsChecked);
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.Audio.MutesSystemSounds);

            sounds.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            using var image = new RenderTargetBitmap(PixelSize.FromSize(sounds.Bounds.Size, 1), new Vector(96, 96));
            image.Render(sounds);
            Snapshot.Save(image, "call-sounds");
            Assert.Empty(errors.Errors);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = oldTheme;
            window.Close();
        }
    }
}

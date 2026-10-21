using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EliteSIP.App.Platform;
using EliteSIP.App.Settings;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

/// <summary>Окно настроек: все разделы открываются и привязки целы.</summary>
public sealed class SettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(SettingsSectionKind.Work)]
    [InlineData(SettingsSectionKind.Audio)]
    [InlineData(SettingsSectionKind.Ringtone)]
    [InlineData(SettingsSectionKind.Appearance)]
    [InlineData(SettingsSectionKind.Support)]
    public void Раздел_рисуется_без_ошибок_привязок(SettingsSectionKind section)
    {
        using BindingErrors errors = new();
        using TemporaryHome home = new();

        var settings = AppSettings.Load(home.SettingsPath);
        SettingsViewModel model = new(settings, new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart())
        {
            Section = section,
        };

        var appearance = new Theme.AppearanceService(Avalonia.Application.Current!, new DesktopAppearance(_ => { }));
        SettingsWindow window = new(model, appearance, new TestAudioPlatform().CreateSignalSounds(_ => { }));
        window.Show();

        Snapshot.Save(window.CaptureRenderedFrame()!, $"settings-{section}");
        window.Close();

        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
    }
}

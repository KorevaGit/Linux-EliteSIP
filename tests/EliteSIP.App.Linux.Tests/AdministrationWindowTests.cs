using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EliteSIP.AdminAccess;
using EliteSIP.App.Admin;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Tests;

/// <summary>«Управление»: все разделы открываются и привязки целы.</summary>
public sealed class AdministrationWindowTests
{
    public static TheoryData<AdminSectionKind> Sections => [.. Enum.GetValues<AdminSectionKind>()];

    [AvaloniaTheory]
    [MemberData(nameof(Sections))]
    public void Раздел_рисуется_без_ошибок_привязок(AdminSectionKind section)
    {
        using BindingErrors errors = new();
        using TemporaryHome home = new();

        var settings = AppSettings.Load(home.SettingsPath);
        var access = new AdminAccessState(settings.Admin.ToCredential());
        access.Unlock(string.Empty);

        AdministrationViewModel model = new(settings, access) { Section = section };
        AdministrationWindow window = new(model);
        window.Show();

        Snapshot.Save(window.CaptureRenderedFrame()!, $"admin-{section}");
        window.Close();

        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
    }
}

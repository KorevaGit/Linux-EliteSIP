using Avalonia.Headless.XUnit;
using EliteSIP.App.Platform;
using EliteSIP.AdminAccess;
using EliteSIP.App.Settings;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

public sealed class SparkWorkplaceTests
{
    [AvaloniaFact]
    public void Quick_choice_queues_last_value_but_server_changes_do_not_queue()
    {
        var settings = new AppSettings();
        settings.Panel.Mode = PanelMode.Managed;
        settings.Panel.PresetID = "preset";
        var changes = 0;
        var model = new SettingsViewModel(settings, new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart())
        { OnWorkplaceChanged = () => changes++ };
        model.WorkplaceSite = WorkplaceSite.Remote;
        Assert.Equal("remote", settings.Panel.PendingWorkFormat);
        model.WorkplaceSite = WorkplaceSite.Office;
        Assert.Equal("office", settings.Panel.PendingWorkFormat);
        Assert.Equal(2, changes);
        settings.Panel.PendingWorkFormat = string.Empty;
        settings.Account.Site = WorkplaceSite.Remote;
        Assert.Empty(settings.Panel.PendingWorkFormat);
    }

    [AvaloniaFact]
    public void Locked_site_blocks_choice_and_network_repair_until_admin_override_goes_manual()
    {
        var settings = new AppSettings();
        settings.Panel.Mode = PanelMode.Managed;
        settings.Panel.PresetID = "preset";
        settings.Panel.LockedWorkFormat = "office";
        var access = new AdminAccessState(AdminCredential.Create("test-password"));
        var repairs = 0;
        var model = new SettingsViewModel(settings, new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart())
        { Access = access, OnRepairNetwork = () => repairs++ };
        Assert.False(model.CanSwitchWorkplace);
        model.WorkplaceSite = WorkplaceSite.Remote;
        model.RepairNetwork.Execute(null);
        Assert.Equal(WorkplaceSite.Office, settings.Account.Site);
        Assert.Equal(0, repairs);
        access.Unlock("test-password");
        model.WorkplaceSite = WorkplaceSite.Remote;
        Assert.Equal(PanelMode.Manual, settings.Panel.Mode);
        Assert.Equal(WorkplaceSite.Remote, settings.Account.Site);
        Assert.Empty(settings.Panel.PendingWorkFormat);
    }

    [AvaloniaFact]
    public void Managed_mode_alone_does_not_claim_reachable_spark()
    {
        var settings = new AppSettings();
        settings.Panel.Mode = PanelMode.Managed;
        settings.Panel.InstallationID = "test-machine";
        settings.Panel.SetChannelKey("test-key");
        var model = new SettingsViewModel(settings, new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart());
        Assert.Equal(EliteSIP.App.Resources.Strings.Get("SupportLinkUnchecked"), model.LinkState);
        settings.Panel.ChannelAvailable = true;
        settings.Panel.LastContactAt = DateTimeOffset.UtcNow;
        Assert.Equal(EliteSIP.App.Resources.Strings.Get("SupportLinkOnline"), model.LinkState);
        settings.Panel.ChannelAvailable = false;
        Assert.Equal(EliteSIP.App.Resources.Strings.Get("SupportLinkUnavailable"), model.LinkState);
    }
}

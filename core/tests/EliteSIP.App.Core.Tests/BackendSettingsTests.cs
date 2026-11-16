using System.IO;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Tests;

public sealed class BackendSettingsTests
{
    [Theory]
    [InlineData(600, 3600)]
    [InlineData(120, 120)]
    [InlineData(1800, 1800)]
    public void Upgrade_changes_only_the_previous_standard_knock_interval(double before, double expected)
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-backend-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            var settings = new AppSettings { Revision = 2 };
            settings.PortKnock.RepeatIntervalSeconds = before;
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(expected, loaded.PortKnock.RepeatIntervalSeconds);
            loaded.PortKnock.RepeatIntervalSeconds = 180;
            loaded.Save(path);
            Assert.Equal(180, AppSettings.Load(path).PortKnock.RepeatIntervalSeconds);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Ringtone_and_trace_defaults_preserve_explicit_saved_choices()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-backend-defaults-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, "{}");
            var settings = AppSettings.Load(path);
            Assert.Equal(RingtoneOutput.CallDevice, settings.Ringtone.Output);
            Assert.True(settings.Maintenance.LogsSipTrace);
            settings.Ringtone.Output = RingtoneOutput.SystemDefault;
            settings.Maintenance.LogsSipTrace = false;
            settings.Save(path);
            var loaded = AppSettings.Load(path);
            Assert.Equal(RingtoneOutput.SystemDefault, loaded.Ringtone.Output);
            Assert.False(loaded.Maintenance.LogsSipTrace);
        }
        finally { directory.Delete(recursive: true); }
    }
}

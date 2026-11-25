using System.IO;
using System.Text.Json;
using EliteSIP.App.PanelLine;
using EliteSIP.App.Settings;
using EliteSIP.CallHistory;

namespace EliteSIP.App.Tests;

public sealed class MachineResetRecoveryTests
{
    [Fact]
    public void Reset_removes_pending_history_and_snapshots_before_reopening_database()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-reset-recovery-");
        try
        {
            var database = Path.Combine(directory.FullName, "history.db");
            var record = new CallRecord { CallId = "test", Direction = CallDirection.Incoming, Number = "172", ProfileId = Guid.NewGuid() };
            var recovery = Directory.CreateDirectory(database + ".recovery");
            File.WriteAllText(Path.Combine(recovery.FullName, record.Id.ToString("N") + ".json"), JsonSerializer.Serialize(record));
            File.WriteAllText(Path.Combine(recovery.FullName, record.Id.ToString("N") + ".json.tmp"), "partial");
            File.WriteAllText(database + ".pending", "pending");
            File.WriteAllText(database + ".pending.tmp", "partial");
            var unrelated = Path.Combine(directory.FullName, "keep.txt");
            File.WriteAllText(unrelated, "keep");
            var profile = Path.Combine(directory.FullName, Path.GetFileName(AppSettings.DefaultPath));
            var settings = new AppSettings();
            settings.Setup.IsCompleted = true;
            settings.Save(profile);
            File.WriteAllText(profile + AppSettings.UnreadableSuffix, "{");

            settings.DisableWrites();
            Assert.Empty(MachineReset.Wipe(directory.FullName));
            settings.Save(profile);
            settings.SeedRecoveryCopy(profile);
            Assert.False(File.Exists(profile));
            Assert.False(File.Exists(profile + AppSettings.RecoverySuffix));
            Assert.False(File.Exists(profile + AppSettings.UnreadableSuffix));
            Assert.False(Directory.Exists(recovery.FullName));
            Assert.False(File.Exists(database + ".pending"));
            Assert.False(File.Exists(database + ".pending.tmp"));
            Assert.True(File.Exists(unrelated));
            using var store = new CallHistoryStore(new(database));
            Assert.Empty(store.Records(new(record.ProfileId)));
        }
        finally { directory.Delete(recursive: true); }
    }
}

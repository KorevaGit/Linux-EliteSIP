namespace EliteSIP.Platform.Linux.Tests;

public sealed class UserServiceJournalTests
{
    [Fact]
    public async Task Missing_journalctl_is_reported_without_throwing()
    {
        Assert.Contains("недоступен", await UserServiceJournal.CollectAsync("/nonexistent/journalctl"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Journal_secrets_are_redacted_before_export()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-journal-test-");
        try
        {
            var executable = Path.Combine(directory.FullName, "journalctl");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nprintf '%s\\n' 'Authorization: Digest username=\"test\", response=\"control-secret\"'\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            var result = await UserServiceJournal.CollectAsync(executable);
            Assert.Contains("exit=0", result, StringComparison.Ordinal);
            Assert.DoesNotContain("control-secret", result, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Hanging_journal_collection_is_cancelled()
    {
        var directory = Directory.CreateTempSubdirectory("elitesip-journal-timeout-");
        try
        {
            var executable = Path.Combine(directory.FullName, "journalctl");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nexec sleep 30\n");
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            var result = await UserServiceJournal.CollectAsync(executable, TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("превышен", result, StringComparison.Ordinal);
        }
        finally { directory.Delete(recursive: true); }
    }
}

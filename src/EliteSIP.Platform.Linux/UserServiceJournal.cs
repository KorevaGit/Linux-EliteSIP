using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using EliteSIP.Diagnostics;

namespace EliteSIP.Platform.Linux;

/// <summary>Ограниченный по времени и объёму журнал службы пользователя для архива поддержки.</summary>
internal static class UserServiceJournal
{
    internal static Task<string> CollectAsync(string executable = "journalctl", TimeSpan? timeout = null, bool updater = false)
        => CollectAsync(updater
            ? ["--system", "--unit=elitesip-updater.service", "--unit=elitesip-install.service"]
            : ["--user", "--unit=elitesip.service"], "--since=-30days", executable, timeout);

    internal static Task<string> CollectNetworkAsync()
        => CollectAsync(["--system", "--unit=NetworkManager.service", "--unit=systemd-networkd.service"],
            "--since=-2days");

    internal static Task<string> CollectAudioAsync()
        => CollectAsync(["--user", "--unit=pipewire.service", "--unit=pipewire-pulse.service",
            "--unit=wireplumber.service", "--unit=pulseaudio.service"], "--since=-2days");

    private static async Task<string> CollectAsync(string[] units, string since, string executable = "journalctl", TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in units.Concat([since, "--lines=500", "--no-pager", "--output=short-iso"]))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        try
        {
            process.Start();
            var output = ReadLimitedAsync(process.StandardOutput, 1024 * 1024, deadline.Token);
            var errors = ReadLimitedAsync(process.StandardError, 16384, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, errors).ConfigureAwait(false);
                var text = $"journalctl exit={process.ExitCode}\n{await output.ConfigureAwait(false)}\n{await errors.ConfigureAwait(false)}";
                return string.Join(Environment.NewLine, text.Split('\n').Select(LogRedaction.Redact));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(output, errors).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                return "Журнал службы не собран: превышен срок ожидания.";
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Журнал службы недоступен: {error.GetType().Name}.";
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var keep = Math.Min(count, Math.Max(0, limit - result.Length));
            result.Append(buffer, 0, keep);
        }
        if (result.Length == limit) { result.Append("\n[журнал ограничен по объёму]"); }
        return result.ToString();
    }
}

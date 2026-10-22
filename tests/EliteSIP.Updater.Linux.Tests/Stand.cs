using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EliteSIP.PanelLink;
using EliteSIP.Updater;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace EliteSIP.Updater.Linux.Tests;

/// <summary>
/// Стенд одного такта: каталоги во временном, заводская настройка со своим
/// ключом, канал, который отдаёт подписанный манифест и пакет, и машина,
/// которая запоминает, о чём её просили.
/// </summary>
internal sealed class Stand : IDisposable
{
    internal const string Channel = "https://update.elitesip.vip:8081";

    private readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());

    internal Stand()
    {
        var root = Path.Combine(Path.GetTempPath(), "EliteSIP.Updater.Linux.Tests", Guid.NewGuid().ToString("N"));

        Layout = new Layout(
            Path.Combine(root, "opt"),
            Path.Combine(root, "updates"),
            Path.Combine(root, "updater"),
            Path.Combine(root, "updater.lock"));

        Directory.CreateDirectory(Layout.Program);
        Directory.CreateDirectory(Layout.Shared);
        Directory.CreateDirectory(Layout.Trusted);

        File.WriteAllText(Path.Combine(Layout.Program, "provisioning.json"), JsonSerializer.Serialize(new
        {
            updates = new { baseURL = Channel, user = "elitesip", password = "secret" },
            presetsPublicKey = "P8XaNYhYwDSSS+805amNpc84GGrnnWV+mgbvyPiWBIs=",
            releasesPublicKey = Convert.ToBase64String(_key.GeneratePublicKey().GetEncoded()),
        }));

        Http = new HttpClient(Server);
    }

    internal Layout Layout { get; }

    internal FakeChannel Server { get; } = new();

    internal FakeMachine Machine { get; } = new();

    internal HttpClient Http { get; }

    internal DateTime Now { get; set; } = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    internal List<string> Lines { get; } = [];

    internal string LogText => string.Join('\n', Lines);

    public void Dispose()
    {
        Http.Dispose();
        Directory.Delete(Path.GetDirectoryName(Layout.Trusted)!, recursive: true);
    }

    internal Task<int> RunAsync() =>
        new UpdaterRun(Layout, Machine, Http, Lines.Add, () => Now).RunAsync();

    /// <summary>Выкладывает выпуск на канал: пакет и подписанный манифест к нему.</summary>
    internal byte[] Publish(string version, string? url = null)
    {
        var package = Encoding.UTF8.GetBytes($"пакет {version} " + Guid.NewGuid());
        url ??= $"{Channel}/releases/linux/EliteSIP-{version}.deb";

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = 1,
            version,
            url,
            sha256 = Convert.ToHexString(SHA256.HashData(package)),
            size = package.Length,
            published_at = "2026-10-22T10:00:00Z",
            notes = string.Empty,
        }));

        Ed25519Signer signer = new();
        signer.Init(forSigning: true, _key);
        signer.BlockUpdate(payload, 0, payload.Length);

        Server.Files[$"{Channel}/releases/linux/current.json"] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signer.GenerateSignature()),
        }));
        Server.Files[url] = package;

        return package;
    }

    /// <summary>Оператор согласился — так, как это пишет приложение.</summary>
    internal void Consent(string version, DateTime? writtenUtc = null)
    {
        File.WriteAllText(Layout.Request, version);
        File.SetLastWriteTimeUtc(Layout.Request, writtenUtc ?? Now);
    }

    /// <summary>Снимок общего каталога: root в нём ничего не меняет.</summary>
    internal string SharedSnapshot() => string.Join('\n', Directory
        .GetFileSystemEntries(Layout.Shared, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => $"{path} {File.GetLastWriteTimeUtc(path).Ticks}"));
}

internal sealed class FakeChannel : HttpMessageHandler
{
    internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    internal List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);

        return Task.FromResult(Files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class FakeMachine : IMachine
{
    internal Version? Installed { get; set; } = new(0, 1, 0, 0);

    internal bool AppRunning { get; set; }

    internal bool Installing { get; set; }

    internal bool InstallStarts { get; set; } = true;

    internal bool SessionsAnswer { get; set; } = true;

    internal List<(string Package, string Log)> Installs { get; } = [];

    internal int Restarts { get; private set; }

    public Version? InstalledVersion() => Installed;

    public bool IsAppRunning() => AppRunning;

    public bool IsInstalling() => Installing;

    public bool StartInstall(string package, string installLog, out string why)
    {
        why = InstallStarts ? string.Empty : "systemd отказал";
        if (InstallStarts)
        {
            Installs.Add((package, installLog));
        }

        return InstallStarts;
    }

    public bool RestartInSessions(out string why)
    {
        Restarts++;
        why = SessionsAnswer ? string.Empty : "за машиной никого нет";
        return SessionsAnswer;
    }
}

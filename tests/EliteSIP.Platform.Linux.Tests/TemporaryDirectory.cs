namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Каталог под одну проверку; удаляется вместе с ней.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    internal TemporaryDirectory()
    {
        Path = Directory.CreateTempSubdirectory("elitesip-").FullName;
    }

    internal string Path { get; }

    internal string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

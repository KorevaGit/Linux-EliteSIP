namespace EliteSIP.App.Tests;

/// <summary>Свой каталог настроек на проверку — чужие настройки машины не трогаются.</summary>
internal sealed class TemporaryHome : IDisposable
{
    internal TemporaryHome()
    {
        Path = Directory.CreateTempSubdirectory("elitesip-app-").FullName;
    }

    internal string Path { get; }

    internal string SettingsPath => System.IO.Path.Combine(Path, "settings.json");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

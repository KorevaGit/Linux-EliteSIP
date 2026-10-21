using Avalonia.Media.Imaging;

namespace EliteSIP.App.Tests;

/// <summary>Снимки окон — в каталог сборки проверок, для глаз человека.</summary>
internal static class Snapshot
{
    internal static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "snapshots");

    internal static void Save(Bitmap frame, string name)
    {
        System.IO.Directory.CreateDirectory(Directory);
        frame.Save(Path.Combine(Directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

using EliteSIP.App.Settings;
using SkiaSharp;

namespace EliteSIP.App.Theme;

internal static class BackgroundImages
{
    internal static string StoredPath => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "window-background.png");
    internal static SKBitmap Read(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 30 * 1024 * 1024) { throw new InvalidDataException("Image exceeds 30 MB"); }
        using var stream = file.OpenRead();
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid image");
        var info = codec.Info;
        if (info.Width < 1 || info.Height < 1 || (long)info.Width * info.Height > 100_000_000)
        { throw new InvalidDataException("Image dimensions exceed limit"); }
        var factor = Math.Min(1, 1600d / Math.Max(info.Width, info.Height));
        var target = new SKImageInfo(Math.Max(1, (int)(info.Width * factor)), Math.Max(1, (int)(info.Height * factor)), SKColorType.Bgra8888, SKAlphaType.Premul);
        var scaled = codec.GetScaledDimensions((float)factor);
        // PNG and some codecs cannot decode at a reduced size. Bound that
        // temporary allocation before decoding on machines with 4 GB RAM.
        if ((long)scaled.Width * scaled.Height * 4 > 64 * 1024 * 1024)
        { throw new InvalidDataException("Image requires more than 64 MB to decode; resize it before importing"); }
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var decoded = SKBitmap.Decode(codec, decodeInfo) ?? throw new InvalidDataException("Cannot decode image");
        if (decoded.Width <= 1600 && decoded.Height <= 1600) { return decoded; }
        try { return decoded.Resize(target, new SKSamplingOptions(SKFilterMode.Linear)) ?? throw new InvalidDataException("Cannot resize image"); }
        finally { decoded.Dispose(); }
    }
    internal static void Import(string source, string destination)
    {
        using var image = Read(source);
        var temporary = destination + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException("Cannot encode image");
            using (var stream = File.Create(temporary)) { data.SaveTo(stream); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

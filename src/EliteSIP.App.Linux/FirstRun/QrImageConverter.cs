using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EliteSIP.PanelLink;

namespace EliteSIP.App.FirstRun;

/// <summary>
/// QR-матрица → картинка — перевод <c>FirstRun/QrImageConverter</c>: модуль на
/// пиксель, тихая зона 4 модуля, растягивается без сглаживания
/// (<c>BitmapInterpolationMode.None</c> в разметке).
/// </summary>
public static class QrImageConverter
{
    public static IValueConverter Instance { get; } = new FuncValueConverter<QrCode?, Bitmap?>(
        qr => qr is null ? null : Render(qr));

    // BGRA непрозрачные: чёрный модуль и белый фон.
    private const int Black = unchecked((int)0xFF000000);
    private const int White = -1;

    internal static Bitmap Render(QrCode qr)
    {
        const int quiet = 4;
        var size = qr.Size + (quiet * 2);
        var pixels = new int[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[(y * size) + x] = qr[x - quiet, y - quiet] ? Black : White;
            }
        }

        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var frame = bitmap.Lock())
        {
            for (var row = 0; row < size; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    pixels, row * size, frame.Address + (row * frame.RowBytes), size);
            }
        }

        return bitmap;
    }
}

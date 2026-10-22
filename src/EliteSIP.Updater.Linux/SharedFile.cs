using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EliteSIP.Updater;

/// <summary>
/// Чтение из общего каталога от root.
/// </summary>
///
/// <remarks>
/// <para>
/// Каталог открыт на запись любому, и вместо файла там может оказаться что
/// угодно. Поэтому файл открывается не обычным <c>File.Open</c>, а так:
/// </para>
/// <list type="bullet">
///   <item><description><c>O_NOFOLLOW</c> — символическая ссылка не
///   открывается вовсе. Ядро с <c>fs.protected_symlinks = 1</c> и так не
///   пустило бы root по чужой ссылке в каталоге с битом sticky, но полагаться
///   на настройку машины здесь незачем;</description></item>
///   <item><description><c>O_NONBLOCK</c> — именованный канал вместо файла не
///   повесит обновляльщика на открытии: тот ждал бы писателя вечно, и
///   обновления машины кончились бы. Чтение из канала дальше отказывает
///   (<see cref="RandomAccess"/> с ним не работает) — и это просто «файла
///   нет»;</description></item>
///   <item><description>размер сверяется с потолком до чтения.</description></item>
/// </list>
/// <para>
/// Жёсткая ссылка на чужой файл здесь не появится: <c>fs.protected_hardlinks</c>
/// не даёт пользователю ссылаться на файл, которым он не владеет. Даже если
/// бы появилась — root только читает её и разбирает как версию или сверяет с
/// отпечатком.
/// </para>
/// </remarks>
internal static partial class SharedFile
{
    private const int ReadOnly = 0;
    private const int NonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;

    /// <summary>
    /// <c>O_NOFOLLOW</c>. Число зависит от архитектуры: на x86-64 — 0x20000,
    /// на arm64 — 0x8000.
    /// </summary>
    private static int NoFollow =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

    /// <summary>
    /// Читает файл целиком. <see langword="false"/> — нет, не файл, ссылка
    /// или больше <paramref name="maximumBytes"/>.
    /// </summary>
    internal static bool TryRead(string path, long maximumBytes, out byte[] data, out DateTime writtenUtc)
    {
        data = [];
        writtenUtc = default;

        var descriptor = Open(path, ReadOnly | NonBlocking | NoFollow | CloseOnExec);
        if (descriptor < 0)
        {
            return false;
        }

        using SafeFileHandle handle = new((nint)descriptor, ownsHandle: true);

        try
        {
            var length = RandomAccess.GetLength(handle);
            if (length > maximumBytes)
            {
                return false;
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(offset), offset);
                if (read == 0)
                {
                    // Файл укоротили под нами — прочитанное не то, что было.
                    return false;
                }

                offset += read;
            }

            data = buffer;
            writtenUtc = File.GetLastWriteTimeUtc(handle);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Переносит отметку из общего каталога к себе — со временем записи
    /// оригинала. Нет отметки — нет и копии.
    /// </summary>
    ///
    /// <remarks>
    /// Время записи переносится, потому что по нему общий
    /// <see cref="Request"/> отличает новое согласие от уже обработанного.
    /// Копию со старым содержимым оставлять нельзя: отметку в общем каталоге
    /// стёрли, а обновляльщик продолжал бы видеть согласие.
    /// </remarks>
    internal static bool TryCopyIn(string source, string copy, long maximumBytes)
    {
        if (!TryRead(source, maximumBytes, out var data, out var written))
        {
            TryDelete(copy);
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(copy, data);
            File.SetLastWriteTimeUtc(copy, written);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            TryDelete(copy);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Не снялась — следующий такт перепишет или снимет её снова.
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
}

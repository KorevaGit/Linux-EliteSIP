using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace EliteSIP.App.Platform;

/// <summary>
/// Какая система на машине — для Spark: семейство и название под версией
/// клиента в списке сотрудников. Уходит заголовками
/// <c>X-EliteSIP-Platform</c> и <c>X-EliteSIP-OS</c> с каждым запросом канала.
/// </summary>
internal static class MachinePlatform
{
    /// <summary><c>linux</c>, <c>windows</c> или <c>macos</c> — как ждёт сервер обновлений.</summary>
    internal static string Family { get; } =
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Название системы: «Linux Mint 22.3»; не нашлось — описание среды .NET.</summary>
    internal static string Name { get; } = HeaderSafe(
        (OperatingSystem.IsLinux() ? PrettyName(ReadOsRelease()) : null) ?? RuntimeInformation.OSDescription);

    /// <summary><c>PRETTY_NAME</c> из содержимого <c>/etc/os-release</c>.</summary>
    internal static string? PrettyName(string? osRelease)
    {
        foreach (var line in (osRelease ?? string.Empty).Split('\n'))
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                var value = line["PRETTY_NAME=".Length..].Trim().Trim('"', '\'').Trim();
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Печатный ASCII до 64 знаков: значение уходит заголовком HTTP, и
    /// кириллица в нём (названия сборок бывают и такими) не дошла бы.
    /// </summary>
    internal static string HeaderSafe(string text)
    {
        StringBuilder safe = new(Math.Min(text.Length, 64));
        foreach (var symbol in text)
        {
            if (safe.Length == 64)
            {
                break;
            }

            if (symbol is >= ' ' and <= '~')
            {
                safe.Append(symbol);
            }
        }

        return safe.ToString().Trim();
    }

    private static string? ReadOsRelease()
    {
        foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}

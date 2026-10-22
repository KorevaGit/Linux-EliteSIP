using System.Reflection;
using EliteSIP.Audio;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Platform;

/// <summary>
/// Из чего собран выпуск и на чём он работает — для строки запуска в журнале
/// и для архива поддержки.
/// </summary>
///
/// <remarks>
/// Первый вопрос поддержки — «какая версия и какая система». У Linux к нему
/// добавляются два: из какого общего кода собран выпуск (подмодуль
/// <c>shared/</c> идёт своим ходом, план, «Версия»), и какой сеанс — X11 или
/// Wayland.
/// </remarks>
internal static class SupportFacts
{
    /// <summary>
    /// Общий код выпуска: <c>git describe</c> подмодуля при сборке, например
    /// <c>linux-v0.1.1-0-g3f963d5</c>. Вписывает сборка
    /// (<c>EliteSIP.App.Linux.csproj</c>, цель <c>StampSharedCode</c>).
    /// </summary>
    internal static string SharedCode { get; } =
        typeof(SupportFacts).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "SharedCode")?.Value is { Length: > 0 } code
                ? code
                : "неизвестен";

    /// <summary>Строка запуска: версия, общий код, сеанс.</summary>
    internal static string StartupLine(string version, Func<string, string?> environment) =>
        $"запуск: EliteSIP {version} (Linux), общий код {SharedCode}, сеанс {GraphicsSession.Describe(environment)}";

    /// <summary>
    /// <c>summary.txt</c> архива. Секретов здесь нет: номер и идентификатор
    /// машины поддержка и так спрашивает первыми.
    /// </summary>
    internal static string Summary(
        string version,
        string? installation,
        string? number,
        IAudioPlatform audio,
        Func<string, string?> environment)
    {
        var lines = new List<string>
        {
            $"EliteSIP {version} (Linux)",
            $"общий код: {SharedCode}",
            $"машина: {installation}",
            $"добавочный: {number}",
            $"система: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"сеанс: {GraphicsSession.Describe(environment)}",
        };

        lines.AddRange(Sound(audio));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Файлы журнала и <c>crash.log</c> — пути, а не строки: архив копирует
    /// файлы сам (<see cref="EliteSIP.Diagnostics.SupportArchive.Make"/>).
    /// </summary>
    internal static IReadOnlyList<string> Files(IEnumerable<string> logs, string profileDirectory)
    {
        var files = logs.ToList();

        var crash = Path.Combine(profileDirectory, "crash.log");
        if (File.Exists(crash))
        {
            files.Add(crash);
        }

        return files;
    }

    /// <summary>
    /// Устройства по умолчанию и списки: половина жалоб на звук — «говорю в
    /// ноутбук, слушаю гарнитуру» (план, «Что ещё вскрылось»), и это видно
    /// отсюда сразу.
    /// </summary>
    private static IEnumerable<string> Sound(IAudioPlatform audio)
    {
        IReadOnlyList<AudioDevice> inputs;
        IReadOnlyList<AudioDevice> outputs;
        AudioDevice? input;
        AudioDevice? output;

        try
        {
            inputs = audio.Devices(AudioDeviceDirection.Capture);
            outputs = audio.Devices(AudioDeviceDirection.Render);
            input = audio.DefaultDevice(AudioDeviceDirection.Capture);
            output = audio.DefaultDevice(AudioDeviceDirection.Render);
        }
        catch (InvalidOperationException error)
        {
            return [$"звук: сервер недоступен ({error.Message})"];
        }

        return
        [
            $"микрофон по умолчанию: {input?.Name ?? "нет"}",
            $"выход по умолчанию: {output?.Name ?? "нет"}",
            $"микрофоны: {string.Join("; ", inputs.Select(device => device.Name))}",
            $"выходы: {string.Join("; ", outputs.Select(device => device.Name))}",
        ];
    }
}

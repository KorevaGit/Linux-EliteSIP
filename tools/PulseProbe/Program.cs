using System.Diagnostics;
using System.Globalization;
using EliteSIP.Audio.Pulse;
using PulseProbe;

// Стенд W-L0А. Режимы:
//   devices                                   устройства и их свойства (через pactl)
//   duplex [--source S] [--sink S] [--seconds N] [--fragment MS] [--target MS] [--tone] [--role R]
//   catalog                                   устройства так, как их видит продукт (W-L2)
//   engine [--seconds N] [--codec pcmu|pcma|g722] [--input ID] [--output ID] [--tone] [--loopback]
//                                             продуктовый тракт на живом сервере (W-L2)
//
// Вывод в duplex по умолчанию — тишина: стенд можно гонять в общем кабинете.

string mode = args.Length > 0 ? args[0] : "help";
string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
double Number(string name, double fallback) =>
    Arg(name) is string s ? double.Parse(s, CultureInfo.InvariantCulture) : fallback;

switch (mode)
{
    case "signals":
    {
        // Служебные звуки через общий проигрыватель на выводе Pulse: тихие
        // гудки, тон клавиши, рингтон из файла (если передан — WAV или MP3).
        List<string> log = [];
        using var player = PulseSignalSounds.Create(log.Add);
        Console.WriteLine("гудки 1,5 с");
        player.StartRingback(null, 0.1);
        Thread.Sleep(1500);
        player.StopRingback();
        Console.WriteLine("клавиша 5");
        player.PlayKeyTone('5', null);
        Thread.Sleep(400);
        if (args.Length > 1)
        {
            Console.WriteLine($"рингтон {args[1]}: {(player.PreviewRingtone(args[1], 0.15, null) ? "свой" : "стандартный")}");
            Thread.Sleep(2000);
            player.StopPreview();
        }

        foreach (var line in log)
        {
            Console.WriteLine("журнал: " + line);
        }

        Console.WriteLine($"играет после снятия: {player.IsPlaying}");
        return 0;
    }

    case "devices":
        return DeviceList.Print();

    case "duplex":
        return new DuplexProbe(new DuplexProbe.Options(
            Source: Arg("--source"),
            Sink: Arg("--sink"),
            Seconds: (int)Number("--seconds", 30),
            CaptureFragmentMs: Number("--fragment", 10),
            PlaybackTargetMs: Number("--target", 20),
            Tone: args.Contains("--tone"),
            Role: Arg("--role") ?? "phone")).Run();

    case "catalog":
        foreach (EliteSIP.Audio.AudioDeviceDirection direction in Enum.GetValues<EliteSIP.Audio.AudioDeviceDirection>())
        {
            Console.WriteLine(direction == EliteSIP.Audio.AudioDeviceDirection.Capture ? "== микрофоны" : "== выходы");
            string? fallback = EliteSIP.Audio.Pulse.PulseDeviceCatalog.Default(direction)?.Id;
            foreach (EliteSIP.Audio.AudioDevice device in EliteSIP.Audio.Pulse.PulseDeviceCatalog.Devices(direction))
            {
                Console.WriteLine((device.Id == fallback ? "* " : "  ") + device.Summary);
                Console.WriteLine("    " + device.Id);
            }
        }

        return 0;

    case "engine":
        return new EngineProbe(new EngineProbe.Options(
            Seconds: (int)Number("--seconds", 30),
            Codec: Arg("--codec") switch
            {
                "pcma" => EliteSIP.MediaCore.AudioCodec.Pcma,
                "g722" => EliteSIP.MediaCore.AudioCodec.G722,
                _ => EliteSIP.MediaCore.AudioCodec.Pcmu,
            },
            Input: Arg("--input"),
            Output: Arg("--output"),
            Tone: args.Contains("--tone"),
            Loopback: args.Contains("--loopback"))).Run();

    default:
        Console.WriteLine("PulseProbe devices | catalog | duplex [...] | engine [--seconds N] [--codec C] [--input ID] [--output ID] [--tone] [--loopback]");
        return 1;
}

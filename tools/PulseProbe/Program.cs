using System.Diagnostics;
using System.Globalization;
using PulseProbe;

// Стенд W-L0А. Режимы:
//   devices                                   устройства и их свойства (через pactl)
//   duplex [--source S] [--sink S] [--seconds N] [--fragment MS] [--target MS] [--tone] [--role R]
//
// Вывод в duplex по умолчанию — тишина: стенд можно гонять в общем кабинете.

string mode = args.Length > 0 ? args[0] : "help";
string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
double Number(string name, double fallback) =>
    Arg(name) is string s ? double.Parse(s, CultureInfo.InvariantCulture) : fallback;

switch (mode)
{
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

    default:
        Console.WriteLine("PulseProbe devices | duplex [--source S] [--sink S] [--seconds N] [--fragment MS] [--target MS] [--tone] [--role R]");
        return 1;
}

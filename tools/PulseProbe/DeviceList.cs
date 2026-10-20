using System.Diagnostics;
using System.Text.Json;

namespace PulseProbe;

/// <summary>
/// Устройства так, как их видит звуковой сервер, — через <c>pactl -f json</c>.
/// </summary>
///
/// <remarks>
/// Стенду этого хватает: вопрос W-L0А не «как читать список», а «что в нём
/// есть» — форм-фактор, шина, профиль, частота. Продукт будет читать то же
/// самое через <c>pa_context_get_*_info_list</c> на W-L2.
/// </remarks>
internal static class DeviceList
{
    private static readonly string[] Keys =
    [
        "device.form_factor", "device.bus", "device.api", "device.class",
        "device.profile.name", "device.description", "api.bluez5.profile",
        "media.class", "node.name",
    ];

    internal static int Print()
    {
        foreach (string kind in new[] { "sinks", "sources" })
        {
            Console.WriteLine(kind == "sinks" ? "== выходы" : "== входы");
            using var document = JsonDocument.Parse(Pactl("-f", "json", "list", kind));
            foreach (JsonElement device in document.RootElement.EnumerateArray())
            {
                string name = device.GetProperty("name").GetString() ?? "?";
                string spec = device.TryGetProperty("sample_specification", out var s) ? s.GetString() ?? "" : "";
                string state = device.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                Console.WriteLine($"{name}");
                Console.WriteLine($"    {spec}, {state}");
                if (device.TryGetProperty("properties", out JsonElement properties))
                {
                    foreach (string key in Keys)
                    {
                        if (properties.TryGetProperty(key, out JsonElement value))
                        {
                            Console.WriteLine($"    {key} = {value.GetString()}");
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"по умолчанию: выход {Pactl("get-default-sink").Trim()}, вход {Pactl("get-default-source").Trim()}");
        return 0;
    }

    private static string Pactl(params string[] arguments)
    {
        var start = new ProcessStartInfo("pactl") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        start.Environment["LC_ALL"] = "C";
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
}

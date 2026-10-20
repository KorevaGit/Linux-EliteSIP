using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace WindowProbe;

/// <summary>
/// Сценарий стенда: показать окно входящего так, как его будет показывать
/// продукт, и снять с X-сервера то, что получилось на самом деле.
/// </summary>
internal sealed class Probe(IClassicDesktopStyleApplicationLifetime desktop)
{
    private readonly bool _inject = Program.Arguments.Contains("--inject");
    private readonly int _humanSeconds = Program.Arguments.SkipWhile(a => a != "--human").Skip(1).Select(int.Parse).FirstOrDefault();
    private readonly bool _noInputHint = Program.Arguments.Contains("--no-input-hint");
    private readonly List<string> _report = [];
    private int _clicks;
    private nint _display;
    private InputWatch? _watch;
    private Window? _window;
    private TextBlock? _status;
    private Button? _accept;

    internal async void Run()
    {
        try
        {
            await RunAsync();
        }
        catch (Exception error)
        {
            Line($"СБОЙ: {error}");
        }
        finally
        {
            foreach (string line in _report)
            {
                Console.WriteLine(line);
            }

            _watch?.Dispose();
            if (_display != 0)
            {
                _ = Xlib.XCloseDisplay(_display);
            }

            desktop.Shutdown();
        }
    }

    private void Line(string text) => _report.Add(text);

    private async Task RunAsync()
    {
        _display = Xlib.XOpenDisplay(0);
        _watch = InputWatch.Start(out string? failure);
        if (_display == 0 || _watch is null)
        {
            Line($"X11 недоступен: {failure}");
            return;
        }

        nuint root = Xlib.XDefaultRootWindow(_display);
        Line($"сеанс: XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}, рабочий стол {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")}");
        nuint active0 = ActiveWindow(root);
        Line($"активное окно до показа: {Describe(active0)}");

        // Позиция — в пикселях на экране под курсором, как у Windows-версии
        // (W9-GUARD.md, «Позиция считается в пикселях»).
        (int px, int py) = Pointer(root);
        var helper = new Window();
        Screen? screen = helper.Screens.ScreenFromPoint(new PixelPoint(px, py)) ?? helper.Screens.Primary;
        PixelRect work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        double scaling = screen?.Scaling ?? 1;
        var requested = new PixelPoint(work.X + (int)(150 * scaling) + 37, work.Y + (int)(150 * scaling) + 23);
        Line($"курсор: {px},{py}; экран {screen?.Bounds}, рабочая область {work}, масштаб {scaling}");

        _status = new TextBlock { Text = "EliteSIP · стенд окна входящего", Foreground = Brushes.White, Margin = new Thickness(12) };
        var accept = _accept = new Button
        {
            Content = "Принять",
            HorizontalAlignment = HorizontalAlignment.Center,
            Width = 160,
            Height = 48,
            Focusable = false,
        };
        accept.Click += (_, _) =>
        {
            _clicks++;
            _status.Text = $"щелчков: {_clicks}";
        };

        _window = new Window
        {
            Width = 320,
            Height = 212,
            CanResize = false,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = requested,
            Background = new SolidColorBrush(Color.FromRgb(0x1f, 0x2a, 0x44)),
            Content = new StackPanel { Children = { _status, accept } },
            Title = "EliteSIP — входящий (стенд)",
        };
        ApplyChromeless(_window);

        var opened = new TaskCompletionSource();
        _window.Opened += (_, _) => opened.TrySetResult();
        _window.Show();
        await opened.Task;

        nuint ours = (nuint)(_window.TryGetPlatformHandle()?.Handle ?? 0);
        _watch.WatchWindow(ours);
        if (_noInputHint)
        {
            Line("WM_HINTS.input → False (ключ --no-input-hint)");
            SetInputHint(ours, false);
        }

        await Task.Delay(800);

        nuint active1 = ActiveWindow(root);
        Line($"окно стенда: 0x{ours:x}");
        Line($"активное окно после показа: {Describe(active1)} — {(active1 == active0 ? "фокус НЕ забран" : active1 == ours ? "ФОКУС ЗАБРАН окном стенда" : "сменилось на другое")}");
        Line($"_NET_WM_STATE: {string.Join(", ", Xlib.Property(_display, ours, "_NET_WM_STATE").Select(a => Xlib.AtomName(_display, a)))}");
        Line($"_NET_WM_WINDOW_TYPE: {string.Join(", ", Xlib.Property(_display, ours, "_NET_WM_WINDOW_TYPE").Select(a => Xlib.AtomName(_display, a)))}");
        nuint[] hints = Xlib.Property(_display, ours, "WM_HINTS");
        Line($"WM_HINTS: flags 0x{(hints.Length > 0 ? hints[0] : 0):x}, input {(hints.Length > 1 ? hints[1].ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}");
        (int ax, int ay) = Translate(ours, root, 0, 0);
        Line($"позиция: просили {requested.X},{requested.Y}, вышло {ax},{ay}{(ax == requested.X && ay == requested.Y ? " — совпало" : $" — РАСХОЖДЕНИЕ {ax - requested.X},{ay - requested.Y}")}");
        Line($"Avalonia Position: {_window.Position}, размер клиента {_window.ClientSize}");
        nuint[] stacking = Xlib.Property(_display, root, "_NET_CLIENT_LIST_STACKING");
        int index = Array.IndexOf(stacking, ours);
        Line($"в стопке окон: {(index < 0 ? "не найдено" : $"{index + 1} из {stacking.Length}{(index == stacking.Length - 1 ? " — сверху" : " — НЕ сверху")}")}");
        Line($"простой ввода (XScreenSaver): {IdleMs()} мс");

        if (_inject)
        {
            await InjectAsync(root, ours, active0);
        }

        if (_humanSeconds > 0)
        {
            await HumanAsync(root, active0);
        }

        Summarize();
        _window.Close();
    }

    /// <summary>
    /// Синтетический путь и щелчок через XTest — то, чем работают xdotool и
    /// почти все автокликеры. Курсор возвращается на место.
    /// </summary>
    private async Task InjectAsync(nuint root, nuint ours, nuint active0)
    {
        (int px, int py) = Pointer(root);

        // Центр кнопки — из разметки, в пикселях окна: щелчок обязан прийти в
        // кнопку, а не в окно рядом с ней.
        Button accept = _accept!;
        Point center = accept.TranslatePoint(new Point(accept.Bounds.Width / 2, accept.Bounds.Height / 2), _window!) ?? new Point(160, 70);
        double scale = _window!.RenderScaling;
        (int ax, int ay) = Translate(ours, root, (int)(center.X * scale), (int)(center.Y * scale));

        while (_watch!.Events.TryDequeue(out _))
        {
        }

        Line("");
        Line($"XTest: путь от {px},{py} к кнопке {ax},{ay} и щелчок");
        for (int i = 1; i <= 12; i++)
        {
            _ = Xlib.XTestFakeMotionEvent(_display, -1, px + ((ax - px) * i / 12), py + ((ay - py) * i / 12), 0);
            _ = Xlib.XFlush(_display);
            await Task.Delay(15);
        }

        int before = _clicks;
        _ = Xlib.XTestFakeButtonEvent(_display, 1, 1, 0);
        _ = Xlib.XFlush(_display);
        await Task.Delay(40);
        _ = Xlib.XTestFakeButtonEvent(_display, 1, 0, 0);
        _ = Xlib.XFlush(_display);
        await Task.Delay(500);

        nuint active2 = ActiveWindow(root);
        Line($"щелчок дошёл до кнопки: {(_clicks > before ? "да" : "НЕТ")}");
        Line($"активное окно после щелчка: {Describe(active2)} — {(active2 == active0 ? "фокус НЕ забран" : "ФОКУС СМЕНИЛСЯ")}");
        Line($"простой ввода сразу после XTest: {IdleMs()} мс");
        ReportEvents("XTest");

        _ = Xlib.XTestFakeMotionEvent(_display, -1, px, py, 0);
        _ = Xlib.XFlush(_display);
        await Task.Delay(100);
        while (_watch.Events.TryDequeue(out _))
        {
        }
    }

    private async Task HumanAsync(nuint root, nuint active0)
    {
        while (_watch!.Events.TryDequeue(out _))
        {
        }

        int before = _clicks;
        _status!.Text = $"Щёлкните «Принять» мышью ({_humanSeconds} с)";
        Line("");
        Line($"человек: жду настоящего щелчка {_humanSeconds} с");
        var watch = Stopwatch.StartNew();
        while (_clicks == before && watch.Elapsed.TotalSeconds < _humanSeconds)
        {
            await Task.Delay(100);
        }

        await Task.Delay(300);
        Line($"щелчок человека: {(_clicks > before ? "был" : "не дождался")}");
        nuint active = ActiveWindow(root);
        Line($"активное окно после щелчка человека: {Describe(active)} — {(active == active0 ? "фокус НЕ забран" : "ФОКУС СМЕНИЛСЯ")}");
        ReportEvents("человек");
    }

    private void ReportEvents(string who)
    {
        var events = new List<InputWatch.InputEvent>();
        while (_watch!.Events.TryDequeue(out var e))
        {
            events.Add(e);
        }

        Dictionary<int, string> names = DeviceNames();
        string Name(int id) => id == 0 ? "0 (не заполнено)" : names.TryGetValue(id, out string? n) ? $"{id} «{n}»" : id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var group in events.GroupBy(e => (e.Kind, e.DeviceId, e.SourceId)))
        {
            Line($"  {who}: {group.Key.Kind} ×{group.Count()} — deviceid {Name(group.Key.DeviceId)}, sourceid {Name(group.Key.SourceId)}");
        }

        if (events.Count == 0)
        {
            Line($"  {who}: событий XI2 нет");
        }
    }

    private void Summarize()
    {
        Line("");
        Line($"итого щелчков по кнопке: {_clicks}");
    }

    private (int X, int Y) Pointer(nuint root)
    {
        unsafe
        {
            nuint r, c;
            int px, py, wx, wy;
            uint mask;
            _ = Xlib.XQueryPointer(_display, root, &r, &c, &px, &py, &wx, &wy, &mask);
            return (px, py);
        }
    }

    private (int X, int Y) Translate(nuint window, nuint root, int x, int y)
    {
        unsafe
        {
            int ax, ay;
            nuint child;
            _ = Xlib.XTranslateCoordinates(_display, window, root, x, y, &ax, &ay, &child);
            return (ax, ay);
        }
    }

    private unsafe long IdleMs()
    {
        Xlib.XScreenSaverInfo* info = Xlib.XScreenSaverAllocInfo();
        _ = Xlib.XScreenSaverQueryInfo(_display, Xlib.XDefaultRootWindow(_display), info);
        long idle = (long)info->Idle;
        _ = Xlib.XFree(info);
        return idle;
    }

    private nuint ActiveWindow(nuint root)
    {
        nuint[] value = Xlib.Property(_display, root, "_NET_ACTIVE_WINDOW");
        return value.Length > 0 ? value[0] : 0;
    }

    private unsafe string Describe(nuint window)
    {
        if (window == 0)
        {
            return "нет";
        }

        nuint atom = Xlib.XInternAtom(_display, "WM_CLASS", 0);
        nuint type, items, after;
        int format;
        byte* data;
        string cls = "?";
        if (Xlib.XGetWindowProperty(_display, window, atom, 0, 256, 0, 0, &type, &format, &items, &after, &data) == 0 && data != null)
        {
            cls = System.Text.Encoding.UTF8.GetString(data, (int)items).Replace('\0', '/').TrimEnd('/');
            _ = Xlib.XFree(data);
        }

        return $"0x{window:x} ({cls})";
    }

    private static Dictionary<int, string> DeviceNames()
    {
        var result = new Dictionary<int, string>();
        try
        {
            var start = new ProcessStartInfo("xinput", "list --short") { RedirectStandardOutput = true, UseShellExecute = false };
            using Process p = Process.Start(start)!;
            foreach (string raw in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                int id = raw.IndexOf("id=", StringComparison.Ordinal);
                if (id < 0)
                {
                    continue;
                }

                string digits = new([.. raw[(id + 3)..].TakeWhile(char.IsDigit)]);
                string name = raw[..id].Trim(' ', '\t', '⎡', '⎜', '⎣', '↳').Trim();
                if (int.TryParse(digits, out int n))
                {
                    result[n] = name;
                }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        return result;
    }

    private static void ApplyChromeless(Window window) => window.WindowDecorations = WindowDecorations.None;

    private unsafe void SetInputHint(nuint window, bool input)
    {
        // WM_HINTS — 9 значений формата 32: flags, input, initial_state, ...
        // Флаг InputHint = 1. Правим через XChangeProperty, без XSetWMHints:
        // стенду хватает, а структура XWMHints не нужна.
        nuint[] current = Xlib.Property(_display, window, "WM_HINTS");
        var values = new nuint[9];
        Array.Copy(current, values, Math.Min(current.Length, 9));
        values[0] |= 1;
        values[1] = input ? 1u : 0u;
        nuint atom = Xlib.XInternAtom(_display, "WM_HINTS", 0);
        fixed (nuint* data = values)
        {
            _ = XChangeProperty(_display, window, atom, atom, 32, 0, (byte*)data, 9);
        }

        _ = Xlib.XFlush(_display);
    }

    [System.Runtime.InteropServices.DllImport("libX11.so.6")]
    private unsafe static extern int XChangeProperty(nint display, nuint window, nuint property, nuint type, int format, int mode, byte* data, int count);
}

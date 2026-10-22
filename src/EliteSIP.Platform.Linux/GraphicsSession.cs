namespace EliteSIP.Platform.Linux;

/// <summary>Какой графический сеанс у оператора.</summary>
internal enum GraphicsSessionKind
{
    X11,

    /// <summary>Сеанс Wayland: софтфон работает через XWayland.</summary>
    Wayland,

    Unknown,
}

/// <summary>
/// Сеанс X11 или Wayland — то, от чего зависит защита окна входящего.
/// </summary>
///
/// <remarks>
/// <para>
/// v1 поддерживает только X11 (план, «Что решено»). На Wayland софтфон
/// запустится — Avalonia откроется через XWayland, — но защита приёма там
/// слабее, и молча: XWayland не видит курсор над родными окнами Wayland
/// (а браузер с CRM — родной), не знает признака XTest у ввода через портал,
/// и окно может не встать поверх. Поэтому о таком сеансе говорится в журнале
/// и оператору, а не выясняется по жалобе.
/// </para>
/// <para>
/// Признаки — переменные окружения сеанса. <c>XDG_SESSION_TYPE</c> в
/// пользовательскую службу systemd передают не все рабочие столы, а
/// <c>WAYLAND_DISPLAY</c> GNOME и KDE передают всегда: он и решает, если
/// первой нет.
/// </para>
/// </remarks>
internal static class GraphicsSession
{
    internal static GraphicsSessionKind Current => Detect(Environment.GetEnvironmentVariable);

    internal static GraphicsSessionKind Detect(Func<string, string?> environment)
    {
        var type = environment("XDG_SESSION_TYPE");

        if (string.Equals(type, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY")))
        {
            return GraphicsSessionKind.Wayland;
        }

        if (string.Equals(type, "x11", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(environment("DISPLAY")))
        {
            return GraphicsSessionKind.X11;
        }

        return GraphicsSessionKind.Unknown;
    }

    /// <summary>Строка для журнала и архива поддержки.</summary>
    internal static string Describe(Func<string, string?> environment)
    {
        var kind = Detect(environment);
        var desktop = environment("XDG_CURRENT_DESKTOP") is { Length: > 0 } name ? name : "?";

        return kind switch
        {
            GraphicsSessionKind.X11 => $"X11 ({desktop}, DISPLAY={environment("DISPLAY")})",
            GraphicsSessionKind.Wayland => $"Wayland через XWayland ({desktop}) — НЕ ПОДДЕРЖИВАЕТСЯ, защита окна входящего ослаблена",
            _ => $"не определён ({desktop})",
        };
    }
}

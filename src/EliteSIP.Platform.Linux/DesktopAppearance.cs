using Tmds.DBus.Protocol;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Тема и акцент рабочего стола — то, что на Windows <c>AppearanceService</c> и
/// <c>SystemAccent</c> читали из реестра.
/// </summary>
///
/// <remarks>
/// <para>
/// Источник — портал <c>org.freedesktop.portal.Settings</c>, пространство
/// <c>org.freedesktop.appearance</c>: <c>color-scheme</c> (0 — без
/// предпочтения, 1 — тёмная, 2 — светлая) и <c>accent-color</c> (три доли
/// RGB от 0 до 1). Его отдают GNOME, KDE и в Mint — xapp-портал за Cinnamon.
/// Читать настройки GTK или Cinnamon самим значило бы писать по ветке на
/// каждый рабочий стол.
/// </para>
/// <para>
/// <b>Нет портала или нет предпочтения — светлая тема и акцент продукта.</b>
/// Так решено в плане: «как в системе» на машине, где система ничего не
/// говорит, — это светлая, как у Windows с настройками по умолчанию. Cinnamon
/// на Mint 22.3 отдаёт <c>color-scheme = 0</c>, пока в «Темах» не выбран
/// тёмный режим явно (замер 21 октября 2026).
/// </para>
/// </remarks>
internal sealed class DesktopAppearance : IDisposable
{
    private const string Portal = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string SettingsInterface = "org.freedesktop.portal.Settings";
    private const string Namespace = "org.freedesktop.appearance";

    private readonly Action<string> _log;
    private IDisposable? _subscription;

    internal DesktopAppearance(Action<string> log)
    {
        _log = log;
    }

    /// <summary>Система просит тёмную тему.</summary>
    internal bool PrefersDark { get; private set; }

    /// <summary>Акцент системы; <see langword="null"/> — свой акцент продукта.</summary>
    internal AccentColor? Accent { get; private set; }

    /// <summary>Тема или акцент сменились. Не в потоке интерфейса.</summary>
    internal event Action? Changed;

    internal async Task StartAsync()
    {
        try
        {
            var connection = DBusConnection.Session;

            PrefersDark = IsDark(await ReadAsync(connection, "color-scheme").ConfigureAwait(false));
            Accent = ToAccent(await ReadAsync(connection, "accent-color").ConfigureAwait(false));

            _subscription = await connection.WatchSignalAsync(
                    Portal, PortalPath, SettingsInterface, "SettingChanged",
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Namespace: reader.ReadString(), Key: reader.ReadString(), Value: reader.ReadVariantValue());
                    },
                    change =>
                    {
                        if (change.HasValue)
                        {
                            OnSettingChanged(change.Value.Namespace, change.Value.Key, change.Value.Value);
                        }
                    },
                    ObserverFlags.None, false, null)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            _log($"тема: портал недоступен ({error.Message}) — светлая тема и свой акцент");
        }
    }

    public void Dispose() => _subscription?.Dispose();

    /// <summary><c>color-scheme</c> → тёмная ли. Всё, кроме явной 1, — светлая.</summary>
    internal static bool IsDark(VariantValue? value) =>
        value is { Type: VariantValueType.UInt32 } scheme && scheme.GetUInt32() == 1;

    /// <summary>
    /// <c>accent-color</c> → цвет. Доли вне 0…1 — «акцент не задан» по
    /// спецификации портала.
    /// </summary>
    internal static AccentColor? ToAccent(VariantValue? value)
    {
        if (value is not { Type: VariantValueType.Struct, Count: 3 } rgb)
        {
            return null;
        }

        var red = rgb.GetItem(0).GetDouble();
        var green = rgb.GetItem(1).GetDouble();
        var blue = rgb.GetItem(2).GetDouble();

        return InRange(red) && InRange(green) && InRange(blue)
            ? new AccentColor(Byte(red), Byte(green), Byte(blue))
            : null;

        static bool InRange(double share) => share is >= 0 and <= 1;
        static byte Byte(double share) => (byte)Math.Round(share * 255);
    }

    private void OnSettingChanged(string space, string key, VariantValue value)
    {
        if (space != Namespace)
        {
            return;
        }

        if (key == "color-scheme")
        {
            PrefersDark = IsDark(value);
        }
        else if (key == "accent-color")
        {
            Accent = ToAccent(value);
        }
        else
        {
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Одна настройка или <see langword="null"/>, если её нет.
    /// </summary>
    ///
    /// <remarks>
    /// <c>ReadOne</c> — с версии 2 портала (2023); старый <c>Read</c> отдаёт
    /// то же значение, завёрнутое в лишний вариант. Сначала новый, при отказе —
    /// старый: на Ubuntu 22.04 и Debian 11 портал старый.
    /// </remarks>
    private static async Task<VariantValue?> ReadAsync(DBusConnection connection, string key)
    {
        try
        {
            return await CallAsync(connection, "ReadOne", key).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException error) when (error.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod")
        {
            try
            {
                var wrapped = await CallAsync(connection, "Read", key).ConfigureAwait(false);
                return wrapped.Type is VariantValueType.Variant ? wrapped.GetVariantValue() : wrapped;
            }
            catch (DBusErrorReplyException)
            {
                return null;
            }
        }
        catch (DBusErrorReplyException)
        {
            // Нет такой настройки (org.freedesktop.portal.Error.NotFound).
            return null;
        }
    }

    private static Task<VariantValue> CallAsync(DBusConnection connection, string method, string key)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Portal, PortalPath, SettingsInterface, method, "ss", MessageFlags.None);
        writer.WriteString(Namespace);
        writer.WriteString(key);

        return connection.CallMethodAsync(
            writer.CreateMessage(),
            static (message, _) => message.GetBodyReader().ReadVariantValue(),
            null);
    }
}

/// <summary>Цвет акцента в байтах — то, что палитре нужно от системы.</summary>
internal readonly record struct AccentColor(byte Red, byte Green, byte Blue);

using Tmds.DBus.Protocol;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Есть ли в сеансе куда поставить значок — то, что на Windows решал
/// <c>TrayIcon.IsAdded</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Значок Avalonia на Linux — StatusNotifierItem: он регистрируется у
/// <c>org.kde.StatusNotifierWatcher</c>, а рисует его хозяин области
/// уведомлений, зарегистрированный у того же наблюдателя. В Cinnamon
/// наблюдатель — <c>xapp-sn-watcher</c>, хозяин — апплет панели. Перезапуск
/// наблюдателя Avalonia переживает сам: следит за сменой его владельца и
/// регистрирует значок заново (это и есть аналог <c>TaskbarCreated</c>).
/// </para>
/// <para>
/// Чего Avalonia не говорит — <b>виден ли значок вообще</b>. В чистом GNOME
/// без расширения AppIndicator наблюдателя нет, и значок не появится нигде.
/// Прятать туда панель тогда нельзя: софтфон окажется запущенным, невидимым
/// и без выхода. Поэтому, как и на Windows, закрытие панели прячет её только
/// при <see cref="IsAvailable"/>, а иначе сворачивает.
/// </para>
/// </remarks>
internal sealed class TrayAvailability : IDisposable
{
    private const string Watcher = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";

    private readonly Action<string> _log;
    private IDisposable? _ownerWatch;

    internal TrayAvailability(Action<string> log)
    {
        _log = log;
    }

    /// <summary>Наблюдатель есть, и у него есть хозяин, который рисует значки.</summary>
    internal bool IsAvailable { get; private set; }

    /// <summary><see cref="IsAvailable"/> сменилось. Не в потоке интерфейса.</summary>
    internal event Action? Changed;

    internal async Task StartAsync()
    {
        try
        {
            var connection = DBusConnection.Session;

            // Только про наблюдатель (arg0): без фильтра шина присылала бы
            // смену владельца каждого имени в сеансе.
            MatchRule rule = new()
            {
                Type = MessageType.Signal,
                Sender = "org.freedesktop.DBus",
                Interface = "org.freedesktop.DBus",
                Member = "NameOwnerChanged",
                Arg0 = Watcher,
            };

            _ownerWatch = await connection.AddMatchAsync(
                    rule,
                    static (message, _) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Name: reader.ReadString(), Old: reader.ReadString(), New: reader.ReadString());
                    },
                    change =>
                    {
                        if (change.HasValue && change.Value.Name == Watcher)
                        {
                            _ = RefreshAsync(connection);
                        }
                    },
                    false, ObserverFlags.None, null)
                .ConfigureAwait(false);

            await RefreshAsync(connection).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusExceptionBase or IOException or System.Net.Sockets.SocketException)
        {
            _log($"значок: шина сеанса недоступна ({error.Message}) — панель будет сворачиваться");
        }
    }

    public void Dispose() => _ownerWatch?.Dispose();

    private async Task RefreshAsync(DBusConnection connection)
    {
        var available = await HostRegisteredAsync(connection).ConfigureAwait(false);
        if (available == IsAvailable)
        {
            return;
        }

        IsAvailable = available;
        _log(available
            ? "значок: область уведомлений есть"
            : "значок: области уведомлений нет — закрытие панели её свернёт");
        Changed?.Invoke();
    }

    /// <remarks>
    /// Свойство <c>IsStatusNotifierHostRegistered</c>, а не просто наличие
    /// имени: наблюдатель бывает и без хозяина — запущен, а рисовать некому.
    /// </remarks>
    private static async Task<bool> HostRegisteredAsync(DBusConnection connection)
    {
        try
        {
            return await connection.CallMethodAsync(
                    PropertyMessage(connection),
                    static (message, _) => message.GetBodyReader().ReadVariantValue().GetBool(),
                    null)
                .ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // Нет наблюдателя (ServiceUnknown) или он не знает свойства.
            return false;
        }
    }

    private static MessageBuffer PropertyMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Watcher, WatcherPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        writer.WriteString(Watcher);
        writer.WriteString("IsStatusNotifierHostRegistered");

        return writer.CreateMessage();
    }
}

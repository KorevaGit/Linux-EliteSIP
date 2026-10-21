using System.Net.NetworkInformation;
using EliteSIP.App.Platform;

namespace EliteSIP.Platform.Linux;

/// <summary>
/// Сон и переезд в другую сеть — Linux-половина <c>Shell/NetworkWatch</c>
/// Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Почему это нужно и почему события смены адреса склеиваются, сказано в
/// Windows-версии; поведение то же. Отличаются источники.
/// </para>
/// <para>
/// <see cref="NetworkChange.NetworkAddressChanged"/> на Linux работает: .NET
/// слушает netlink (<c>RTMGRP_IPV4_IFADDR</c> и соседей), и NetworkManager,
/// переключая Wi-Fi, даёт ту же очередь событий, что Windows. А
/// <c>SystemEvents.PowerModeChanged</c> здесь не приходит вовсе — пробуждение
/// берётся у logind (<see cref="SessionLifetime.Resumed"/>).
/// </para>
/// </remarks>
internal sealed class NetworkWatch : IDisposable
{
    /// <summary>Три секунды тишины — как на Windows.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly IUiThread _ui;
    private readonly SessionLifetime _session;
    private readonly Action _onNetworkChanged;
    private readonly Action _onResume;
    private readonly IUiTimer _settling;

    private bool _disposed;

    internal NetworkWatch(IUiThread ui, SessionLifetime session, Action onNetworkChanged, Action onResume)
    {
        _ui = ui;
        _session = session;
        _onNetworkChanged = onNetworkChanged;
        _onResume = onResume;

        _settling = ui.CreateTimer(Settle);
        _settling.Tick += OnSettled;

        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        _session.Resumed += OnResumed;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        _session.Resumed -= OnResumed;

        _settling.Stop();
        _settling.Tick -= OnSettled;
        _disposed = true;
    }

    private void OnSettled(object? sender, EventArgs e)
    {
        _settling.Stop();
        _onNetworkChanged();
    }

    // Событие приходит с потока netlink, таймер живёт на потоке окна.
    // Перезапуск таймера и есть склейка очереди событий в одно.
    private void OnAddressChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        _settling.Stop();
        _settling.Start();
    });

    private void OnResumed() => _ui.Post(_onResume);
}

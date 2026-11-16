using Avalonia.Threading;

namespace EliteSIP.App.Theme;

/// <summary>Coalesce appearance slider writes on the UI thread; flush before exit/restart.</summary>
internal sealed class DeferredSettingsSave : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Action? _pending;
    internal DeferredSettingsSave() { _timer.Tick += (_,_) => Flush(); }
    internal void Schedule(Action save)
    {
        _pending=save;_timer.Stop();_timer.Start();
    }
    internal void Flush()
    {
        _timer.Stop();var save=_pending;_pending=null;save?.Invoke();
    }
    public void Dispose() => Flush();
}

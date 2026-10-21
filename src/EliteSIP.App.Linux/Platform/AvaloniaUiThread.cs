using Avalonia.Threading;

namespace EliteSIP.App.Platform;

/// <summary>Поток окна Avalonia — Linux-половина <c>WpfUiThread</c>.</summary>
///
/// <remarks>
/// Таймеры — с приоритетом <see cref="DispatcherPriority.Background"/>: после
/// ввода и отрисовки, как <c>DispatcherTimer</c> WPF без явного приоритета
/// (<see cref="IUiThread.CreateTimer"/>).
/// </remarks>
internal sealed class AvaloniaUiThread : IUiThread
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public IUiTimer CreateTimer(TimeSpan interval) => new Timer(interval);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        internal Timer(TimeSpan interval)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
            _timer.Tick += (sender, e) => Tick?.Invoke(this, e);
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsEnabled => _timer.IsEnabled;

        public event EventHandler? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}

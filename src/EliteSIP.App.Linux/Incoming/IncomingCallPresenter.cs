using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using EliteSIP.App.Platform;
using EliteSIP.CallGuard;

namespace EliteSIP.App.Incoming;

/// <summary>
/// Показ входящего — Linux-половина <c>Incoming/IncomingCallPresenter</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// <b>Временная, до W-L6.</b> Здесь то, без чего на вызов нельзя ответить:
/// окно поверх всех, рингтон, мигание панели, проверка цифрой той же
/// <see cref="CallGuardSession"/>, что на Windows. Чего нет и что приносит
/// W-L6: путь курсора по всему экрану (XInput2 — здесь только движения над
/// самим окном), признак нажатия, присланного XTest, случайное место под
/// курсором и показ без активации на Muffin (<c>WM_HINTS.input = False</c>,
/// W-L0Б).
/// </para>
/// <para>
/// Нажатие поэтому считается живым всегда, и в журнал это пишется: отчёт
/// защиты без источника нажатия читать как полный нельзя.
/// </para>
/// </remarks>
public sealed class IncomingCallPresenter : IIncomingCallView, IDisposable
{
    private readonly Action<string> _log;
    private readonly Func<Window?> _panel;
    private IncomingCallWindow? _window;
    private IncomingCallViewModel? _model;
    private CallGuardSession? _session;
    private Stopwatch? _clock;
    private Action? _onAnswer;
    private Action? _onDecline;

    public IncomingCallPresenter(Action<string> log, Func<Window?> panel)
    {
        _log = log;
        _panel = panel;
    }

    public bool IsVisible => _window is not null;

    public Action? StartRingtone { get; set; }

    public Action? StopRingtone { get; set; }

    public CallGuardReport? LastReport { get; private set; }

    public void Show(IncomingCallSubject subject, CallGuardPolicy policy, Action onAnswer, Action onDecline)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Hide();

        policy = policy.Normalized();
        _clock = Stopwatch.StartNew();
        _session = new CallGuardSession(policy, TimeSpan.Zero, Random.Shared);
        LastReport = _session.Report;
        _onAnswer = onAnswer;
        _onDecline = onDecline;

        _model = new IncomingCallViewModel(subject, _session.Challenge, policy.IsEnabled, Attempt, Decline);
        var window = new IncomingCallWindow(_model);
        _window = window;

        window.PointerMoved += OnPointerMoved;
        window.Opened += (_, _) => Center(window);
        window.Show();

        if (_panel() is { } panel)
        {
            global::EliteSIP.Platform.Linux.WindowAttention.Request(Handle(panel));
        }

        StartRingtone?.Invoke();
        _log("окно входящего: показ без защиты курсора и источника нажатия (до W-L6)");
    }

    public void Hide()
    {
        StopRingtone?.Invoke();

        if (_window is not null && _panel() is { } panel)
        {
            global::EliteSIP.Platform.Linux.WindowAttention.Cancel(Handle(panel));
        }

        if (_session is not null)
        {
            LastReport = _session.Report;
        }

        if (_window is not null)
        {
            _window.PointerMoved -= OnPointerMoved;
            _window.Close();
            _window = null;
        }

        _session = null;
        _model = null;
        _clock = null;
        _onAnswer = null;
        _onDecline = null;
    }

    public void Dispose() => Hide();

    private static nuint Handle(Window window) => (nuint)(window.TryGetPlatformHandle()?.Handle ?? 0);

    private void Attempt(char target)
    {
        if (_session is not CallGuardSession session || _clock is not Stopwatch clock)
        {
            return;
        }

        var verdict = session.Evaluate(new CallGuardAttempt(target, false, clock.Elapsed));
        LastReport = session.Report;

        if (verdict.Rejection is CallGuardRejection reason)
        {
            if (_model is not null)
            {
                _model.Refusal = reason.OperatorMessage();
            }

            _log($"вызов не принят: {reason.LogMessage()}");
            return;
        }

        var answer = _onAnswer;
        _log($"вызов принят: {session.Report.Summary()}");
        Hide();
        answer?.Invoke();
    }

    private void Decline()
    {
        var decline = _onDecline;
        Hide();
        decline?.Invoke();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_session is null || _window is null)
        {
            return;
        }

        var point = _window.PointToScreen(e.GetPosition(_window));
        _session.NoteCursor(new ScreenPoint(point.X, point.Y));
    }

    private static void Center(Window window)
    {
        if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen)
        {
            return;
        }

        var work = screen.WorkingArea;
        var size = PixelSize.FromSize(window.Bounds.Size, screen.Scaling);
        window.Position = new PixelPoint(work.X + ((work.Width - size.Width) / 2), work.Y + ((work.Height - size.Height) / 2));
    }
}

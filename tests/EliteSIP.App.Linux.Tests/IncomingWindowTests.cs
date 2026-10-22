using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Incoming;
using EliteSIP.CallGuard;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

/// <summary>
/// Окно входящего (W-L6): цели приёма недоступны ни AT-SPI, ни клавиатуре,
/// размер до показа совпадает с показанным, а разбор происхождения нажатия
/// метит всё, что пришло не от живой мыши.
/// </summary>
public sealed class IncomingWindowTests
{
    public static TheoryData<int> TargetCounts() => [1, 3];

    [AvaloniaTheory]
    [MemberData(nameof(TargetCounts))]
    public void Цели_приёма_без_объекта_автоматизации_и_без_фокуса(int targets)
    {
        using BindingErrors errors = new();
        var window = Window(targets);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToList();
        var guarded = buttons.OfType<GuardedButton>().ToList();
        var others = buttons.Except(guarded).ToList();

        // Одна «Ответить» или ряд цифр — по числу целей.
        Assert.Equal(targets, guarded.Count);
        foreach (var target in guarded)
        {
            Assert.False(target.Focusable);
            Assert.IsType<NoneAutomationPeer>(ControlAutomationPeer.CreatePeerForElement(target));
        }

        // «Отклонить» доступность сохраняет: отказаться можно и без мыши.
        var decline = Assert.Single(others);
        var peer = ControlAutomationPeer.CreatePeerForElement(decline);
        Assert.IsType<ButtonAutomationPeer>(peer);
        Assert.False(string.IsNullOrEmpty(peer.GetName()));

        Snapshot.Save(window.CaptureRenderedFrame()!, $"incoming-{targets}");
        window.Close();
        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
    }

    /// <summary>
    /// Место считается до показа по замеренному размеру, и показ его не
    /// меняет: высота, которую поставили окну до отображения, — та же,
    /// что у показанного. Иначе Muffin вдвигал бы окно по чужой высоте.
    /// </summary>
    [AvaloniaFact]
    public void Замер_до_показа_совпадает_с_показанным_окном()
    {
        var window = Window(targets: 1);
        var measured = window.MeasureFrame(scaling: 1);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(320, measured.Width);
        Assert.Equal(measured.Height, Math.Ceiling(window.ClientSize.Height), 0.5);
        window.Close();
    }

    [AvaloniaFact]
    public void Слежения_нет_значит_признаков_нет()
        => Assert.False(IncomingCallPresenter.Classify(watching: false, press: null, 0, 0).IsSynthetic);

    [AvaloniaFact]
    public void Живая_мышь_и_живой_путь_не_синтетика()
    {
        var (synthetic, text) = IncomingCallPresenter.Classify(true, Press(8, "Telink Wireless Receiver Mouse", xtest: false), 0, 69);

        Assert.False(synthetic);
        Assert.Contains("Telink", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Нажатие_от_XTest_синтетика()
        => Assert.True(IncomingCallPresenter.Classify(true, Press(4, "Virtual core XTEST pointer", xtest: true), 0, 30).IsSynthetic);

    /// <summary>Рука живая, а курсор у окна вёл скрипт — это видно только на Linux.</summary>
    [AvaloniaFact]
    public void Путь_через_XTest_синтетика_даже_при_живом_нажатии()
        => Assert.True(IncomingCallPresenter.Classify(true, Press(8, "Telink Wireless Receiver Mouse", xtest: false), 5, 40).IsSynthetic);

    /// <summary>Кнопку нажали без мыши: действием AT-SPI или событием XSendEvent.</summary>
    [AvaloniaFact]
    public void Нет_нажатия_мышью_синтетика()
    {
        var (synthetic, text) = IncomingCallPresenter.Classify(true, press: null, 0, 0);

        Assert.True(synthetic);
        Assert.Contains("AT-SPI", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void XTest_узнаётся_по_имени_от_сервера()
    {
        Assert.True(PointerWatch.IsXTest("Virtual core XTEST pointer"));
        Assert.False(PointerWatch.IsXTest("Telink Wireless Receiver Mouse"));
    }

    private static PointerPress Press(int id, string device, bool xtest)
        => new(id, device, xtest, System.Diagnostics.Stopwatch.GetTimestamp());

    private static IncomingCallWindow Window(int targets)
    {
        var policy = new CallGuardPolicy { TargetCount = targets }.Normalized();
        var challenge = CallGuardChallenge.Build(policy, new Random(7));
        var subject = IncomingCallSubject.Classify(
            "79161234567", "Иван", requestsAutoAnswer: false, ownNumber: "100", masksMobileNumbers: true);
        var model = new IncomingCallViewModel(subject, challenge, isGuarded: true, _ => { }, () => { });
        return new IncomingCallWindow(model);
    }
}

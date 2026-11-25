using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using EliteSIP.App.Panel;

namespace EliteSIP.App.Tests;

/// <summary>Раскладка панели — то, что LayoutTests проверяли на WPF.</summary>
public sealed class PanelLayoutTests
{
    [AvaloniaFact]
    public void Панель_поднимается_и_рисуется()
    {
        PanelViewModel model = new() { StatusTitle = "701", StatusLabel = "Офис" };
        PanelWindow window = new(model) { AllowsClosing = true };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Snapshot.Save(frame!, "panel-idle");

        window.Close();
    }

    [AvaloniaFact]
    public void Таймер_работает_только_пока_есть_линия()
    {
        var model = new PanelViewModel();
        var window = new PanelWindow(model) { AllowsClosing = true };
        var clock = (DispatcherTimer)typeof(PanelWindow)
            .GetField("_clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        window.Show();
        try
        {
            Assert.False(clock.IsEnabled);
            model.PendingNumber = "701";
            Assert.False(clock.IsEnabled);
            model.Lines.Add(new CallLineViewModel { CallId = "one", ConnectedAt = DateTimeOffset.Now });
            Assert.True(clock.IsEnabled);
            model.Lines.Clear();
            Assert.False(clock.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Первый_показ_вытянутый_четыре_к_трём()
        => Assert.Equal(432, PanelWindow.InitialHeight(width: 324, content: 220, room: 1000));

    [Fact]
    public void Содержимое_выше_телефона_берёт_своё()
        => Assert.Equal(520, PanelWindow.InitialHeight(width: 324, content: 520, room: 1000));

    [Fact]
    public void Не_выше_экрана()
        => Assert.Equal(400, PanelWindow.InitialHeight(width: 324, content: 220, room: 400));

    [AvaloniaFact]
    public void Без_сохранённого_размера_окно_вытянуто()
    {
        PanelViewModel model = new() { StatusTitle = "701", StatusLabel = "Офис" };
        PanelWindow window = new(model) { AllowsClosing = true };
        window.Show();

        Assert.Equal(SizeToContent.Manual, window.SizeToContent);
        Assert.True(window.Height >= window.Width * PanelWindow.PhoneAspect - 1,
            $"окно {window.Width:0}×{window.Height:0} — не вытянуто");
        Assert.True(window.Height >= window.MinHeight, "ниже содержимого");

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Snapshot.Save(frame!, "panel-phone");

        window.Close();
    }

    [AvaloniaFact]
    public void Сохранённый_размер_оператора_важнее()
    {
        PanelViewModel model = new() { StatusTitle = "701", StatusLabel = "Офис" };
        PanelWindow window = new(model) { AllowsClosing = true, RestoreSize = () => (400, 560) };
        window.Show();

        Assert.Equal(400, window.Width);
        Assert.Equal(Math.Max(560, window.MinHeight), window.Height);

        window.Close();
    }

    [AvaloniaFact]
    public void Время_справа_внизу_а_номер_и_состояние_под_именем()
    {
        var model = new PanelViewModel { CallStatus = "Разговор" };
        var line = new CallLineViewModel { CallId = "one", SecondaryNumber = "172", Title = "Очень длинное название собеседника", IsActive = true, ConnectedAt = DateTimeOffset.Now.AddSeconds(-34) };
        model.Lines.Add(line);
        model.ActiveLine = line;
        var window = new PanelWindow(model) { AllowsClosing = true, Width = 324, Height = 700 };
        window.Show();
        try
        {
            model.Tick();
            Dispatcher.UIThread.RunJobs();
            Snapshot.Save(window.CaptureRenderedFrame()!, "panel-call-header");
            Rect Box(string name)
            {
                var control = window.FindControl<Control>(name)!;
                return new Rect(control.TranslatePoint(new Point(0, 0), window)!.Value, control.Bounds.Size);
            }

            var title = Box("CallTitleLabel");
            var duration = Box("CallDuration");
            Assert.True(duration.Top >= title.Bottom - 1);
            Assert.True(duration.Height < 30);
            Assert.True(Box("CallNumberPart").Width > 0);
            Assert.InRange(Box("CallStatusLabel").Left - Box("CallNumberPart").Right, -1, 1);

            // Без номера состояние встаёт на его место у левого края.
            line.SecondaryNumber = string.Empty;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.InRange(Box("CallStatusLabel").Left - title.Left, -1, 1);
            Snapshot.Save(window.CaptureRenderedFrame()!, "panel-call-header-no-number");
        }
        finally
        {
            window.Close();
        }
    }
}

using Avalonia.Controls;
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
}

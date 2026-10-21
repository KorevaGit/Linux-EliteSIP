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
}

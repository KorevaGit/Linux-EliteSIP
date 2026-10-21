using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.AdminAccess;
using EliteSIP.App.FirstRun;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>
/// Раскладка — перевод <c>LayoutTests</c> Windows-версии на Avalonia Headless:
/// шаги мастера, подписи клавиш, растяжение панели. Что и почему проверяется,
/// сказано там; числа те же.
/// </summary>
public sealed class LayoutTests
{
    public static TheoryData<FirstRunStep, string> Steps()
    {
        var data = new TheoryData<FirstRunStep, string>();
        foreach (var step in Enum.GetValues<FirstRunStep>())
        {
            data.Add(step, "Light");
            data.Add(step, "Dark");
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Steps))]
    public void Шаг_мастера_помещается_без_прокрутки(FirstRunStep step, string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        try
        {
            var (window, scroll, shown) = Wizard(step);

            var extra = scroll.Extent.Height - scroll.Viewport.Height;
            var parts = string.Join(", ", shown.Children.Select(child => $"{child.GetType().Name}:{child.Bounds.Height:0}"));
            Assert.True(extra <= 0.5, $"шаг {step} просит на {extra:0} точек больше, чем есть ({scroll.Viewport.Height:0}): {parts}");

            window.Hide();
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    [AvaloniaTheory]
    [InlineData(FirstRunStep.Welcome)]
    [InlineData(FirstRunStep.Appearance)]
    [InlineData(FirstRunStep.Finale)]
    public void Короткий_шаг_стоит_по_центру_а_не_у_верха(FirstRunStep step)
    {
        var (window, scroll, shown) = Wizard(step);

        var top = shown.TranslatePoint(default, scroll)!.Value.Y;
        var bottom = scroll.Viewport.Height - top - shown.Bounds.Height;
        Assert.InRange(Math.Abs(top - bottom), 0, 2);

        window.Hide();
    }

    [AvaloniaFact]
    public void Предел_клавиш_не_трогается_когда_всё_влезает()
        => Assert.Null(PanelWindow.FitMacroHeight(content: 700, room: 900, rows: 6, macroHeight: 60));

    /// <summary>
    /// Шесть рядов по 60 на экране, где их не хватает на 100 точек, отдают по
    /// ⌈100 / 6⌉ = 17. Увеличения у Linux-панели нет (на Windows его предел
    /// тоже 1,0), поэтому и множителя в формуле нет.
    /// </summary>
    [AvaloniaFact]
    public void Клавиши_ужимаются_до_экрана_поровну_на_ряд()
    {
        Assert.Equal(43, PanelWindow.FitMacroHeight(content: 1100, room: 1000, rows: 6, macroHeight: 60));
    }

    [AvaloniaFact]
    public void Клавиша_не_становится_ниже_предела()
    {
        Assert.Equal(
            PanelWindow.MinimumMacroHeight,
            PanelWindow.FitMacroHeight(content: 3000, room: 600, rows: 10, macroHeight: 60));
    }

    [AvaloniaTheory]
    [InlineData(60)]
    [InlineData(44)]
    [InlineData(34)]
    public void Подпись_клавиши_не_вылезает_за_клавишу(double limit)
    {
        var model = Panel(macroHeight: 60);
        model.MacroHeightLimit = limit;
        var frame = Layout(model, 254, double.PositiveInfinity);

        var boxes = frame.GetVisualDescendants().OfType<ShrinkBox>().Where(box => box.Bounds.Height > 0).ToList();
        Assert.NotEmpty(boxes);

        foreach (var box in boxes)
        {
            var key = box.FindAncestorOfType<Button>()!;
            var text = (TextBlock)box.Child!;
            Assert.True(text.DesiredSize.Height <= key.Bounds.Height + 0.5,
                $"«{text.Text}»: подпись {text.DesiredSize.Height:0} при клавише {key.Bounds.Height:0}");
            Assert.True(key.Bounds.Height <= limit + 0.5, $"«{text.Text}»: клавиша {key.Bounds.Height:0} выше предела {limit}");
        }
    }

    [AvaloniaFact]
    public void Растянутое_окно_с_клавишами_растит_управление_до_102_остальное_клавишам()
    {
        var natural = MeasurePanel(double.PositiveInfinity, withMacros: true);
        var tall = MeasurePanel(1000, withMacros: true);

        Assert.Equal(68, natural.Controls, 0.5);
        Assert.Equal(102, tall.Controls, 0.5);
        Assert.True(tall.Key > natural.Key + 20, $"клавиша {natural.Key:0} → {tall.Key:0}");
        Assert.True(tall.Font > natural.Font + 4, $"кегль {natural.Font:0.#} → {tall.Font:0.#}");
        Assert.Equal(natural.Bottom, tall.Bottom, 0.5);
    }

    [AvaloniaFact]
    public void Окно_минимальной_высоты_не_срезает_клавиши()
    {
        var natural = Layout(Panel(macroHeight: 44, withMacros: true), 300, double.PositiveInfinity).DesiredSize.Height;

        Assert.Equal(68, MeasurePanel(natural, withMacros: true).Controls, 0.5);
    }

    [AvaloniaFact]
    public void Без_клавиш_растягивание_целиком_уходит_кнопкам_управления()
    {
        var natural = MeasurePanel(double.PositiveInfinity, withMacros: false);
        var tall = MeasurePanel(700, withMacros: false);

        Assert.True(tall.Controls > natural.Controls + 200, $"управление {natural.Controls:0} → {tall.Controls:0}");
        Assert.Equal(natural.Bottom, tall.Bottom, 0.5);
    }

    [AvaloniaFact]
    public void Пробный_замер_окна_не_сбрасывает_выросший_кегль()
    {
        var frame = Layout(Panel(macroHeight: 44, withMacros: true), 300, 1000);
        var text = (TextBlock)frame.GetVisualDescendants().OfType<ShrinkBox>().First(box => box.Bounds.Height > 0).Child!;
        var grown = text.FontSize;

        // Пробный замер на бесконечной высоте — то, чем окно узнаёт свой
        // нижний предел (PanelWindow.UpdateMinimumHeight), — и раскладка обратно.
        frame.Measure(new Size(300, double.PositiveInfinity));
        frame.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();

        Assert.True(grown > 20, $"кегль не вырос: {grown:0.#}");
        Assert.Equal(grown, text.FontSize, 0.5);
    }

    private static readonly string[] Titles =
    [
        "Квартиры 8+", "Квартиры 8+ ТОП", "Квартиры 15 +", "Квартиры 15 + ТОП",
        "Кв и дома 25 +", "Квартиры и дома 45+", "Участки СБ остальные",
        "Квартиры и дома 100+", "Вернуть клиента", "Кнопка для промаха",
    ];

    private static (FirstRunWindow Window, ScrollViewer Scroll, StackPanel Shown) Wizard(FirstRunStep step)
    {
        var model = new FirstRunViewModel(new AppSettings(), new AdminAccessState());

        // Шаг ставится напрямую: пройти к нему по-честному значило бы
        // заполнять поля и ходить в сеть привязки.
        typeof(FirstRunViewModel).GetProperty(nameof(FirstRunViewModel.Step))!
            .SetValue(model, step, BindingFlags.NonPublic | BindingFlags.Instance, null, null, null);

        var window = new FirstRunWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = window.StepScroll;
        var shown = ((Grid)scroll.Content!).Children.OfType<StackPanel>().First(panel => panel.IsVisible);
        return (window, scroll, shown);
    }

    private static PanelViewModel Panel(double macroHeight, bool withMacros = true)
    {
        var model = new PanelViewModel { MacroColumns = 2, MacroHeight = macroHeight };
        if (withMacros)
        {
            foreach (var title in Titles)
            {
                model.Macros.Add(new MacroViewModel(title, "1"));
            }
        }

        return model;
    }

    /// <summary>
    /// Рамка панели, разложенная в заданный размер, как WpfHost.Layout:
    /// бесконечная высота — окно по содержимому, иначе ровно такой высоты.
    /// </summary>
    private static Border Layout(PanelViewModel model, double width, double height)
    {
        var window = new PanelWindow(model);
        var frame = window.Frame;
        window.Content = null;

        // Окно здесь только хозяин стилей и модели; рамка — как в панели.
        // Естественная высота — в прокрутке: она меряет содержимое на
        // бесконечной высоте и раскладывает по желаемой, ровно как
        // WpfHost.Layout с бесконечной высотой. Окно без экрана по
        // содержимому само не сжимается.
        // Прокрутка Avalonia растягивает содержимое до себя — прижимаем к верху.
        if (double.IsInfinity(height))
        {
            frame.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }

        var host = new Window
        {
            Content = double.IsInfinity(height) ? new ScrollViewer { Content = frame } : frame,
            DataContext = model,
            Width = width,
            Height = double.IsInfinity(height) ? 2000 : height,
        };

        window.RelayoutForTests();
        host.Show();
        Dispatcher.UIThread.RunJobs();
        return frame;
    }

    private static (double Key, double Bottom, double Controls, double Font) MeasurePanel(double height, bool withMacros)
    {
        var frame = Layout(Panel(macroHeight: 44, withMacros), 300, height);
        var root = (Grid)frame.Child!;
        var middle = root.Children.OfType<Grid>().Single(grid => Grid.GetRow(grid) == 2);
        var controls = middle.RowDefinitions[2].ActualHeight;
        var bottom = root.RowDefinitions[4].ActualHeight;

        var box = frame.GetVisualDescendants().OfType<ShrinkBox>().FirstOrDefault(item => item.Bounds.Height > 0);
        if (box is null)
        {
            return (0, bottom, controls, 0);
        }

        var key = box.FindAncestorOfType<Button>()!;
        var text = (TextBlock)box.Child!;
        Assert.True(text.DesiredSize.Height <= key.Bounds.Height + 0.5, $"подпись {text.DesiredSize.Height:0} при клавише {key.Bounds.Height:0}");
        Assert.True(key.Bounds.Height >= 44 - 0.5, $"клавиша срезана до {key.Bounds.Height:0}");
        return (key.Bounds.Height, bottom, controls, text.FontSize);
    }
}

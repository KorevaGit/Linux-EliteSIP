using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EliteSIP.App.Theme;

/// <summary>
/// Состояние окна — кнопкам рамки, которую рисует Avalonia: развёрнуто ли
/// окно и активно ли оно.
/// </summary>
///
/// <remarks>
/// <para>
/// Кнопки рамки (Theme/Controls.axaml, «Рамка окна») сделаны под тему окон
/// Cinnamon, и двух вещей им не хватает: значок «восстановить» у развёрнутого
/// окна и серый «закрыть» у неактивного — так их рисует Muffin. Сама Avalonia
/// развёрнутость показывает подменой контура внутри своей кнопки, без класса,
/// а активность окна кнопкам не сообщает вовсе.
/// </para>
/// <para>
/// Кнопки живут не в окне, а рядом с ним — в слое рамки над окном (корень
/// дерева — <c>TopLevelHost</c>), поэтому <c>$parent[Window]</c> до окна из
/// разметки не дотягивается, и классы ставятся отсюда.
/// </para>
/// </remarks>
internal static class DrawnCaption
{
    internal const string Restore = "restore";

    internal const string Inactive = "inactive";

    /// <summary>Подписывается на все окна приложения. Зовётся один раз при запуске.</summary>
    internal static void Register()
    {
        Window.WindowStateProperty.Changed.AddClassHandler<Window>((window, _) => Update(window));
        WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>((window, _) => Update(window));

        // Рамка собирается при показе окна, а не при создании: до него
        // кнопок ещё нет.
        Window.WindowOpenedEvent.AddClassHandler<Window>(
            (window, _) => Dispatcher.UIThread.Post(
                () =>
                {
                    Update(window);
                    TintFrame(window);
                },
                DispatcherPriority.Loaded));
    }

    /// <summary>
    /// Кромка рамки — кистью разделителя, а не <c>ThemeBorderMidBrush</c>.
    /// </summary>
    ///
    /// <remarks>
    /// Шаблон рамки SimpleTheme красит кромку <c>ThemeBorderMidBrush</c> —
    /// кистью рамок полей ввода, на чёрном окне тёмной темы она светло-серая
    /// и смотрится белой обводкой. Значение задано в самом шаблоне, а оно у
    /// Avalonia сильнее стиля, поэтому кисть ставится привязкой отсюда; она
    /// динамическая и перекрашивается вместе с темой.
    /// </remarks>
    private static void TintFrame(Window window)
    {
        foreach (var border in Root(window).GetVisualDescendants()
                     .OfType<Border>()
                     .Where(border => border.Name is "PART_WindowBorder"))
        {
            border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("SeparatorBrush"));
        }
    }

    /// <summary>Классы кнопкам рамки этого окна по его нынешнему состоянию.</summary>
    internal static void Update(Window window)
    {
        foreach (var button in Buttons(window))
        {
            button.Classes.Set(Restore, window.WindowState is WindowState.Maximized);
            button.Classes.Set(Inactive, !window.IsActive);
        }
    }

    /// <summary>Кнопки «свернуть», «развернуть» и «закрыть» рамки окна.</summary>
    internal static IEnumerable<Button> Buttons(Window window)
    {
        return Root(window).GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton");
    }

    /// <summary>Корень слоя рамки над окном — <c>TopLevelHost</c>.</summary>
    private static Visual Root(Window window)
    {
        Visual root = window;
        while (root.GetVisualParent() is { } parent)
        {
            root = parent;
        }

        return root;
    }
}

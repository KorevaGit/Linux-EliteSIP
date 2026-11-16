using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace EliteSIP.App.Theme;

/// <summary>Product chrome; move, resize and maximize are delegated to the Linux window manager.</summary>
internal static class DrawnCaption
{
    internal const string Restore = "restore";
    internal const string Inactive = "inactive";
    internal static void Register()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
        Window.WindowStateProperty.Changed.AddClassHandler<Window>((window, _) => Update(window));
        WindowBase.IsActiveProperty.Changed.AddClassHandler<Window>((window, _) => Update(window));
    }
    internal static void Attach(Window window)
    {
        if (!Program.DrawsOwnFrame || window.GetType().Assembly != typeof(App).Assembly || window is Incoming.IncomingCallWindow || window.WindowDecorations == WindowDecorations.None || window.Content is not Control content) { return; }
        window.WindowDecorations = WindowDecorations.None;
        window.Content = null;
        var layout = new Grid { RowDefinitions = new RowDefinitions("32,*") };
        var caption = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Background = Brushes.Transparent };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new Thickness(9,0), VerticalAlignment = VerticalAlignment.Center };
        var crown = new BrandMark { Width = 15, Height = 15 };
        crown.Bind(BrandMark.ForegroundProperty, window.GetResourceObservable("TextPrimaryBrush"));
        var title = new TextBlock { Name = "ProductCaption", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));
        if (window is Panel.PanelWindow panel)
        {
            title.FontSize = 14; title.FontWeight = FontWeight.SemiBold;
            title.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(Panel.PanelViewModel.StatusTitle))
            { Source = panel.Model, StringFormat = Resources.LinuxStrings.Get("PanelYourNumber") + ": {0}" });
        }
        title.Bind(TextBlock.ForegroundProperty, window.GetResourceObservable(window is Panel.PanelWindow ? "TextPrimaryBrush" : "TextSecondaryBrush"));
        heading.Children.Add(crown); heading.Children.Add(title); caption.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons,1); caption.Children.Add(buttons);
        Button Add(string glyph, string name, Action action)
        {
            var button = new Button { Name = name, Width = 38, Height = 32, Padding = new Thickness(0), Content = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            button.Classes.Add("caption");
            var label = Resources.Strings.Get(name == "PART_CloseButton" ? "WindowClose" : name == "PART_MinimizeButton" ? "WindowMinimize" : "WindowMaximize");
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            ToolTip.SetTip(button, label);
            button.Click += (_,_) => action(); buttons.Children.Add(button); return button;
        }
        Add("−", "PART_MinimizeButton", () => window.WindowState = WindowState.Minimized).IsVisible = window.CanResize;
        void Toggle() { if (window.CanResize) { window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; } }
        var maximize = Add("□", "PART_MaximizeButton", Toggle); maximize.IsVisible = window.CanResize;
        Add("×", "PART_CloseButton", window.Close).Classes.Add("close");
        caption.PointerPressed += (_,e) =>
        {
            if (e.Source is Visual source && (source is Button || source.GetVisualAncestors().OfType<Button>().Any())) { return; }
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) { return; }
            if (e.ClickCount == 2) { Toggle(); } else { window.BeginMoveDrag(e); }
            e.Handled = true;
        };
        layout.Children.Add(caption); Grid.SetRow(content,1); layout.Children.Add(content);
        var edge = new Border { BorderThickness = new Thickness(1), Child = layout };
        edge.Bind(Border.BorderBrushProperty,window.GetResourceObservable("ControlBorderBrush"));
        edge.Bind(Border.BackgroundProperty,window.GetResourceObservable("WindowBackgroundBrush"));
        window.Content = edge;
        // Borderless windows need explicit resize hit testing; the WM owns the drag.
        window.AddHandler(InputElement.PointerPressedEvent, (_,e) =>
        {
            if (!window.CanResize || window.WindowState != WindowState.Normal || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) { return; }
            var p = e.GetPosition(window); const int grip=5;
            var left=p.X<grip;var right=p.X>window.Bounds.Width-grip;var top=p.Y<grip;var bottom=p.Y>window.Bounds.Height-grip;
            WindowEdge? side = (left,right,top,bottom) switch
            {
                (true,_,true,_) => WindowEdge.NorthWest, (true,_,_,true) => WindowEdge.SouthWest,
                (_,true,true,_) => WindowEdge.NorthEast, (_,true,_,true) => WindowEdge.SouthEast,
                (true,_,_,_) => WindowEdge.West, (_,true,_,_) => WindowEdge.East,
                (_,_,true,_) => WindowEdge.North, (_,_,_,true) => WindowEdge.South, _ => null,
            };
            if(side is {} value) { window.BeginResizeDrag(value,e); e.Handled=true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    internal static void Update(Window window) { foreach(var button in Buttons(window)) { button.Classes.Set(Restore,window.WindowState==WindowState.Maximized);button.Classes.Set(Inactive,!window.IsActive);if (button.Name == "PART_MaximizeButton" && button.Content is TextBlock glyph) { glyph.Text = window.WindowState == WindowState.Maximized ? "❐" : "□"; } } }
    internal static IEnumerable<Button> Buttons(Window window) => window.GetVisualDescendants().OfType<Button>().Where(button=>button.Name is "PART_MinimizeButton" or "PART_MaximizeButton" or "PART_CloseButton");
}

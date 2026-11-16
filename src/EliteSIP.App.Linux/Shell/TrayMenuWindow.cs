using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Shell;

/// <summary>Product-owned tray menu, styled by the live application palette.</summary>
internal sealed class TrayMenuWindow : Window
{
    private bool _closing;
    private readonly PanelViewModel _model;
    private readonly Tray.TrayActions _actions;
    internal TrayMenuWindow(PanelViewModel model,Tray.TrayActions actions)
    {
        _model=model;_actions=actions;
        WindowDecorations=WindowDecorations.None;ShowInTaskbar=false;CanResize=false;Topmost=true;
        Background=Brushes.Transparent;TransparencyLevelHint=[WindowTransparencyLevel.Transparent];
        Width=280;SizeToContent=SizeToContent.Height;WindowStartupLocation=WindowStartupLocation.Manual;
        Title="EliteSIP";Deactivated+=(_,_)=>Hide();KeyDown+=OnMenuKey;
    }
    internal void ClosePermanently() { _closing=true;Close(); }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if(!_closing) { e.Cancel=true;Hide(); }
        base.OnClosing(e);
    }
    private void OnMenuKey(object? sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape) { Hide();e.Handled=true;return; }
        if(e.Key is not (Key.Up or Key.Down or Key.Home or Key.End)) { return; }
        var buttons=this.GetVisualDescendants().OfType<Button>().Where(button=>button.IsEffectivelyVisible&&button.IsEnabled).ToArray();
        if(buttons.Length==0) { return; }
        var index=Array.FindIndex(buttons,button=>button.IsKeyboardFocusWithin);
        index=e.Key switch { Key.Home=>0,Key.End=>buttons.Length-1,Key.Up=>(index<=0?buttons.Length:index)-1,_=>(index+1)%buttons.Length };
        buttons[index].Focus();e.Handled=true;
    }
    internal void Refresh(string status)
    {
        var rows=new StackPanel { Spacing=2 };
        var heading=new TextBlock { Text=status, FontSize=12, FontWeight=FontWeight.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(9,8,9,6) };
        heading.Bind(TextBlock.ForegroundProperty,this.GetResourceObservable("TextSecondaryBrush"));rows.Children.Add(heading);
        void Separator()
        {
            var line=new Border { Height=1,Margin=new Thickness(6,5) };line.Bind(Border.BackgroundProperty,this.GetResourceObservable("SeparatorBrush"));rows.Children.Add(line);
        }
        Button Add(string label,string glyph,Action action,bool enabled=true,bool check=false)
        {
            var content=new Grid { ColumnDefinitions=new ColumnDefinitions("22,*,18") };
            var icon=new Icon { Glyph=glyph,Size=17,VerticalAlignment=VerticalAlignment.Center };content.Children.Add(icon);
            var text=new TextBlock { Text=label,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(text,1);content.Children.Add(text);
            if(check) { var tick=new Icon { Glyph="settings.check",Size=14,VerticalAlignment=VerticalAlignment.Center };Grid.SetColumn(tick,2);content.Children.Add(tick); }
            var button=new Button { Content=content,IsEnabled=enabled,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,MinHeight=38,Padding=new Thickness(9,8) };
            button.Classes.Add("tray-menu");button.Click+=(_,_)=> { Hide();action(); };rows.Children.Add(button);return button;
        }
        Separator();
        Add(Strings.Get(_actions.IsPanelVisible()?"TrayHidePanel":"TrayShowPanel"),"settings.monitor",_actions.TogglePanel);
        Add(Strings.Get("TrayHistory"),"clock",_actions.ShowHistory);
        Add(Strings.Get("TraySettings"),"settings.sliders-horizontal",_actions.ShowSettings);
        Separator();Add(Strings.Get("TrayDoNotDisturb"),"settings.power",_actions.ToggleOffline,!_model.IsInCall,_model.IsOfflineByChoice);
        Separator();Add(Strings.Get("TrayQuit"),"settings.unplug",_actions.Quit);
        var frame=new Border { Child=rows,Padding=new Thickness(6),Margin=new Thickness(6),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(0,1,0,0),BoxShadow=BoxShadows.Parse("0 4 12 0 #38000000") };
        frame.Bind(Border.BackgroundProperty,this.GetResourceObservable("SurfaceBrush"));frame.Bind(Border.BorderBrushProperty,this.GetResourceObservable("ControlBorderBrush"));Content=frame;
    }
    internal void OpenAt(int x,int y)
    {
        Position=new PixelPoint(x,y);Show();
        var screen=Screens.ScreenFromPoint(new PixelPoint(x,y))??Screens.Primary;
        if(screen is null) { return; }
        var area=screen.WorkingArea;var width=(int)Math.Ceiling(ClientSize.Width*screen.Scaling);var height=(int)Math.Ceiling(ClientSize.Height*screen.Scaling);
        Position=new PixelPoint(Math.Clamp(x,area.X,Math.Max(area.X,area.Right-width)),Math.Clamp(y,area.Y,Math.Max(area.Y,area.Bottom-height)));
        Activate();
    }
}

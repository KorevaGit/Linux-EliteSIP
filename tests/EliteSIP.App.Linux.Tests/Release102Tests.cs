using Avalonia.Headless.XUnit;
using EliteSIP.App;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Panel;

using EliteSIP.App.Theme;
namespace EliteSIP.App.Tests;
public sealed class Release102Tests
{
    [AvaloniaFact]
    public void Every_packaged_icon_contains_visible_geometry()
    {
        Assert.NotEmpty(IconLibrary.Find("settings.play")!.Strokes);
        Assert.All(IconLibrary.All, pair=>Assert.NotEmpty(pair.Value.Strokes));
    }
    [Fact]
    public void Replay_volume_is_live_and_zero_mutes_while_two_doubles_samples()
    {
        double volume=0;
        var samples=new short[30];Array.Fill(samples,(short)2000);
        var playback=new RecordingSamples(samples,8000,()=>volume);
        var output=new float[10];playback.Read(output);Assert.All(output,x=>Assert.Equal(0,x));
        volume=1;playback.Read(output);var normal=output[0];Assert.True(normal>0);
        volume=2;playback.Read(output);Assert.Equal(normal*2,output[0],5);
    }
    [AvaloniaFact]
    public void Product_scrollbars_have_pills_and_drag_in_both_directions()
    {
        var scroll=new ScrollViewer {HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new Border {Width=3000,Height=3000}};
        var window=new Window {Width=320,Height=240,Content=scroll};window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var bars=window.GetVisualDescendants().OfType<ScrollBar>().Where(b=>b.IsVisible).ToArray();Assert.Equal(2,bars.Length);
            foreach(var bar in bars)
            {
                var thumb=Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
                Assert.Contains("product-scroll-thumb",thumb.Classes);
                var horizontal=bar.Orientation==Avalonia.Layout.Orientation.Horizontal;
                Assert.Equal(12,horizontal?bar.Bounds.Height:bar.Bounds.Width);
                var point=thumb.TranslatePoint(new Point(thumb.Bounds.Width/2,thumb.Bounds.Height/2),window)!.Value;
                window.MouseMove(point);window.MouseDown(point,MouseButton.Left);
                window.MouseMove(point+(horizontal?new Vector(40,0):new Vector(0,40)));window.MouseUp(point+(horizontal?new Vector(40,0):new Vector(0,40)),MouseButton.Left);
                Dispatcher.UIThread.RunJobs();Assert.True(horizontal?scroll.Offset.X>0:scroll.Offset.Y>0);
            }
            Snapshot.Save(window.CaptureRenderedFrame()!,"product-scrollbars");
        }
        finally {window.Close();}
    }
    [AvaloniaFact]
    public void Profile_menu_exposes_real_quit_command()
    {
        var called=0;var model=new PanelViewModel {Quit=new RelayCommand(_=>called++)};
        var panel=new PanelWindow(model) {AllowsClosing=true};panel.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            panel.ProfileMenu.Open(panel.ProfilePill);Dispatcher.UIThread.RunJobs();
            var item=panel.ProfileMenu.Items.OfType<MenuItem>().Single(x=>x.Name=="QuitMenuItem");
            Assert.Same(model.Quit,item.Command);item.Command!.Execute(null);Assert.Equal(1,called);
        }
        finally {panel.Close();}
    }
    [AvaloniaFact]
    public void Window_caption_controls_share_vector_size_and_switch_restore_shape()
    {
        var panel=new PanelWindow(new PanelViewModel()) {AllowsClosing=true};panel.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var buttons=DrawnCaption.Buttons(panel).ToArray();Assert.Equal(3,buttons.Length);
            Assert.All(buttons,button=>{var glyph=Assert.IsType<CaptionGlyph>(button.Content);Assert.Equal(12,glyph.Width);Assert.Equal(12,glyph.Height);Assert.Equal(38,button.Width);});
            panel.WindowState=WindowState.Maximized;Dispatcher.UIThread.RunJobs();
            Assert.Equal("Restore",Assert.IsType<CaptionGlyph>(buttons.Single(b=>b.Name=="PART_MaximizeButton").Content).Kind);
        }
        finally {panel.Close();}
    }
    [AvaloniaFact]
    public void History_row_has_no_hover_or_selection_background()
    {
        var window=new EliteSIP.App.History.CallHistoryWindow();window.HistoryList.ItemsSource=new[]{"fixture"};window.HistoryList.IsVisible=true;window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            var item=Assert.Single(window.HistoryList.GetVisualDescendants().OfType<ListBoxItem>());
            window.HistoryList.SelectedIndex=0;
            var point=item.TranslatePoint(new Point(20,10),window)!.Value;window.MouseMove(point);Dispatcher.UIThread.RunJobs();
            var presenter=item.GetVisualDescendants().OfType<ContentPresenter>().Single(c=>c.Name=="PART_ContentPresenter");
            Assert.Equal(0,Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color.A);
        }
        finally {window.Close();}
    }
}

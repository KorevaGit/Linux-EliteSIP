using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;
using EliteSIP.App.Shell;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

public sealed class TrayUiTests
{
    [AvaloniaFact]
    public void Panel_has_live_uppercase_number_dot_and_Windows_settings_icon()
    {
        var old=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        var model=new PanelViewModel { StatusTitle="701",Registration=RegistrationState.Registered };
        var window=new PanelWindow(model) { AllowsClosing=true };window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var caption=window.GetVisualDescendants().OfType<TextBlock>().Single(text=>text.Name=="ProductCaption");
            Assert.Equal("ВАШ НОМЕР: 701",caption.Text);Assert.True(caption.FontSize>=14);Assert.Equal(FontWeight.SemiBold,caption.FontWeight);
            var profile=window.FindControl<Button>("ProfilePill")!;
            Assert.Empty(profile.GetVisualDescendants().OfType<BrandMark>());
            Assert.Contains(profile.GetVisualDescendants().OfType<Ellipse>(),dot=>dot.Width==8);
            Assert.Contains(window.GetVisualDescendants().OfType<Icon>(),icon=>icon.Glyph=="slider.horizontal.2"&&icon.Size==16);
            model.StatusTitle="702";Dispatcher.UIThread.RunJobs();Assert.Equal("ВАШ НОМЕР: 702",caption.Text);Assert.Contains("702",window.Title);
            Snapshot.Save(window.CaptureRenderedFrame()!,"tray-panel-number");
        }
        finally { window.Close();CultureInfo.CurrentUICulture=old; }
    }

    [AvaloniaFact]
    public void Offline_choice_replaces_profile_identity_with_offline_label()
    {
        var model = new PanelViewModel { StatusTitle="701", Registration=RegistrationState.Registered };
        var window = new PanelWindow(model) { AllowsClosing=true };window.Show();
        try
        {
            model.GoOffline.Execute(null);Dispatcher.UIThread.RunJobs();
            var pill=window.FindControl<Button>("ProfilePill")!;
            Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsEffectivelyVisible&&t.Text==Strings.Get("PanelOffline"));
            Assert.DoesNotContain(pill.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsEffectivelyVisible&&t.Text=="701");
            model.GoOnline.Execute(null);Dispatcher.UIThread.RunJobs();
            Assert.Contains(pill.GetVisualDescendants().OfType<TextBlock>(),t=>t.IsEffectivelyVisible&&t.Text=="701");
        }
        finally {window.Close();}
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tray_menu_uses_live_theme_disables_offline_during_call_and_survives_close(bool dark)
    {
        var app=Application.Current!;var before=app.RequestedThemeVariant;
        using var errors=new BindingErrors();var history=0;
        var model=new PanelViewModel();
        var menu=new TrayMenuWindow(model,new Tray.TrayActions(()=>true,()=>{},()=>history++,()=>{},()=>{},()=>{}));
        try
        {
            app.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
            menu.Refresh("EliteSIP · 701");menu.OpenAt(100,100);Dispatcher.UIThread.RunJobs();
            Assert.True(menu.IsVisible);Assert.Equal(WindowDecorations.None,menu.WindowDecorations);
            var frame=menu.GetVisualDescendants().OfType<Border>().First(border=>border.CornerRadius.TopLeft==12);
            var first=Assert.IsAssignableFrom<ILinearGradientBrush>(frame.Background).GradientStops[0].Color;
            Snapshot.Save(menu.CaptureRenderedFrame()!,"tray-menu-"+(dark?"dark":"light"));
            app.RequestedThemeVariant=dark?ThemeVariant.Light:ThemeVariant.Dark;Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(first,Assert.IsAssignableFrom<ILinearGradientBrush>(frame.Background).GradientStops[0].Color);
            menu.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Home });
            menu.RaiseEvent(new KeyEventArgs { RoutedEvent=InputElement.KeyDownEvent,Key=Key.Up });
            var navigation=menu.GetVisualDescendants().OfType<Button>().Where(button=>button.IsEnabled).ToArray();
            Assert.True(navigation[^1].IsKeyboardFocusWithin);
            menu.Close();Assert.False(menu.IsVisible);menu.OpenAt(100,100);Assert.True(menu.IsVisible);
            model.Lines.Add(new CallLineViewModel { Title="Test" });menu.Refresh("In call");Dispatcher.UIThread.RunJobs();
            Button Find(string key)=>menu.GetVisualDescendants().OfType<Button>().Single(button=>button.GetVisualDescendants().OfType<TextBlock>().Any(text=>text.Text==Strings.Get(key)));
            Assert.False(Find("TrayDoNotDisturb").IsEnabled);
            Find("TrayHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.Equal(1,history);Assert.False(menu.IsVisible);
            Assert.Empty(errors.Errors);
        }
        finally { menu.ClosePermanently();app.RequestedThemeVariant=before; }
    }
}

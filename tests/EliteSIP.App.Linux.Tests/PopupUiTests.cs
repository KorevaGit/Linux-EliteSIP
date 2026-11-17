using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Platform;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

public sealed class PopupUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settings_language_and_all_combo_popups_use_product_theme(bool dark)
    {
        using var errors = new BindingErrors();
        var app = Application.Current!;var before = app.RequestedThemeVariant;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var model = new SettingsViewModel(new AppSettings(), new AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart()) { Section=SettingsSectionKind.Appearance };
        var appearance = new AppearanceService(app, new DesktopAppearance(_=>{}));
        var window = new SettingsWindow(model, appearance, new TestAudioPlatform().CreateSignalSounds(_=>{}));
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var combo = window.FindControl<ComboBox>("LanguageChoice")!;
            Assert.InRange(combo.Bounds.Height, 30, 34);
            combo.IsDropDownOpen = true;Dispatcher.UIThread.RunJobs();
            var popup = combo.GetVisualDescendants().OfType<Popup>().Single();
            var border = Assert.IsType<Border>(popup.Child);
            Assert.Equal(new CornerRadius(10), border.CornerRadius);
            Assert.IsAssignableFrom<ILinearGradientBrush>(border.Background);
            using var bitmap = new RenderTargetBitmap(PixelSize.FromSize(border.Bounds.Size,1),new Vector(96,96));
            bitmap.Render(border);Snapshot.Save(bitmap,"language-menu-"+(dark?"dark":"light"));
            
            combo.IsDropDownOpen=false;
            Assert.Empty(errors.Errors);
        }
        finally { window.Close();app.RequestedThemeVariant=before; }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Profile_context_menu_has_product_surface_and_live_offline_check(bool dark)
    {
        var app=Application.Current!;var before=app.RequestedThemeVariant;
        app.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
        var model=new EliteSIP.App.Panel.PanelViewModel {StatusTitle="701",Registration=EliteSIP.App.Panel.RegistrationState.Registered};
        var window=new EliteSIP.App.Panel.PanelWindow(model) {AllowsClosing=true};window.Show();
        try
        {
            var pill=window.FindControl<Button>("ProfilePill")!;
            var menu=pill.ContextMenu!;menu.Open(pill);Dispatcher.UIThread.RunJobs();
            // Observe live menu bindings after panel initialization: its optional
            // call/activity slots report unrelated null-path diagnostics at startup.
            using var errors = new BindingErrors();
            var frame=menu.GetVisualDescendants().OfType<Border>().Single(b=>b.Name=="ProductMenu");
            Assert.Equal(new CornerRadius(10),frame.CornerRadius);
            Assert.IsAssignableFrom<ILinearGradientBrush>(frame.Background);
            var offline=menu.Items.OfType<MenuItem>().Single(item=>item.Name=="OfflineMenuItem");
            model.GoOffline.Execute(null);Dispatcher.UIThread.RunJobs();Assert.True(offline.IsChecked);
            model.GoOnline.Execute(null);Dispatcher.UIThread.RunJobs();Assert.False(offline.IsChecked);
            using var image=new RenderTargetBitmap(PixelSize.FromSize(menu.Bounds.Size,1),new Vector(96,96));
            image.Render(menu);Snapshot.Save(image,"profile-menu-"+(dark?"dark":"light"));
            menu.Close();Assert.True(errors.Errors.Count==0,string.Join(Environment.NewLine,errors.Errors));
        }
        finally {window.Close();app.RequestedThemeVariant=before;}
    }

}

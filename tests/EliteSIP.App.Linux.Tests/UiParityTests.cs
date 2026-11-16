using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.AdminAccess;
using EliteSIP.App.Admin;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

public sealed class UiParityTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_settings_and_admin_page_renders_in_both_themes_with_stable_width(bool dark)
    {
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        using var home=new TemporaryHome();using var errors=new BindingErrors();
        try
        {
            app.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
            var settings=AppSettings.Load(home.SettingsPath);settings.Account.Username="701";
            using var appearance=new AppearanceService(app,new DesktopAppearance(_=>{})) { Appearance=dark?Appearance.Dark:Appearance.Light };
            var model=new SettingsViewModel(settings,new EliteSIP.App.Platform.AvaloniaUiThread(),new TestAudioPlatform(),new TestAutoStart());
            var window=new SettingsWindow(model,appearance,new TestAudioPlatform().CreateSignalSounds(_=>{}));window.Show();
            try { foreach(var section in model.Sections) { model.Section=section.Kind;Dispatcher.UIThread.RunJobs();Assert.Equal(720,window.Width);Snapshot.Save(window.CaptureRenderedFrame()!,"parity-settings-"+section.Kind+"-"+dark); } }
            finally { window.Close(); }
            var admin=new AdministrationViewModel(settings,new AdminAccessState());var control=new AdministrationWindow(admin) { Width=740 };control.Show();
            try
            {
                foreach(var section in admin.Sections) { admin.Section=section.Kind;Dispatcher.UIThread.RunJobs();Snapshot.Save(control.CaptureRenderedFrame()!,"parity-admin-"+section.Kind+"-"+dark); }
                foreach(var page in Enum.GetValues<AdminIncomingPage>()) { admin.Section=AdminSectionKind.Incoming;admin.IncomingPage=page;Dispatcher.UIThread.RunJobs(); }
                foreach(var page in Enum.GetValues<AdminSystemPage>()) { admin.Section=AdminSectionKind.History;admin.SystemPage=page;Dispatcher.UIThread.RunJobs(); }
                Assert.All(control.GetVisualDescendants().OfType<TextBox>().Where(x=>x.IsEffectivelyVisible),box=>Assert.True(box.TranslatePoint(new Point(box.Bounds.Width,0),control)!.Value.X<=control.Bounds.Width+1));
            }
            finally { admin.Revert();control.Close(); }
            Assert.True(errors.Errors.Count==0,string.Join(Environment.NewLine,errors.Errors));
        }
        finally { app.RequestedThemeVariant=old; }
    }
    [AvaloniaFact]
    public void Microphone_test_button_updates_immediately_before_the_next_meter_poll()
    {
        var model = new SettingsViewModel(new AppSettings(), new EliteSIP.App.Platform.AvaloniaUiThread(), new TestAudioPlatform(), new TestAutoStart());
        var before = model.MicrophoneTestText;
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.ReportSelfTestRunning(true);
        Assert.NotEqual(before, model.MicrophoneTestText);
        Assert.Contains(nameof(model.MicrophoneTestText), changed);
        Assert.Contains(nameof(model.SelfTestElapsed), changed);
        model.ReportSelfTestRunning(false);
        Assert.Equal(before, model.MicrophoneTestText);
        Assert.Equal("00:00", model.SelfTestElapsed);
    }

    [AvaloniaFact]
    public void Wizard_uses_product_chrome_and_two_column_manual_form()
    {
        using var errors = new BindingErrors();
        var model = new EliteSIP.App.FirstRun.FirstRunViewModel(new AppSettings(), new AdminAccessState());
        model.UseManualSetup();
        var window = new EliteSIP.App.FirstRun.FirstRunWindow(model);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(WindowDecorations.None, window.WindowDecorations);
            Assert.Equal(560, window.Width);
            Assert.Equal(440, window.Height);
            Assert.Contains(DrawnCaption.Buttons(window), button => button.Name == "PART_CloseButton");
            var sip = window.FindControl<TextBox>("SipPasswordField")!;
            var admin = window.FindControl<TextBox>("AdminPasswordField")!;
            Assert.True(sip.TranslatePoint(default, window)!.Value.X > admin.TranslatePoint(default, window)!.Value.X);
            Assert.Equal('●', sip.PasswordChar);
            Assert.Empty(errors.Errors);
        }
        finally { window.Hide(); }
    }

    [AvaloniaFact]
    public void Call_header_has_two_columns_and_offline_number_entry_remains_enabled()
    {
        var model=new PanelViewModel { CanPlaceCall=false };var window=new PanelWindow(model) { AllowsClosing=true };window.Show();
        try
        {
            Assert.True(window.FindControl<TextBox>("DialedField")!.IsEnabled);
            model.ActiveLine=new CallLineViewModel { Title="Очень длинное имя собеседника, которое должно оставаться в своей колонке", SecondaryNumber="+7 ••• •••-••-67", ConnectedAt=DateTimeOffset.Now.AddMinutes(-2) };model.Lines.Add(model.ActiveLine);
            Dispatcher.UIThread.RunJobs();Snapshot.Save(window.CaptureRenderedFrame()!,"parity-panel-connected");
            model.ActiveLine=null;model.Lines.Clear();model.PendingNumber="79990000000";model.CallStatus="Гудки…";
            Dispatcher.UIThread.RunJobs();Assert.True(model.IsOutgoingPending);Snapshot.Save(window.CaptureRenderedFrame()!,"parity-panel-ringing");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public void Update_notes_are_plain_scrollable_text_and_recording_reader_has_finite_samples()
    {
        var notes="<script>literal</script>\n"+string.Join("\n",Enumerable.Repeat("Long release note",100));
        var window=new UpdateOffer(new Version(0,1,7),new Version(0,1,8),notes);window.Show();
        try { var scroll=Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>());Assert.Equal(notes,Assert.IsType<TextBlock>(scroll.Content).Text);Assert.True(scroll.Bounds.Height<=340); }
        finally { window.Close(); }
        var samples=new RecordingSamples([short.MinValue,0,short.MaxValue],8000);var buffer=new float[8];Assert.Equal(3,samples.Read(buffer));Assert.Equal(-1,buffer[0]);Assert.Equal(0,samples.Read(buffer));
    }
}

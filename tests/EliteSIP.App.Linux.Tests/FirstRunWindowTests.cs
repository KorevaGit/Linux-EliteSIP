using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EliteSIP.AdminAccess;
using EliteSIP.App.FirstRun;
using EliteSIP.App.Settings;

namespace EliteSIP.App.Tests;

/// <summary>
/// Мастер: все шаги проходятся ручной настройкой и рисуются без ошибок
/// привязок — то, что LayoutTests Windows-версии проверяли шагами мастера.
/// </summary>
public sealed class FirstRunWindowTests
{
    [AvaloniaFact]
    public void Шаги_мастера_от_приветствия_до_итога()
    {
        using BindingErrors errors = new();
        using TemporaryHome home = new();

        var settings = AppSettings.Load(home.SettingsPath);
        var access = new AdminAccessState(settings.Admin.ToCredential());
        FirstRunViewModel model = new(settings, access);
        FirstRunWindow window = new(model);
        window.Show();

        void Shot(string name)
        {
            Dispatcher.UIThread.RunJobs();
            Snapshot.Save(window.CaptureRenderedFrame()!, "firstrun-" + name);
        }

        Shot("welcome");
        model.Forward();
        Shot("pair");

        model.UseManualSetup();
        model.Username = "701";
        model.OfficeAddress = "10.0.0.1";
        model.SipPassword = "secret";
        model.AdminPassword = "admin";
        model.RepeatedAdminPassword = "admin";
        Shot("user");

        Assert.True(model.CanGoForward);
        model.Forward();
        Assert.Equal(FirstRunStep.Appearance, model.Step);
        Shot("appearance");

        model.Forward();
        Assert.Equal(FirstRunStep.Finale, model.Step);
        Shot("finale");

        // Не Close: закрытие посреди мастера спрашивает «бросить настройку?»,
        // и в Headless ответить на это некому.
        window.Hide();
        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
    }
}

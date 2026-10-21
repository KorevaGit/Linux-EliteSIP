using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;

namespace EliteSIP.App.FirstRun;

/// <summary>
/// Мастер первого запуска — перевод <c>FirstRun/FirstRunWindow</c>: язык, код
/// и QR привязки к Spark (или ручная настройка), тема, итог.
/// </summary>
///
/// <remarks>
/// Шаги, опрос канала и запись настроек — общая <see cref="FirstRunViewModel"/>.
/// Ответ мастера синхронный (<see cref="Run"/>), как <c>ShowDialog</c> у WPF:
/// приложение до него не поднимает ни телефон, ни панель.
/// </remarks>
public partial class FirstRunWindow : Window
{
    private bool _completed;

    public FirstRunWindow()
    {
        InitializeComponent();
    }

    internal FirstRunWindow(FirstRunViewModel model)
        : this()
    {
        Model = model;
        DataContext = model;
    }

    public FirstRunViewModel? Model { get; }

    /// <summary>Показать и дождаться: <see langword="true"/> — машина настроена.</summary>
    internal bool Run()
    {
        using CancellationTokenSource closed = new();
        Closed += (_, _) => closed.Cancel();
        Show();
        Dispatcher.UIThread.MainLoop(closed.Token);
        return _completed;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_completed || Model is null || Model.Step is FirstRunStep.Welcome)
        {
            return;
        }

        var answer = Dialog.Ask(
            this,
            Strings.Get("FirstRunAbandonTitle"),
            Strings.Get("FirstRunAbandonBody"),
            confirmTitle: Strings.Get("FirstRunAbandonConfirm"));

        if (answer is not DialogAnswer.Confirm)
        {
            e.Cancel = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Model?.Dispose();
        base.OnClosed(e);
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => Model?.Back();

    private void OnNewCodeClick(object? sender, RoutedEventArgs e) => Model?.RequestNewCode();

    private void OnManualSetupClick(object? sender, RoutedEventArgs e) => Model?.UseManualSetup();

    private void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        if (!Model.IsLastStep)
        {
            Model.Forward();
            return;
        }

        Model.Complete();
        _completed = true;
        Close();
    }

    private void OnSipPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.SipPassword = SipPasswordField.Text ?? string.Empty;
        }
    }

    private void OnAdminPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.AdminPassword = AdminPasswordField.Text ?? string.Empty;
        }
    }

    private void OnRepeatedAdminPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.RepeatedAdminPassword = RepeatedAdminPasswordField.Text ?? string.Empty;
        }
    }
}

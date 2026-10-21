using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EliteSIP.AdminAccess;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Admin;

/// <summary>
/// Пароль перед «Управлением» — перевод <c>Admin/AdminUnlockWindow</c>.
/// </summary>
///
/// <remarks>
/// Без пароля на машине окно не спрашивает ничего, а предупреждает — как на
/// Windows. Ответ — синхронный (<see cref="Ask"/>), тем же вложенным циклом,
/// что у <c>Theme/Dialog</c>: приложение открывает «Управление» сразу за ним.
/// </remarks>
public partial class AdminUnlockWindow : Window
{
    private readonly AdminAccessState? _access;
    private bool _accepted;

    public AdminUnlockWindow()
    {
        InitializeComponent();
    }

    internal AdminUnlockWindow(AppSettings settings, AdminAccessState access)
        : this()
    {
        _access = access;

        if (settings.Admin.IsProtected)
        {
            Opened += (_, _) => PasswordField.Focus();
            return;
        }

        PasswordStep.IsVisible = false;
        WarningText.IsVisible = true;
        EnterButton.IsEnabled = true;
        Opened += (_, _) => EnterButton.Focus();
    }

    /// <summary>Показать и дождаться: <see langword="true"/> — можно открывать.</summary>
    internal bool Ask(Window? owner)
    {
        using CancellationTokenSource closed = new();
        Closed += (_, _) => closed.Cancel();

        if (owner is { IsVisible: true })
        {
            _ = ShowDialog(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Show();
        }

        Dispatcher.UIThread.MainLoop(closed.Token);
        return _accepted;
    }

    private void OnPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        var length = PasswordField.Text?.Length ?? 0;
        EnterButton.IsEnabled = length > 0;
        if (length > 0)
        {
            ProblemRow.IsVisible = false;
        }
    }

    private void OnPasswordFocus(object? sender, FocusChangedEventArgs e) => ShowCapsLock();

    private void OnPasswordKeyUp(object? sender, KeyEventArgs e) => ShowCapsLock();

    private void ShowCapsLock() => CapsRow.IsVisible = CapsLock.IsOn();

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter && EnterButton.IsEnabled)
        {
            OnEnterClick(sender, e);
        }
    }

    private void OnEnterClick(object? sender, RoutedEventArgs e)
    {
        if (_access is null)
        {
            return;
        }

        if (_access.IsUnlocked || !_access.IsProtected)
        {
            _access.Unlock(string.Empty);
            Accept();
            return;
        }

        try
        {
            _access.Unlock(PasswordField.Text ?? string.Empty);
        }
        catch (AdminAccessException)
        {
            PasswordField.Text = string.Empty;
            ProblemText.Text = Strings.Get("AdminUnlockWrongPassword");
            ProblemRow.IsVisible = true;
            PasswordField.Focus();
            ShowCapsLock();
            return;
        }

        Accept();
    }

    private void Accept()
    {
        _accepted = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}

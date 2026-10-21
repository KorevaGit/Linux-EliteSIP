using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EliteSIP.App.Resources;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Admin;

/// <summary>
/// «Управление» — перевод <c>Admin/AdministrationWindow</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// Правки копятся в общей <see cref="AdministrationViewModel"/> и пишутся
/// разом по «Сохранить» — здесь только окно: вопросы перед сохранением,
/// закрытием с правками и сбросом машины, поля паролей (у Avalonia нет
/// <c>PasswordBox</c> — пароль живёт в <see cref="TextBox.Text"/> со
/// скрытыми знаками и в модель уходит по каждому изменению).
/// </remarks>
public partial class AdministrationWindow : Window
{
    private bool _closingConfirmed;

    public AdministrationWindow()
    {
        InitializeComponent();
    }

    internal AdministrationWindow(AdministrationViewModel model)
        : this()
    {
        Model = model;
        DataContext = model;
    }

    public AdministrationViewModel? Model { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closingConfirmed || Model is not { IsDirty: true } model)
        {
            return;
        }

        var answer = Dialog.Ask(
            this,
            Strings.Get("AdminCloseDirtyTitle"),
            Strings.Get("AdminCloseDirtyBody"),
            DialogButtons.SaveDiscardCancel);

        switch (answer)
        {
            case DialogAnswer.Confirm:
                model.Save();
                break;
            case DialogAnswer.Discard:
                model.Revert();
                break;
            default:
                e.Cancel = true;
                break;
        }
    }

    private void CloseConfirmed()
    {
        _closingConfirmed = true;
        Close();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        var answer = Dialog.Ask(
            this,
            Strings.Get("AdminSaveConfirmTitle"),
            Strings.Get("AdminSaveConfirmBody"),
            confirmTitle: Strings.Get("AdminSave"));

        if (answer is not DialogAnswer.Confirm)
        {
            return;
        }

        Model.Save();
        CloseConfirmed();
    }

    private void OnRevertClick(object? sender, RoutedEventArgs e) => Model?.Revert();

    private void OnMacroAddClick(object? sender, RoutedEventArgs e) => Model?.AddMacro();

    private void OnMacroRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Of<MacroSetting>(sender) is { } macro)
        {
            Model?.RemoveMacro(macro);
        }
    }

    private void OnMacroUpClick(object? sender, RoutedEventArgs e)
    {
        if (Of<MacroSetting>(sender) is { } macro)
        {
            Model?.MoveMacro(macro, offset: -1);
        }
    }

    private void OnMacroDownClick(object? sender, RoutedEventArgs e)
    {
        if (Of<MacroSetting>(sender) is { } macro)
        {
            Model?.MoveMacro(macro, offset: 1);
        }
    }

    private static T? Of<T>(object? sender)
        where T : class
        => (sender as Control)?.DataContext as T;

    private void OnSipPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.SipPassword = SipPasswordField.Text ?? string.Empty;
        }
    }

    private void OnQueueAddClick(object? sender, RoutedEventArgs e) => Model?.AddQueue();

    private void OnQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Of<QueueSetting>(sender) is { } queue)
        {
            Model?.RemoveQueue(queue);
        }
    }

    private void OnKnockAddClick(object? sender, RoutedEventArgs e) => Model?.AddKnockStep();

    private void OnKnockRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Of<PortKnockStepSetting>(sender) is { } step)
        {
            Model?.RemoveKnockStep(step);
        }
    }

    private void OnNewPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.NewPassword = NewPasswordField.Text ?? string.Empty;
        }
    }

    private void OnRepeatedPasswordChanged(object? sender, TextChangedEventArgs e)
    {
        if (Model is not null)
        {
            Model.RepeatedPassword = RepeatedPasswordField.Text ?? string.Empty;
        }
    }

    private void OnSetPasswordClick(object? sender, RoutedEventArgs e)
    {
        Model?.SetPassword();
        NewPasswordField.Text = string.Empty;
        RepeatedPasswordField.Text = string.Empty;
    }

    private void OnRemovePasswordClick(object? sender, RoutedEventArgs e)
    {
        var answer = Dialog.Ask(
            this,
            Strings.Get("AdminRemovePasswordTitle"),
            Strings.Get("AdminRemovePasswordBody"),
            confirmTitle: Strings.Get("AdminRemovePasswordConfirm"));

        if (answer is DialogAnswer.Confirm)
        {
            Model?.RemovePassword();
        }
    }

    private void OnResetMachineClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanResetMachine: true } model)
        {
            return;
        }

        var answer = Dialog.Ask(
            this,
            Strings.Get("AdminResetConfirmTitle"),
            Strings.Get("AdminResetConfirmBody"),
            confirmTitle: Strings.Get("AdminResetConfirmButton"),
            footnote: Strings.Get("AdminResetConfirmFootnote"),
            countdownSeconds: 10,
            destructive: true);

        if (answer is not DialogAnswer.Confirm || !model.CanResetMachine)
        {
            return;
        }

        // Сброс — после закрытия окна: он перезапускает приложение, а окно с
        // открытыми правками, закрытое из-под него, спросило бы «сохранить?».
        var reset = model.ResetMachine;
        model.Revert();
        CloseConfirmed();

        if (reset is not null)
        {
            Dispatcher.UIThread.Post(reset);
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(AdministrationViewModel.SettingsPath);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        // xdg-open: Nemo в Cinnamon, Nautilus в GNOME.
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }
}

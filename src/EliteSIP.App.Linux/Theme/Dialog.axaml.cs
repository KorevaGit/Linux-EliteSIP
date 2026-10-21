using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Theme;

public enum DialogAnswer
{
    Confirm,
    Discard,
    Cancel,
}

public enum DialogButtons
{
    ConfirmCancel,
    SaveDiscardCancel,
}

/// <summary>
/// Вопрос оператору — перевод <c>Theme/Dialog</c> Windows-версии: те же
/// кнопки, отсчёт перед опасным подтверждением и красная надпись на нём.
/// </summary>
///
/// <remarks>
/// <see cref="Ask"/> синхронный, как <c>ShowDialog</c> WPF: на ответ ждут и
/// общий код (<c>UpdateService.AskToInstall</c> возвращает <c>bool</c>), и
/// обработчики окон, которые за ним что-то сохраняют или отменяют. В Avalonia
/// модальный показ асинхронный, поэтому ответ дожидается вложенным циклом
/// окна (<see cref="Dispatcher.MainLoop"/>) — тем же, чем WPF крутил
/// <c>ShowDialog</c>.
/// </remarks>
public partial class Dialog : Window
{
    private DialogAnswer _answer = DialogAnswer.Cancel;

    public Dialog()
    {
        InitializeComponent();
    }

    public static DialogAnswer Ask(
        Window? owner,
        string title,
        string body,
        DialogButtons buttons = DialogButtons.ConfirmCancel,
        string? confirmTitle = null,
        string? footnote = null,
        int countdownSeconds = 0,
        bool destructive = false)
    {
        var dialog = new Dialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;

        if (footnote is { Length: > 0 })
        {
            dialog.FootnoteText.Text = footnote;
            dialog.FootnoteText.IsVisible = true;
        }

        Button confirm;
        if (buttons is DialogButtons.SaveDiscardCancel)
        {
            confirm = dialog.AddButton(Strings.Get("DialogSave"), DialogAnswer.Confirm, primary: true);
            dialog.AddButton(Strings.Get("DialogDiscard"), DialogAnswer.Discard, primary: false);
        }
        else
        {
            confirm = dialog.AddButton(
                confirmTitle ?? Strings.Get("DialogConfirm"), DialogAnswer.Confirm, primary: true, destructive);
        }

        dialog.AddButton(Strings.Get("DialogCancel"), DialogAnswer.Cancel, primary: false);

        if (countdownSeconds > 0)
        {
            dialog.CountDown(confirm, confirmTitle ?? Strings.Get("DialogConfirm"), countdownSeconds);
        }

        using CancellationTokenSource closed = new();
        dialog.Closed += (_, _) => closed.Cancel();

        if (owner is { IsVisible: true })
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
        }

        Dispatcher.UIThread.MainLoop(closed.Token);
        return dialog._answer;
    }

    private void CountDown(Button confirm, string title, int seconds)
    {
        var remaining = seconds;
        confirm.IsEnabled = false;
        confirm.IsDefault = false;
        confirm.Content = $"{title} ({remaining})";

        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            if (remaining > 0)
            {
                confirm.Content = $"{title} ({remaining})";
                return;
            }

            timer.Stop();
            confirm.Content = title;
            confirm.IsEnabled = true;
        };

        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private Button AddButton(string title, DialogAnswer answer, bool primary, bool destructive = false)
    {
        var button = new Button
        {
            Content = title,
            Height = 32,
            MinWidth = 96,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            IsDefault = primary,
            IsCancel = answer is DialogAnswer.Cancel,
        };

        button.Classes.Add("surface");
        if (primary && !destructive)
        {
            button.Classes.Add("accent");
        }

        if (destructive && this.TryFindResource("StatusFailureBrush", ActualThemeVariant, out var red) && red is IBrush brush)
        {
            button.Foreground = brush;
        }

        button.Click += (_, _) =>
        {
            _answer = answer;
            Close();
        };

        Buttons.Children.Add(button);
        return button;
    }
}

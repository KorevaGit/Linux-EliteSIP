using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EliteSIP.App.Resources;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// История звонков — перевод <c>History/CallHistoryWindow</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// Содержание — общая <see cref="CallHistoryViewModel"/>: фильтры, дни,
/// календарь, догрузка. Здесь — окно: прокрутка, всплывающий календарь и
/// снимок списка в буфер обмена (вдвое крупнее экранного, как на Windows, —
/// его вставляют в переписку с поддержкой).
/// </remarks>
public partial class CallHistoryWindow : Window
{
    private readonly DispatcherTimer _noticeTimer;

    public CallHistoryWindow()
    {
        InitializeComponent();

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice.IsVisible = false;
        };
    }

    internal CallHistoryWindow(CallHistoryViewModel model)
        : this()
    {
        Model = model;
        DataContext = model;
        Title = string.IsNullOrEmpty(model.ProfileTitle)
            ? Strings.Get("HistoryWindowTitle")
            : Strings.Get("HistoryWindowTitleWithProfile").Replace("{0}", model.ProfileTitle, StringComparison.Ordinal);
    }

    public CallHistoryViewModel? Model { get; }

    /// <summary>Перезвонить: номер уходит в поле набора панели.</summary>
    public event Action<string>? RedialRequested;

    protected override void OnClosed(EventArgs e)
    {
        _noticeTimer.Stop();
        base.OnClosed(e);
    }

    private void OnFilterChosen(object? sender, RoutedEventArgs e)
    {
        if (Model is not null && sender is RadioButton { Tag: HistoryFilter filter, IsChecked: true } && Model.Filter != filter)
        {
            Model.Filter = filter;
            Scroller.ScrollToHome();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Model is not { HasMore: true } || Scroller.Viewport.Height <= 0)
        {
            return;
        }

        // Догружать заранее — за экран до конца, а не на последней строке.
        var remaining = Scroller.Extent.Height - Scroller.Offset.Y - Scroller.Viewport.Height;
        if (remaining <= Scroller.Viewport.Height)
        {
            Model.LoadMore();
        }
    }

    private void OnCalendarClick(object? sender, RoutedEventArgs e) => CalendarPopup.IsOpen = true;

    private void OnCalendarOpened(object? sender, EventArgs e) => Model?.Calendar.Reset();

    private void OnPreviousMonthClick(object? sender, RoutedEventArgs e) => Model?.Calendar.ShiftMonth(-1);

    private void OnNextMonthClick(object? sender, RoutedEventArgs e) => Model?.Calendar.ShiftMonth(1);

    private void OnClearDayClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            Model.Calendar.SelectedDay = null;
        }
    }

    private void OnDayClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not null && (sender as Control)?.DataContext is CalendarDay day)
        {
            Model.Calendar.SelectedDay = day.Date;
            CalendarPopup.IsOpen = false;
            Scroller.ScrollToHome();
        }
    }

    private void OnRedialClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string number } && !string.IsNullOrEmpty(number))
        {
            RedialRequested?.Invoke(number);
        }
    }

    private async void OnSnapshotClick(object? sender, RoutedEventArgs e)
    {
        const double scale = 2;
        var size = ListSurface.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0 || Clipboard is not { } clipboard)
        {
            return;
        }

        using RenderTargetBitmap image = new(
            new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));

        // Подложка окна под скруглёнными углами списка: без неё углы снимка
        // прозрачные и в переписке выходят чёрными.
        using (var context = image.CreateDrawingContext())
        {
            context.DrawRectangle(Background ?? Brushes.White, null, new Rect(size));
        }

        image.Render(ListSurface);
        await clipboard.SetBitmapAsync(image);
        ShowNotice(Strings.Get("HistorySnapshotDone"));
    }

    private void ShowNotice(string text)
    {
        NoticeText.Text = text;
        Notice.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }
}

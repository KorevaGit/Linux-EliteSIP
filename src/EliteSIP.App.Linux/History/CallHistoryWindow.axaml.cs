using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        HistoryList.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged);

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

    private ScrollViewer? Scroller => HistoryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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
            Scroller?.ScrollToHome();
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (Model is not { HasMore: true } || Scroller is not { } scroller || scroller.Viewport.Height <= 0)
        {
            return;
        }

        // Догружать заранее — за экран до конца, а не на последней строке.
        var remaining = scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height;
        if (remaining <= scroller.Viewport.Height)
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
            Scroller?.ScrollToHome();
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
        try
        {
            if (Clipboard is { } clipboard) { await CopySnapshotAsync(clipboard); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            ShowNotice(LinuxStrings.Get("HistorySnapshotFailed"));
        }
    }

    internal async Task CopySnapshotAsync(IClipboard clipboard)
    {
        const double scale = 2;
        var size = ListSurface.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _noticeTimer.Stop();
        Notice.IsVisible = false;
        // Avalonia 12's immediate high-DPI renderer clips nested visuals. Capture
        // the full viewport at 96 DPI, then enlarge it once in the export.
        using RenderTargetBitmap list = new(new PixelSize((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height)),new Vector(96,96));
        list.Render(ListSurface);
        using RenderTargetBitmap image = new(new PixelSize((int)Math.Ceiling(size.Width*scale),(int)Math.Ceiling((size.Height+52)*scale)),new Vector(96,96));
        using(var context=image.CreateDrawingContext())
        {
            // One explicit DIP-to-pixel transform. Avalonia's bitmap source
            // rectangle is in pixels, even when Bitmap.Size is in DIPs.
            using var scaled = context.PushTransform(Matrix.CreateScale(scale, scale));
            context.DrawRectangle(Background ?? Brushes.White,null,new Rect(0,0,size.Width,size.Height+52));
            var stamp=$"EliteSIP · {Model?.ProfileTitle} · {DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss zzz} · UTC {DateTimeOffset.UtcNow:HH:mm:ss}";
            var text=new FormattedText(stamp,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("sans-serif"),12,Foreground ?? Brushes.Black);
            context.DrawText(text,new Point(10,16));
            context.DrawImage(list,new Rect(0,0,list.PixelSize.Width,list.PixelSize.Height),new Rect(0,52,size.Width,size.Height));
        }
        // X11 reads lazily, possibly after this window closes. The clipboard
        // owns the transfer; each request decodes an independent bitmap.
        using var png = new MemoryStream();
        image.Save(png, PngBitmapEncoderOptions.Default);
        var bytes = png.ToArray();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, () =>
        {
            using var source = new MemoryStream(bytes, writable: false);
            return new Bitmap(source);
        }));
        try { await clipboard.SetDataAsync(transfer); }
        catch { ((IDisposable)transfer).Dispose(); throw; }
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

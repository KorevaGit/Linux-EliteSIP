using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EliteSIP.App.History;
using EliteSIP.CallHistory;

namespace EliteSIP.App.Tests;

/// <summary>История: показательные записи, фильтры и календарь без ошибок привязок.</summary>
public sealed class HistoryWindowTests
{
    [AvaloniaFact]
    public void История_с_записями_рисуется_и_календарь_открывается()
    {
        using BindingErrors errors = new();
        using TemporaryHome home = new();
        using CallHistoryStore store = new(new CallHistoryStore.Settings(Path.Combine(home.Path, "history.db")));

        var profile = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        Add(store, profile, "712", "Отдел продаж", now.AddMinutes(-12), CallDirection.Incoming, CallOutcome.Completed, answered: true);
        Add(store, profile, "89261234567", null, now.AddHours(-2), CallDirection.Incoming, CallOutcome.Missed, answered: false);
        Add(store, profile, "101", "Юрист", now.AddDays(-1), CallDirection.Outgoing, CallOutcome.Busy, answered: false);
        store.Flush();

        CallHistoryViewModel model = new(store, profile, "701", masksMobileNumbers: true, () => 30);
        CallHistoryWindow window = new(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot.Save(window.CaptureRenderedFrame()!, "history");
        Assert.Equal(40, window.CalendarButton.Bounds.Height);
        Assert.Contains(window.GetVisualDescendants().OfType<OutcomeBadge>(), badge=>badge.Size==40);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text=>text.FontSize==18&&text.Text=="Отдел продаж");

        window.CalendarPopup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Snapshot.Save(window.CaptureRenderedFrame()!, "history-calendar");
        var calendar=Assert.IsType<Border>(window.CalendarPopup.Child);
        Assert.IsAssignableFrom<ISolidColorBrush>(calendar.Background);
        Assert.Equal(0, calendar.BoxShadow.Count);
        Assert.All(calendar.GetVisualDescendants().OfType<Border>(), border=>Assert.Equal(0, border.BoxShadow.Count));

        model.Filter = HistoryFilter.Missed;
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
    }

    [AvaloniaFact]
    public async Task Snapshot_survives_deferred_clipboard_request_after_window_closes()
    {
        var clipboard = System.Reflection.DispatchProxy.Create<Avalonia.Input.Platform.IClipboard, DeferredClipboard>();
        var deferred = (DeferredClipboard)(object)clipboard;
        var window = new CallHistoryWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.CopySnapshotAsync(clipboard);
        window.Close();
        // X11 requests the image later, often after the click handler has returned.
        for (var i=0;i<3;i++)
        {
            using var bitmap = await deferred.Data!.TryGetBitmapAsync();
            Assert.NotNull(bitmap);
            using var png = new MemoryStream();
            bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Assert.True(png.Length > 100);
        }
        deferred.Data!.Dispose();
    }

    [AvaloniaFact]
    public async Task Snapshot_keeps_all_corners_at_two_times_screen_size()
    {
        var clipboard=System.Reflection.DispatchProxy.Create<Avalonia.Input.Platform.IClipboard,DeferredClipboard>();
        var deferred=(DeferredClipboard)(object)clipboard;
        var window=new CallHistoryWindow();
        window.ListSurface.Child=new Grid {Children={
            new Border {Width=24,Height=24,Background=Brushes.Cyan,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Top},
            new Border {Width=24,Height=24,Background=Brushes.Magenta,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Bottom},
            new Border {Width=24,Height=24,Background=Brushes.Yellow,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Right,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Top},
            new Border {Width=24,Height=24,Background=Brushes.Lime,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Left,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Bottom}}};
        window.Show();Dispatcher.UIThread.RunJobs();
        try
        {
            await window.CopySnapshotAsync(clipboard);
            using var bitmap=await deferred.Data!.TryGetBitmapAsync();
            using var png=new MemoryStream();bitmap!.Save(png,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            using var pixels=SkiaSharp.SKBitmap.Decode(png.ToArray());
            Snapshot.Save(bitmap,"history-capture-corners");
            Assert.Equal(new SkiaSharp.SKColor(0,255,255),pixels.GetPixel(24,104+24));
            Assert.Equal(new SkiaSharp.SKColor(255,0,255),pixels.GetPixel(pixels.Width-24,pixels.Height-24));
            Assert.Equal(new SkiaSharp.SKColor(255,255,0),pixels.GetPixel(pixels.Width-24,104+24));
            Assert.Equal(new SkiaSharp.SKColor(0,255,0),pixels.GetPixel(24,pixels.Height-24));
        }
        finally {window.Close();deferred.Data?.Dispose();}
    }

    public class DeferredClipboard : System.Reflection.DispatchProxy
    {
        public Avalonia.Input.IAsyncDataTransfer? Data { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "SetDataAsync") { Data = (Avalonia.Input.IAsyncDataTransfer)args![0]!; return Task.CompletedTask; }
            throw new NotSupportedException(targetMethod.Name);
        }
    }

    private static void Add(
        CallHistoryStore store, Guid profile, string number, string? name, DateTimeOffset at,
        CallDirection direction, CallOutcome outcome, bool answered)
    {
        var record = new CallRecord
        {
            CallId = Guid.NewGuid().ToString("N"),
            Direction = direction,
            Number = number,
            DisplayName = name,
            ProfileId = profile,
            StartedAt = at,
        };

        store.Begin(record);
        if (answered)
        {
            store.MarkAnswered(record.Id, at.AddSeconds(2));
        }

        store.Finish(record.Id, reason: "проверка", outcome: outcome, at: at.AddSeconds(answered ? 95 : 20));
    }
}

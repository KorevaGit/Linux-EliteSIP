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

        window.CalendarPopup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        Snapshot.Save(window.CaptureRenderedFrame()!, "history-calendar");

        model.Filter = HistoryFilter.Missed;
        Dispatcher.UIThread.RunJobs();

        window.Close();
        Assert.True(errors.Errors.Count == 0, string.Join(Environment.NewLine, errors.Errors.Distinct()));
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

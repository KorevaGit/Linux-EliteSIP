using System.Globalization;
using Avalonia.Data.Converters;
using EliteSIP.CallHistory;

namespace EliteSIP.App.History;

/// <summary>
/// Конвертеры строки истории — перевод <c>History/HistoryConverters</c>.
/// Само содержание строк — общая <see cref="HistoryPresentation"/> (App.Core);
/// здесь только стык с разметкой Avalonia.
/// </summary>
public static class HistoryConverters
{
    /// <summary>Длительность разговора, а без неё — исход словами.</summary>
    public static IValueConverter OutcomeText { get; } = new FuncValueConverter<CallRecord?, string>(record =>
    {
        if (record is null)
        {
            return string.Empty;
        }

        if (record.Duration is { } duration)
        {
            // Часы — только когда они есть: «00:05:12» там, где все звонки
            // короче часа, — два лишних знака в каждой строке.
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss", CultureInfo.CurrentCulture)
                : duration.ToString(@"mm\:ss", CultureInfo.CurrentCulture);
        }

        return record.Outcome.Title() ?? string.Empty;
    });

    public static IValueConverter CallStamp { get; } = new FuncValueConverter<DateTimeOffset, string>(
        moment => moment.ToLocalTime().ToString("HH:mm dd.MM.yyyy", CultureInfo.CurrentCulture));

    public static IValueConverter IsIncoming { get; } = new FuncValueConverter<CallRecord?, bool>(
        record => record?.Direction == CallDirection.Incoming);

    /// <summary>[запись, свой номер, маскировать мобильные] → заголовок строки.</summary>
    public static IMultiValueConverter CallTitle { get; } = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        return list.Count > 0 && list[0] is CallRecord record
            ? HistoryPresentation.Title(record, OwnNumber(list), MasksMobileNumbers(list))
            : string.Empty;
    });

    /// <summary>[запись, свой номер, маскировать мобильные] → подзаголовок строки.</summary>
    public static IMultiValueConverter CallSubtitle { get; } = new FuncMultiValueConverter<object?, string>(values =>
    {
        var list = values.ToList();
        return list.Count > 0 && list[0] is CallRecord record
            ? HistoryPresentation.Subtitle(record, OwnNumber(list), MasksMobileNumbers(list))
            : string.Empty;
    });

    /// <summary>Два значения равны — выбранный день календаря, выбранная строка.</summary>
    public static IMultiValueConverter Equality { get; } = new FuncMultiValueConverter<object?, bool>(values =>
    {
        var list = values.ToList();
        return list.Count == 2 && Equals(list[0], list[1]);
    });

    private static string OwnNumber(List<object?> values) => values.Count > 1 ? values[1] as string ?? string.Empty : string.Empty;

    private static bool MasksMobileNumbers(List<object?> values) => values.Count <= 2 || values[2] is not false;
}

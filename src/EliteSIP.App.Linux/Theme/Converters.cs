using System.Globalization;
using Avalonia.Data.Converters;

namespace EliteSIP.App.Theme;

/// <summary>
/// Конвертеры разметки — то немногое из <c>Theme/Converters</c> Windows-версии,
/// чего в Avalonia нет готовым.
/// </summary>
///
/// <remarks>
/// Видимость по флагу и по наличию значения Avalonia даёт сама (<c>IsVisible</c>
/// булев, <c>!</c> в привязке, <c>ObjectConverters.IsNotNull</c>,
/// <c>StringConverters.IsNotNullOrEmpty</c>), поэтому здесь только сравнение
/// с образцом — то, чем WPF-разметка пользовалась в <c>DataTrigger</c>
/// (<c>Registration = Registered</c>) и в плитках выбора.
/// </remarks>
public static class Converters
{
    /// <summary>Значение равно параметру (по строке — так пишутся члены перечислений в разметке).</summary>
    public static IValueConverter Equal { get; } = new FuncValueConverter<object?, object?, bool>(
        (value, parameter) => value is not null
            && string.Equals(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                Convert.ToString(parameter, CultureInfo.InvariantCulture),
                StringComparison.Ordinal));

    /// <summary>Доля 0…1 → проценты целым числом.</summary>
    public static IValueConverter Percent { get; } = new FuncValueConverter<double, string>(
        value => Math.Round(value * 100).ToString(CultureInfo.CurrentCulture) + " %");
}

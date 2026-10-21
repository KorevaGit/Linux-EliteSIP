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

    /// <summary>
    /// Есть ли что показать: не <see langword="null"/> и не пустая строка —
    /// <c>PresenceToVisibilityConverter</c> Windows-версии.
    /// </summary>
    public static IValueConverter Present { get; } = new FuncValueConverter<object?, bool>(
        value => value is string text ? text.Length > 0 : value is not null);

    /// <summary>
    /// Член перечисления ↔ выбранный переключатель, в обе стороны —
    /// <c>EnumToBoolConverter</c> Windows-версии. Снятый переключатель ничего
    /// не пишет: в группе снятие одного и есть выбор другого.
    /// </summary>
    public static IValueConverter EnumToBool { get; } = new EnumToBoolConverter();

    /// <summary>Доля 0…1 → проценты целым числом.</summary>
    public static IValueConverter Percent { get; } = new FuncValueConverter<double, string>(
        value => Math.Round(value * 100).ToString(CultureInfo.CurrentCulture) + " %");
}

internal sealed class EnumToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter?.ToString() is not { } name)
        {
            return Avalonia.Data.BindingOperations.DoNothing;
        }

        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type.IsEnum ? Enum.Parse(type, name) : name;
    }
}

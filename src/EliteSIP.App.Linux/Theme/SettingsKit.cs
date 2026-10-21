using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace EliteSIP.App.Theme;

// Кирпичи окон настроек и «Управления» — перевод Theme/SettingsKit Windows-
// версии. Каждый — элемент со своим шаблоном в SettingsKit.axaml: раздел с
// заголовком, строка «подпись над полем», пояснение под ней, разделитель.
// Так разметка окон говорит, ЧТО на экране, а отступы и кегли живут в одном
// месте, как и на Windows.

/// <summary>Раздел: заголовок 18 и строки под ним, 32 до следующего раздела.</summary>
public sealed class SettingsSection : HeaderedItemsControl
{
}

/// <summary>Строка: подпись над полем, поле от 240 до 360 в ширину.</summary>
public sealed class SettingsRow : HeaderedContentControl
{
}

/// <summary>Содержимое с отступом строки, но без подписи.</summary>
public sealed class SettingsIndented : ContentControl
{
}

/// <summary>Пояснение под строкой; тревожное — красным.</summary>
public sealed class SettingsNote : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<SettingsNote, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsAlarmingProperty =
        AvaloniaProperty.Register<SettingsNote, bool>(nameof(IsAlarming));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsAlarming
    {
        get => GetValue(IsAlarmingProperty);
        set => SetValue(IsAlarmingProperty, value);
    }
}

/// <summary>Линия между группами строк.</summary>
public sealed class SettingsDivider : TemplatedControl
{
}

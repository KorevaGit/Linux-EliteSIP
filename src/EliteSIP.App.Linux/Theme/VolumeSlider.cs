using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace EliteSIP.App.Theme;

/// <summary>
/// Ползунок громкости 0–200 %: метка на 100 %, к которой бегунок прилипает при
/// перетаскивании, двойной щелчок — сразу 100 %, колесо мыши — шаг 5 %.
/// </summary>
///
/// <remarks>
/// Одно поведение на панели и в настройках: КЦ подстраивает громкость часто, и
/// вернуться к норме должно быть делом одного движения, а не прицеливания.
/// </remarks>
internal static class VolumeSlider
{
    /// <summary>Насколько близко к 100 % бегунок прилипает к метке.</summary>
    internal const double Magnet = 0.075;

    internal static void Attach(Slider slider)
    {
        slider.Classes.Add("volume");
        var dragging = false;
        slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => dragging = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.PropertyChanged += (_, e) =>
        {
            if (dragging && e.Property == RangeBase.ValueProperty)
            {
                slider.Value = Stick(slider.Value);
            }
        };
        slider.DoubleTapped += (_, e) =>
        {
            if (slider.IsEnabled)
            {
                slider.Value = 1;
                e.Handled = true;
            }
        };
        slider.PointerWheelChanged += (_, e) =>
        {
            if (slider.IsEnabled)
            {
                slider.Value = Math.Clamp(Math.Round((slider.Value + (e.Delta.Y > 0 ? 0.05 : -0.05)) * 20) / 20, slider.Minimum, slider.Maximum);
                e.Handled = true;
            }
        };
    }

    /// <summary>Значение у метки 100 % — сама метка.</summary>
    internal static double Stick(double value) => Math.Abs(value - 1) < Magnet ? 1 : value;
}

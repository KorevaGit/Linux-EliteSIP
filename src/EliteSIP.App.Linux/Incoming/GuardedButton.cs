using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace EliteSIP.App.Incoming;

/// <summary>
/// Цель приёма вызова: кнопка, которой нет для средств доступности.
/// </summary>
///
/// <remarks>
/// <para>
/// Аналог <c>AutomationProperties.IsOffscreenBehavior</c> Windows-версии, но
/// жёстче, потому что мягкого способа здесь нет. В Avalonia 12 мост AT-SPI
/// работает всегда, даже при выключенной доступности рабочего стола, и
/// <c>AccessibilityView="Raw"</c> не учитывает: кнопка «Ответить» видна как
/// <c>push button</c> с действием <c>click</c>, и любой скрипт сеанса
/// (<c>pyatspi</c>, <c>doAction(0)</c>) нажимал её, не тронув мыши, — это
/// проверено на живом окне 22 октября 2026.
/// </para>
/// <para>
/// <see cref="NoneAutomationPeer"/> не отдаёт ни роли, ни действия: в дереве
/// AT-SPI остаётся безымянная панель, которую нельзя нажать. «Отклонить»
/// остаётся обычной кнопкой — отказаться от вызова можно и без мыши.
/// </para>
/// </remarks>
public sealed class GuardedButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override AutomationPeer OnCreateAutomationPeer() => new NoneAutomationPeer(this);
}

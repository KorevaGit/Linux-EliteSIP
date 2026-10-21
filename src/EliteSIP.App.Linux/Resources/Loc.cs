using Avalonia.Markup.Xaml;

namespace EliteSIP.App.Resources;

/// <summary>
/// Подпись в разметке: <c>{res:Loc PanelHold}</c>. Сами подписи — общие
/// <see cref="Strings"/> из EliteSIP.App.Core, те же <c>.resx</c>, что у
/// Windows-версии.
/// </summary>
///
/// <remarks>
/// Подпись читается один раз, при создании окна, — как и на Windows: язык
/// меняется перезапуском, а не на лету.
/// </remarks>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}

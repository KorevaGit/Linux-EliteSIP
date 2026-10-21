using System.Globalization;
using System.Resources;
using Avalonia.Markup.Xaml;

namespace EliteSIP.App.Resources;

/// <summary>
/// Подпись в разметке: <c>{res:Loc PanelHold}</c>. Сами подписи — общие
/// <see cref="Strings"/> из EliteSIP.App.Core, те же <c>.resx</c>, что у
/// Windows-версии; поверх них — Linux-перекрытия (<c>LinuxStrings.resx</c>).
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

    public override object ProvideValue(IServiceProvider serviceProvider) => LinuxStrings.Get(Key);
}

/// <summary>Общие подписи с Linux-перекрытиями.</summary>
internal static class LinuxStrings
{
    private static readonly ResourceManager Overrides =
        new("EliteSIP.App.Resources.LinuxStrings", typeof(LinuxStrings).Assembly);

    /// <summary>Своя подпись Linux, если есть, иначе общая.</summary>
    internal static string Get(string key) =>
        Overrides.GetString(key, CultureInfo.CurrentUICulture) ?? Strings.Get(key);
}

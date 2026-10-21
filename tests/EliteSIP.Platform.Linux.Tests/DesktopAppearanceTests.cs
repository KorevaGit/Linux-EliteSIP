using Tmds.DBus.Protocol;

namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Разбор ответов портала о теме.</summary>
public sealed class DesktopAppearanceTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, false)]
    public void Тёмная_только_по_явной_просьбе(uint scheme, bool dark)
    {
        Assert.Equal(dark, DesktopAppearance.IsDark(VariantValue.UInt32(scheme)));
    }

    [Fact]
    public void Нет_настройки_значит_светлая()
    {
        Assert.False(DesktopAppearance.IsDark(null));
    }

    /// <summary>Значение, которое отдал xapp-портал Cinnamon 21 октября 2026.</summary>
    [Fact]
    public void Акцент_из_долей_в_байты()
    {
        var accent = DesktopAppearance.ToAccent(VariantValue.Struct(
            VariantValue.Double(0.121569), VariantValue.Double(0.619608), VariantValue.Double(0.870588)));

        Assert.Equal(new AccentColor(31, 158, 222), accent);
    }

    [Fact]
    public void Доли_вне_диапазона_значат_акцент_не_задан()
    {
        Assert.Null(DesktopAppearance.ToAccent(VariantValue.Struct(
            VariantValue.Double(-1), VariantValue.Double(-1), VariantValue.Double(-1))));
    }
}

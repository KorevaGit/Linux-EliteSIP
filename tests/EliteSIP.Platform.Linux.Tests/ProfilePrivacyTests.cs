namespace EliteSIP.Platform.Linux.Tests;

/// <summary>Каталог профиля закрыт от других пользователей машины.</summary>
public sealed class ProfilePrivacyTests
{
    private const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = Owner | UnixFileMode.UserExecute;
    private const UnixFileMode Open = Owner | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

    [Fact]
    public void Нового_каталога_нет_создаётся_закрытым()
    {
        using var temporary = new TemporaryDirectory();
        var profile = temporary.Combine("EliteSIP");

        Assert.Empty(ProfilePrivacy.Secure(profile));

        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(profile));
    }

    [Fact]
    public void Открытый_каталог_и_файлы_сужаются()
    {
        // Так было на машине разработки: каталог создала ранняя строка
        // журнала, а настройки и crash.log легли по umask 0002.
        using var temporary = new TemporaryDirectory();
        var profile = temporary.Combine("EliteSIP");
        Directory.CreateDirectory(profile);
        File.SetUnixFileMode(profile, OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var settings = Path.Combine(profile, "settings.json");
        var pairing = Path.Combine(profile, "pairing.json");
        var key = Path.Combine(profile, "secret.key");
        File.WriteAllText(settings, "{}");
        File.WriteAllText(pairing, "{}");
        File.WriteAllText(key, "k");
        File.SetUnixFileMode(settings, Open);
        File.SetUnixFileMode(pairing, Open);
        File.SetUnixFileMode(key, Owner);

        var narrowed = ProfilePrivacy.Secure(profile);

        Assert.Equal(["EliteSIP/", "pairing.json", "settings.json"], narrowed.Order(StringComparer.Ordinal));
        Assert.Equal(OwnerDirectory, File.GetUnixFileMode(profile));
        Assert.Equal(Owner, File.GetUnixFileMode(settings));
        Assert.Equal(Owner, File.GetUnixFileMode(pairing));
        Assert.Equal(Owner, File.GetUnixFileMode(key));
    }

    [Fact]
    public void Закрытый_профиль_ничего_не_сообщает()
    {
        using var temporary = new TemporaryDirectory();
        var profile = temporary.Combine("EliteSIP");
        ProfilePrivacy.Secure(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{}");
        File.SetUnixFileMode(Path.Combine(profile, "settings.json"), Owner);

        Assert.Empty(ProfilePrivacy.Secure(profile));
    }

    [Fact]
    public void Ссылка_в_каталоге_не_трогает_то_на_что_указывает()
    {
        using var temporary = new TemporaryDirectory();
        var profile = temporary.Combine("EliteSIP");
        ProfilePrivacy.Secure(profile);

        var outside = temporary.Combine("чужой.txt");
        File.WriteAllText(outside, "x");
        File.SetUnixFileMode(outside, Open);
        File.CreateSymbolicLink(Path.Combine(profile, "ссылка"), outside);

        ProfilePrivacy.Secure(profile);

        Assert.Equal(Open, File.GetUnixFileMode(outside));
    }

    [Fact]
    public void Каталог_ссылка_не_сужается()
    {
        using var temporary = new TemporaryDirectory();
        var real = temporary.Combine("настоящий");
        Directory.CreateDirectory(real);
        File.SetUnixFileMode(real, OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var profile = temporary.Combine("EliteSIP");
        Directory.CreateSymbolicLink(profile, real);

        Assert.Empty(ProfilePrivacy.Secure(profile));
        Assert.Equal(OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(real));
    }
}

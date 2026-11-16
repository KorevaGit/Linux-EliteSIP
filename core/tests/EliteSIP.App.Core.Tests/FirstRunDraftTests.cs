using EliteSIP.AdminAccess;
using EliteSIP.App.FirstRun;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

public sealed class FirstRunRedesignTests
{
    [Fact]
    public void Ручная_настройка_остаётся_черновиком_до_завершения()
    {
        var settings = new AppSettings();
        using var model = new FirstRunViewModel(settings, new AdminAccessState());
        model.Begin();
        Assert.True(model.ShowsPairing);
        Assert.False(model.CanGoForward);
        model.UseManualSetup();
        model.Username = "176";
        model.DisplayName = "Office";
        model.OfficeAddress = "pbx.test";
        model.SipPassword = "test-secret";
        Assert.True(model.CanGoForward);
        Assert.False(settings.Setup.IsCompleted);
        Assert.NotEqual("176", settings.Account.Username);
        model.AdminPassword = "admin-test";
        model.RepeatedAdminPassword = "different";
        Assert.False(model.CanGoForward);
        model.AdminPassword = model.RepeatedAdminPassword = string.Empty;
        model.Forward();
        Assert.True(model.ShowsAppearance);
        model.Back();
        Assert.True(model.ShowsUser);
        Assert.Equal("test-secret", model.SipPassword);
        model.Forward();
        model.Forward();
        Assert.True(model.ShowsFinale);
        Assert.Equal("176", model.ProfileNumber);
        model.Language = settings.Appearance.Language is LanguageSetting.English ? LanguageSetting.Russian : LanguageSetting.English;
        model.Complete();
        Assert.True(settings.Setup.IsCompleted);
        Assert.Equal("test-secret", settings.Credentials.Password());
        Assert.True(model.LanguageChanges);
    }

}

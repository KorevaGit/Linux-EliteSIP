using Avalonia;
using Avalonia.Headless;
using EliteSIP.App.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessSetup))]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace EliteSIP.App.Tests;

/// <summary>
/// Приложение для проверок: то же <see cref="App"/>, что в продукте, — тема,
/// палитра и стили те же, — но без экрана. Отрисовка Skia, а не заглушка
/// Headless: снимки окон должны выглядеть как на экране.
/// </summary>
public static class HeadlessSetup
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

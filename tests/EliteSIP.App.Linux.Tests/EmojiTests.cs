using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EliteSIP.App.Panel;
using EliteSIP.App.Theme;

namespace EliteSIP.App.Tests;

/// <summary>
/// Смайлики Linux-версии: пакет из выпуска и отрисовка. Разбор строки — общий
/// и проверяется в App.Core.Tests.
/// </summary>
public sealed class EmojiTests
{
    [AvaloniaFact]
    public void Пакет_из_выпуска_рядом_с_программой()
        => Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "emoji.pak")), "emoji.pak не попал в выход сборки");

    [AvaloniaFact]
    public void Подпись_со_смайликом_становится_строкой_с_картинкой()
    {
        var block = new TextBlock { FontSize = 16 };
        Emoji.SetText(block, "Акции \U0001F525");

        // Картинка в строке — значит, пакет открылся и PNG разобрался.
        Assert.Contains(block.Inlines!, inline => inline is InlineUIContainer { Child: Image { Source: not null } });
        Assert.Contains(block.Inlines!, inline => inline is Run { Text: "Акции " });

        Emoji.SetText(block, "Акции");
        Assert.Equal("Акции", block.Text);
        Assert.DoesNotContain(block.Inlines!, inline => inline is InlineUIContainer);
    }

    /// <summary>Клавиши макросов со смайликами — ради них пакет и есть.</summary>
    [AvaloniaFact]
    public void Панель_со_смайликами_на_клавишах()
    {
        PanelViewModel model = new() { StatusTitle = "701", StatusLabel = "Продажи \U0001F525", MacroColumns = 2 };
        model.Macros.Add(new MacroViewModel("Горячий \U0001F525", "1"));
        model.Macros.Add(new MacroViewModel("Перезвон \U0001F4DE", "2"));

        PanelWindow window = new(model) { AllowsClosing = true };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Snapshot.Save(window.CaptureRenderedFrame()!, "panel-emoji");
        window.Close();
    }
}

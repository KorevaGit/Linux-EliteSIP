using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EliteSIP.App.Theme;

/// <summary>
/// Подпись со смайликами Apple: <c>theme:Emoji.Text="{Binding Title}"</c> на
/// <see cref="TextBlock"/> — перевод <c>Theme/Emoji</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Подписи, которые администратор набирает в Spark, задуманы со смайликами
/// Apple (0.1.66). На Linux цветной шрифт есть не везде (Noto Color Emoji на
/// Mint стоит, на минимальных системах — нет), и рисует он их по-своему;
/// поэтому, как на Windows, смайлик — картинка из того же <c>emoji.pak</c>.
/// Разбор строки общий (<see cref="EmojiPack"/>, App.Core).
/// </para>
/// <para>
/// Смайлик на четверть выше кегля: у шрифта высота строки больше кегля, и
/// равный ему смайлик выглядит мельче соседних букв.
/// </para>
/// </remarks>
public sealed class Emoji : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Emoji, TextBlock, string?>("Text");

    private static readonly Dictionary<string, Bitmap?> Images = [];

    private static readonly FuncValueConverter<double, double> Scale = new(size => size * 1.25);

    static Emoji()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, change) => Fill(block, change.GetNewValue<string?>() ?? string.Empty));
    }

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>Готовая строка — для пунктов меню, которые собираются в коде.</summary>
    public static TextBlock Block(string text)
    {
        var block = new TextBlock();
        Fill(block, text);
        return block;
    }

    private static void Fill(TextBlock block, string text)
    {
        block.Inlines?.Clear();

        if (!EmojiPack.MayContainEmoji(text) || EmojiPack.Shared is not { } pack)
        {
            block.Text = text;
            return;
        }

        var pieces = pack.Split(text);
        if (pieces.All(piece => piece.Key is null))
        {
            block.Text = text;
            return;
        }

        block.Text = null;
        var inlines = block.Inlines ??= [];
        foreach (var piece in pieces)
        {
            if (piece.Text is { } run)
            {
                inlines.Add(new Run(run));
                continue;
            }

            if (Image(pack, piece.Key!) is not { } source)
            {
                continue;
            }

            var image = new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(1, 0, 1, 0),
            };

            // Привязкой, а не числом: кегль подписи меняет ShrinkBox уже после
            // заполнения, и смайлик должен расти вместе с буквами.
            image.Bind(Layoutable.HeightProperty, new Binding(nameof(TextBlock.FontSize)) { Source = block, Converter = Scale });

            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
    }

    private static Bitmap? Image(EmojiPack pack, string name)
    {
        if (Images.TryGetValue(name, out var cached))
        {
            return cached;
        }

        Bitmap? image = null;
        try
        {
            if (pack.OpenImage(name) is { } stream)
            {
                using (stream)
                {
                    image = new Bitmap(stream);
                }
            }
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException)
        {
            image = null;
        }

        Images[name] = image;
        return image;
    }
}

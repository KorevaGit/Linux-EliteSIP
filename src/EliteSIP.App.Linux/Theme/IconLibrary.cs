using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace EliteSIP.App.Theme;

/// <summary>
/// Комплект значков — тот же файл, что у Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// <c>shared/src/EliteSIP.App/Theme/Icons.xaml</c> собирает стенд IconGen из
/// SVG macOS-версии, и правится он там же. Переписывать его под Avalonia —
/// значит держать второй комплект, который разъедется с первым на первой же
/// новой иконке. Поэтому сюда он встраивается как есть (ресурс сборки) и
/// разбирается при запуске: в нём ровно три вида узлов — <c>Canvas</c> с
/// ключом, вложенные <c>Canvas</c> с преобразованием и <c>Path</c> со
/// штрихом или заливкой цветом значка.
/// </para>
/// <para>
/// Разбор — 37 значков, доли миллисекунды; делается один раз, при первом
/// значке.
/// </para>
/// </remarks>
internal static class IconLibrary
{
    /// <summary>Сторона холста значка в единицах файла.</summary>
    internal const double CanvasSize = 20;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Lazy<Dictionary<string, IconShape>> Shapes = new(Load);

    internal static IReadOnlyDictionary<string, IconShape> All => Shapes.Value;

    internal static IconShape? Find(string? name) =>
        name is not null && All.TryGetValue(name, out var shape) ? shape : null;

    private static Dictionary<string, IconShape> Load()
    {
        using var stream = typeof(IconLibrary).Assembly.GetManifestResourceStream("EliteSIP.App.Theme.Icons.xaml")
            ?? throw new InvalidOperationException("в сборке нет Icons.xaml");

        return Parse(XDocument.Load(stream));
    }

    internal static Dictionary<string, IconShape> Parse(XDocument document)
    {
        Dictionary<string, IconShape> shapes = new(StringComparer.Ordinal);

        foreach (var canvas in document.Root!.Elements().Where(element => element.Name.LocalName == "Canvas"))
        {
            if (canvas.Attribute(Xaml + "Key")?.Value is not { } key)
            {
                continue;
            }

            List<IconStroke> strokes = [];
            Walk(canvas, Matrix.Identity, strokes);
            shapes[key] = new IconShape(strokes);
        }

        return shapes;
    }

    private static void Walk(XElement canvas, Matrix outer, List<IconStroke> strokes)
    {
        foreach (var child in canvas.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Path":
                    strokes.Add(Stroke(child, outer));
                    break;

                case "Canvas":
                    // Преобразование вложенного холста действует раньше
                    // внешнего — порядок WPF: сначала своё, потом родителя.
                    Walk(child, Transform(child) * outer, strokes);
                    break;
            }
        }
    }

    private static Matrix Transform(XElement canvas)
    {
        var group = canvas.Elements().FirstOrDefault(element => element.Name.LocalName == "Canvas.RenderTransform")
            ?.Elements().FirstOrDefault();
        if (group is null)
        {
            return Matrix.Identity;
        }

        // TransformGroup применяет детей по порядку записи.
        IEnumerable<XElement> parts = group.Name.LocalName == "TransformGroup" ? group.Elements() : [group];
        var matrix = Matrix.Identity;
        foreach (var part in parts)
        {
            matrix *= part.Name.LocalName switch
            {
                "ScaleTransform" => Matrix.CreateScale(Number(part, "ScaleX", 1), Number(part, "ScaleY", 1)),
                "TranslateTransform" => Matrix.CreateTranslation(Number(part, "X", 0), Number(part, "Y", 0)),
                "RotateTransform" => Rotation(part),
                _ => Matrix.Identity,
            };
        }

        return matrix;
    }

    private static Matrix Rotation(XElement part)
    {
        var x = Number(part, "CenterX", 0);
        var y = Number(part, "CenterY", 0);
        return Matrix.CreateTranslation(-x, -y)
            * Matrix.CreateRotation(Number(part, "Angle", 0) * Math.PI / 180)
            * Matrix.CreateTranslation(x, y);
    }

    private static IconStroke Stroke(XElement path, Matrix transform) => new(
        Geometry.Parse(path.Attribute("Data")!.Value),
        transform,
        IsStroked: path.Attribute("Stroke") is not null,
        IsFilled: path.Attribute("Fill") is not null,
        Thickness: Number(path, "StrokeThickness", 1),
        Cap: path.Attribute("StrokeStartLineCap")?.Value == "Round" ? PenLineCap.Round : PenLineCap.Flat,
        Join: path.Attribute("StrokeLineJoin")?.Value == "Round" ? PenLineJoin.Round : PenLineJoin.Miter);

    private static double Number(XElement element, string name, double fallback) =>
        element.Attribute(name)?.Value is { } value
            ? double.Parse(value, CultureInfo.InvariantCulture)
            : fallback;
}

/// <summary>Один значок: штрихи в единицах холста 20 × 20.</summary>
internal sealed record IconShape(IReadOnlyList<IconStroke> Strokes);

/// <summary>Один <c>Path</c> значка с итоговым преобразованием холстов над ним.</summary>
internal sealed record IconStroke(
    Geometry Geometry,
    Matrix Transform,
    bool IsStroked,
    bool IsFilled,
    double Thickness,
    PenLineCap Cap,
    PenLineJoin Join);

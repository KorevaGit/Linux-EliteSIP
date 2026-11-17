using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
namespace EliteSIP.App.Theme;
/// <summary>Identical 12 DIP canvas and stroke for all product window controls.</summary>
internal sealed class CaptionGlyph : Control
{
    internal static readonly StyledProperty<string> KindProperty=AvaloniaProperty.Register<CaptionGlyph,string>(nameof(Kind),"Close");
    public static readonly StyledProperty<IBrush?> ForegroundProperty=TextElement.ForegroundProperty.AddOwner<CaptionGlyph>();
    internal string Kind {get=>GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    public IBrush? Foreground {get=>GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value);}
    static CaptionGlyph() {AffectsRender<CaptionGlyph>(KindProperty,ForegroundProperty);}
    internal CaptionGlyph() {Width=12;Height=12;IsHitTestVisible=false;HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center;VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center;}
    public override void Render(DrawingContext context)
    {
        var pen=new Pen(Foreground??Brushes.Gray,1.2);
        switch(Kind)
        {
            case "Minimize":context.DrawLine(pen,new Point(1,6),new Point(11,6));break;
            case "Maximize":context.DrawRectangle(null,pen,new Rect(1,1,10,10));break;
            case "Restore":
                context.DrawGeometry(null,pen,Geometry.Parse("M3 1H11V9 M1 3H9V11H1Z"));break;
            default:context.DrawLine(pen,new Point(1,1),new Point(11,11));context.DrawLine(pen,new Point(11,1),new Point(1,11));break;
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
namespace EliteSIP.App.Theme;
public sealed class ProcessingPreview : Control
{
    public static readonly StyledProperty<bool> IsProcessingProperty=AvaloniaProperty.Register<ProcessingPreview,bool>(nameof(IsProcessing));
    public static readonly StyledProperty<IBrush?> ForegroundProperty=TextElement.ForegroundProperty.AddOwner<ProcessingPreview>();
    public bool IsProcessing { get=>GetValue(IsProcessingProperty);set=>SetValue(IsProcessingProperty,value); }
    public IBrush? Foreground { get=>GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value); }
    static ProcessingPreview() { AffectsRender<ProcessingPreview>(IsProcessingProperty,ForegroundProperty); }
    protected override Size MeasureOverride(Size availableSize)=>new(240,32);
    public override void Render(DrawingContext context)
    {
        var brush=Foreground??Brushes.Gray;var pen=new Pen(brush,1.2);
        var half=(Bounds.Width-24)/2;
        for(var side=0;side<2;side++)
        {
            var origin=side*(half+24);Point? before=null;
            for(var x=0;x<(int)half;x++)
            {
                var voice=Math.Sin(x*.20)*Math.Sin(x*.047)*9;
                var noise=Math.Sin(x*2.7)*3;
                var y=Bounds.Height/2+voice+(side==1&&IsProcessing ? noise*.1 : noise);
                var point=new Point(origin+x,y);
                if(before is { } previous) { context.DrawLine(pen,previous,point); } before=point;
            }
        }
        context.DrawLine(pen,new Point(half+4,16),new Point(half+20,16));
        context.DrawLine(pen,new Point(half+16,12),new Point(half+20,16));
        context.DrawLine(pen,new Point(half+16,20),new Point(half+20,16));
    }
}

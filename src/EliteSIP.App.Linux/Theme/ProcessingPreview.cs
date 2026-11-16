using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EliteSIP.App.Theme;

/// <summary>Windows processing illustration, adapted to Avalonia; not an audio meter.</summary>
public sealed class ProcessingPreview : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty = AvaloniaProperty.Register<ProcessingPreview,bool>(nameof(IsActive));
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ProcessingPreview,string>(nameof(Kind),"Level");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ProcessingPreview>();
    public static readonly StyledProperty<IBrush?> ProcessedBrushProperty = AvaloniaProperty.Register<ProcessingPreview,IBrush?>(nameof(ProcessedBrush));
    private static readonly double[] Noise = [7,5,21,8,6];
    private static readonly double[] Level = [5,21,9,18,6];
    public bool IsActive { get=>GetValue(IsActiveProperty);set=>SetValue(IsActiveProperty,value); }
    public string Kind { get=>GetValue(KindProperty);set=>SetValue(KindProperty,value); }
    public IBrush? Foreground { get=>GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value); }
    public IBrush? ProcessedBrush { get=>GetValue(ProcessedBrushProperty);set=>SetValue(ProcessedBrushProperty,value); }
    static ProcessingPreview() { AffectsRender<ProcessingPreview>(IsActiveProperty,KindProperty,ForegroundProperty,ProcessedBrushProperty); }
    public ProcessingPreview() { Width=102;Height=24;IsHitTestVisible=false; }
    public override void Render(DrawingContext context)
    {
        var secondary=Foreground??Brushes.Gray;
        var active=IsActive?ProcessedBrush??Brushes.Green:secondary;
        var pen=new Pen(secondary,1);
        context.DrawLine(pen,new Point(44,12),new Point(56,12));
        context.DrawLine(pen,new Point(52,8),new Point(56,12));
        context.DrawLine(pen,new Point(52,16),new Point(56,12));
        if(Kind=="Voices")
        {
            for(var side=0;side<2;side++)
            {
                for(var person=0;person<3;person++)
                {
                    using var opacity=context.PushOpacity(side==1&&IsActive&&person!=1?.15:1);
                    var x=side*64+person*11+5;var brush=side==0?secondary:active;
                    context.DrawEllipse(brush,null,new Point(x,7),3,3);
                    context.DrawRectangle(brush,null,new RoundedRect(new Rect(x-4,12,8,9),3));
                }
            }
            return;
        }
        var raw=Kind=="Noise"?Noise:Level;
        for(var i=0;i<raw.Length;i++)
        {
            var before=raw[i];var after=!IsActive?before:Kind=="Noise"?i==2?before:2:14+i%2*2;
            context.DrawRectangle(secondary,null,new RoundedRect(new Rect(i*7,(24-before)/2,4,before),1));
            context.DrawRectangle(active,null,new RoundedRect(new Rect(64+i*7,(24-after)/2,4,after),1));
        }
    }
}

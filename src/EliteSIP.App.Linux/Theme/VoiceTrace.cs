using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
namespace EliteSIP.App.Theme;
public sealed class VoiceTrace : Control
{
    public static readonly StyledProperty<double> LevelProperty=AvaloniaProperty.Register<VoiceTrace,double>(nameof(Level));
    public static readonly StyledProperty<bool> IsRunningProperty=AvaloniaProperty.Register<VoiceTrace,bool>(nameof(IsRunning));
    private readonly Queue<double> _levels=new();
    public double Level { get=>GetValue(LevelProperty);set=>SetValue(LevelProperty,value); }
    public bool IsRunning { get=>GetValue(IsRunningProperty);set=>SetValue(IsRunningProperty,value); }
    static VoiceTrace()
    {
        LevelProperty.Changed.AddClassHandler<VoiceTrace>((trace,args)=>
        { if(trace.IsRunning) { trace._levels.Enqueue(Math.Clamp(trace.Level,0,1));while(trace._levels.Count>80) { trace._levels.Dequeue(); } }trace.InvalidateVisual(); });
        IsRunningProperty.Changed.AddClassHandler<VoiceTrace>((trace,_)=> { if(!trace.IsRunning) { trace._levels.Clear(); }trace.InvalidateVisual(); });
    }
    protected override Size MeasureOverride(Size availableSize)=>new(240,44);
    public override void Render(DrawingContext context)
    {
        var center=Bounds.Height/2;var index=80-_levels.Count;var pen=new Pen(Brushes.Gray,2);
        foreach(var value in _levels) { var x=index++*Bounds.Width/80;var radius=Math.Max(.5,value*(center-2));context.DrawLine(pen,new Point(x,center-radius),new Point(x,center+radius)); }
    }
}

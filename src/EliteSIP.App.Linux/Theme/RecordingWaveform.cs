using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace EliteSIP.App.Theme;

/// <summary>Windows recording trace: 24 samples of the real microphone level.</summary>
public sealed class RecordingWaveform : Control
{
    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<RecordingWaveform,double>(nameof(Level));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<RecordingWaveform>();
    public static readonly StyledProperty<IBrush?> TrackBrushProperty = AvaloniaProperty.Register<RecordingWaveform,IBrush?>(nameof(TrackBrush));
    private readonly Queue<double> _levels = new();
    public double Level { get=>GetValue(LevelProperty);set=>SetValue(LevelProperty,value); }
    public IBrush? Foreground { get=>GetValue(ForegroundProperty);set=>SetValue(ForegroundProperty,value); }
    public IBrush? TrackBrush { get=>GetValue(TrackBrushProperty);set=>SetValue(TrackBrushProperty,value); }
    public RecordingWaveform() { Height=25;MinWidth=60; }
    static RecordingWaveform()
    {
        AffectsRender<RecordingWaveform>(LevelProperty,ForegroundProperty,TrackBrushProperty);
        LevelProperty.Changed.AddClassHandler<RecordingWaveform>((trace,_)=>
        {
            trace._levels.Enqueue(Math.Sqrt(Math.Clamp(trace.Level,0,1)));
            while(trace._levels.Count>24) { trace._levels.Dequeue(); }
        });
    }
    public override void Render(DrawingContext context)
    {
        var count=Math.Min(24,Math.Max(1,(int)(Bounds.Width/5)));
        var levels=_levels.ToArray();
        for(var i=0;i<count;i++)
        {
            var sample=i-count+levels.Length;var level=sample>=0?levels[sample]:0;
            var height=Math.Max(3,level*Bounds.Height);
            context.DrawRectangle(level>0?Foreground??Brushes.Gray:TrackBrush??Brushes.LightGray,null,
                new RoundedRect(new Rect(i*5,(Bounds.Height-height)/2,3,height),1.5));
        }
    }
}

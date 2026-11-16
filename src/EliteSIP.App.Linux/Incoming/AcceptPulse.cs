using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
namespace EliteSIP.App.Incoming;
internal static class AcceptPulse
{
    internal static bool ShouldPulse(GuardedButton button) => button.IsEffectivelyVisible && button.IsEnabled && !button.IsPointerOver;
    internal static void Attach(Window window)
    {
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        var phase=0d;
        timer.Tick+=(_,_)=>
        {
            phase+=.35;
            foreach(var button in window.GetVisualDescendants().OfType<GuardedButton>())
            { button.Opacity=ShouldPulse(button) ? .94+.06*Math.Sin(phase) : 1; }
        };
        window.Closed+=(_,_)=>timer.Stop(); timer.Start();
    }
}

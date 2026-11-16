using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
namespace EliteSIP.App.Theme;
internal static class ResizeEffects
{
    private static readonly ConditionalWeakTable<Window, DispatcherTimer> Timers=new();
    internal static void Register()
    {
        Window.ClientSizeProperty.Changed.AddClassHandler<Window>((window,_)=>
        {
            if(!window.IsVisible) { return; }
            var timer=Timers.GetValue(window,w=>
            {
                var created=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(160) };
                created.Tick+=(_,_)=> { created.Stop(); w.Classes.Remove("resizing"); };
                w.Closed+=(_,_)=>created.Stop(); return created;
            });
            window.Classes.Add("resizing"); timer.Stop();timer.Start();
        });
    }
}

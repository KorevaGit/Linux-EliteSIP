using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using EliteSIP.App.Panel;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using SkiaSharp;

namespace EliteSIP.Tools.TrayProbe;
internal static partial class Program
{
    private static void PerformanceTest()
    {
        var startup=Stopwatch.StartNew();
        AppBuilder.Configure<EliteSIP.App.App>().UsePlatformDetect().SetupWithoutStarting();
        using var stop=new CancellationTokenSource();
        Dispatcher.UIThread.Post(async ()=>
        {
            try
            {
                var path=Path.Combine(Output,"performance-wallpaper.png");
                using(var pixels=new SKBitmap(1600,1000))
                {
                    var random=new Random(17);
                    for(var y=0;y<1000;y++) for(var x=0;x<1600;x++)
                    {var n=random.Next(25);pixels.SetPixel(x,y,new SKColor((byte)(35+n),(byte)(110+n),(byte)(175+n)));}
                    using var data=pixels.Encode(SKEncodedImageFormat.Png,100);using var file=File.Create(path);data.SaveTo(file);
                }
                var settings=new AppearanceSettings {Theme=Appearance.Wallpaper,BackgroundImagePath=path};
                Application.Current!.RequestedThemeVariant=Avalonia.Styling.ThemeVariant.Dark;
                var watch=Stopwatch.StartNew();
                using var background=new WindowBackgroundService(Application.Current!,settings,()=>true);
                var initial=watch.Elapsed.TotalMilliseconds;
                var panel=new PanelWindow(new PanelViewModel {StatusTitle="701"}) {AllowsClosing=true};
                panel.Show();await Task.Delay(500);Capture(panel,"performance-panel");
                var startupMs=startup.Elapsed.TotalMilliseconds;
                var alloc=GC.GetTotalAllocatedBytes(true);watch.Restart();
                for(var i=0;i<8;i++) {settings.BackgroundBlur=i+1;settings.BackgroundBrightness=100+i;background.Apply();}
                var adjustmentsMs=watch.Elapsed.TotalMilliseconds;
                var allocated=GC.GetTotalAllocatedBytes(true)-alloc;
                var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;watch.Restart();
                var imageBuilds=background.ImageBuildCount;var paletteBuilds=background.PaletteBuildCount;
                await Task.Delay(10000);process.Refresh();
                var idleCpu=(process.TotalProcessorTime-cpu).TotalMilliseconds;var idleWall=watch.Elapsed.TotalMilliseconds;
                var visibleRss=process.WorkingSet64;
                panel.Hide();cpu=process.TotalProcessorTime;watch.Restart();await Task.Delay(10000);process.Refresh();
                var result=new {fixture="native X11 panel + synthetic 1600x1000 wallpaper; no SIP/audio",startup_ms=startupMs,background_initial_ms=initial,adjustments_8_ms=adjustmentsMs,adjustments_allocated_bytes=allocated,visible_idle_cpu_ms=idleCpu,visible_idle_wall_ms=idleWall,visible_rss_bytes=visibleRss,hidden_idle_cpu_ms=(process.TotalProcessorTime-cpu).TotalMilliseconds,hidden_idle_wall_ms=watch.Elapsed.TotalMilliseconds,hidden_rss_bytes=process.WorkingSet64,managed_heap_bytes=GC.GetTotalMemory(false),idle_image_builds=background.ImageBuildCount-imageBuilds,idle_palette_builds=background.PaletteBuildCount-paletteBuilds};
                Console.WriteLine(JsonSerializer.Serialize(result));panel.Close();
            }
            catch(Exception error) {Console.WriteLine(error);Environment.ExitCode=1;}
            finally {stop.Cancel();}
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}

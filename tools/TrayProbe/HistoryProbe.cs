using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using EliteSIP.App.History;
using EliteSIP.App.Incoming;
using EliteSIP.CallGuard;
using EliteSIP.App.Panel;
using EliteSIP.CallHistory;
using System.Diagnostics;
using System.Globalization;

namespace EliteSIP.Tools.TrayProbe;

internal static partial class Program
{
    private static void HistoryTest()
    {
        Console.WriteLine("Snapshots: " + Output);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        AppBuilder.Configure<EliteSIP.App.App>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                using var store = new CallHistoryStore(new CallHistoryStore.Settings(Path.Combine(Output,"fixture.db")));
                var profile = Guid.NewGuid();
                foreach(var (name,outcome,direction) in new[] { ("Отдел продаж",CallOutcome.Completed,CallDirection.Outgoing), ("Юрист",CallOutcome.Missed,CallDirection.Incoming), ("Приёмная",CallOutcome.Busy,CallDirection.Outgoing) })
                {
                    var at = DateTimeOffset.Now.AddMinutes(-10);
                    var call = new CallRecord { CallId=Guid.NewGuid().ToString("N"), Number="712",DisplayName=name,ProfileId=profile,StartedAt=at,Direction=direction };
                    store.Begin(call);
                    if(outcome==CallOutcome.Completed) { store.MarkAnswered(call.Id,at.AddSeconds(2)); }
                    store.Finish(call.Id,reason:"fixture",outcome:outcome,at:at.AddSeconds(97));
                }
                store.Flush();
                var window = new CallHistoryWindow(new CallHistoryViewModel(store,profile,"701",true,()=>30));
                window.Show();
                for(var variant=0;variant<2;variant++)
                {
                    var dark=variant==1;
                    Application.Current!.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
                    await Task.Delay(200);
                    Capture(window,"history-"+(dark?"dark":"light"));
                    window.CalendarPopup.IsOpen=true;await Task.Delay(100);
                    var border=(Border)window.CalendarPopup.Child!;
                    using(var image=new RenderTargetBitmap(PixelSize.FromSize(border.Bounds.Size,1),new Vector(96,96)))
                    { image.Render(border);image.Save(Path.Combine(Output,"calendar-"+(dark?"dark":"light")+".png"),PngBitmapEncoderOptions.Default); }
                    window.CalendarPopup.IsOpen=false;
                }
                await window.CopySnapshotAsync(window.Clipboard!);
                window.Close();await Task.Delay(100);
                // GTK is a separate process: its X11 selection request arrives after
                // the click handler and the history window have both finished.
                for(var i=0;i<3;i++)
                {
                    var start=new ProcessStartInfo("/usr/bin/python3") { RedirectStandardOutput=true,RedirectStandardError=true };
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add("import gi; gi.require_version('Gtk','3.0'); from gi.repository import Gtk,Gdk; p=Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).wait_for_image(); assert p is not None; print(str(p.get_width())+'x'+str(p.get_height()))");
                    using var child=Process.Start(start)!;
                    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var output=await child.StandardOutput.ReadToEndAsync(timeout.Token);
                    await child.WaitForExitAsync(timeout.Token);
                    if(child.ExitCode!=0) { throw new InvalidOperationException(await child.StandardError.ReadToEndAsync()); }
                    Console.WriteLine("X11 delayed image request "+(i+1)+": "+output.Trim());
                }
                var model=new PanelViewModel { StatusTitle="701",Registration=RegistrationState.Registered };
                var panel=new PanelWindow(model) { AllowsClosing=true };panel.Show();model.GoOffline.Execute(null);await Task.Delay(100);Capture(panel,"profile-offline");panel.Close();
                Console.WriteLine("History closed; repeated external clipboard reads succeeded; offline panel captured");
                for(var kind=0;kind<2;kind++)
                {
                    var deal=kind==1;
                    var subject=IncomingCallSubject.Classify(deal?"701":"79161234567","Тест",!deal,"701",true);
                    var incoming=new IncomingCallWindow(new IncomingCallViewModel(subject,CallGuardChallenge.Build(new CallGuardPolicy {TargetCount=1}.Normalized(),new Random(7)),false,_=>{},()=>{}));
                    incoming.Show();await Task.Delay(150);
                    var name=deal?"incoming-deal":"incoming-hot";
                    var start=new ProcessStartInfo("/usr/bin/python3") {RedirectStandardError=true};
                    start.ArgumentList.Add("-c");
                    start.ArgumentList.Add("import gi,sys; gi.require_version('Gtk','3.0'); gi.require_version('GdkX11','3.0'); from gi.repository import Gdk,GdkX11; w=GdkX11.X11Window.foreign_new_for_display(Gdk.Display.get_default(),int(sys.argv[1])); p=Gdk.pixbuf_get_from_window(w,0,0,w.get_width(),w.get_height()); assert p is not None; p.savev(sys.argv[2],'png',[],[])");
                    start.ArgumentList.Add(incoming.TryGetPlatformHandle()!.Handle.ToInt64().ToString(CultureInfo.InvariantCulture));
                    start.ArgumentList.Add(Path.Combine(Output,name+"-x11.png"));
                    using var child=Process.Start(start)!;await child.WaitForExitAsync();
                    if(child.ExitCode!=0) {throw new InvalidOperationException(await child.StandardError.ReadToEndAsync());}
                    Console.WriteLine("Native X11 incoming capture: "+name+", decorations="+incoming.WindowDecorations+", size="+incoming.ClientSize);
                    incoming.Close();
                }
            }
            catch(Exception error) { Console.WriteLine(error);Environment.ExitCode=1; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}

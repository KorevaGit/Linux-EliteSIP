using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using EliteSIP.App;
using EliteSIP.App.Panel;
using EliteSIP.App.Shell;
using EliteSIP.App.Theme;
using System.Diagnostics;
using System.Globalization;
using Tmds.DBus.Protocol;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]
namespace EliteSIP.Tools.TrayProbe;

internal static partial class Program
{
 private static readonly string Output=Directory.CreateTempSubdirectory("elitesip-tray-probe-").FullName;
 static bool SniTest => Environment.GetEnvironmentVariable("ELITESIP_TEST_SNI")=="1";
 static string Service => SniTest?$"org.kde.StatusNotifierItem-{Environment.ProcessId}-1":$"org.x.StatusIcon.elitesip_{Environment.ProcessId}";
 sealed class Watcher : IPathMethodHandler
 {
  internal int Calls;
  public string Path=>"/StatusNotifierWatcher";
  public bool HandlesChildPaths=>false;
  public ValueTask HandleMethodAsync(MethodContext context)
  {
   if(context.Request.MemberAsString=="RegisterStatusNotifierItem") { Calls++;using var w=context.CreateReplyWriter(null);context.Reply(w.CreateMessage()); }
   else { context.ReplyUnknownMethodError(); }
   return ValueTask.CompletedTask;
  }
 }
 static MessageBuffer Call(DBusConnection c,string method,string? property=null)
 {
  var service=Service;
  using var w=c.GetMessageWriter();
  w.WriteMethodCallHeader(service,"/StatusNotifierItem",property is null?"org.kde.StatusNotifierItem":"org.freedesktop.DBus.Properties",property is null?method:"Get",property is null?"ii":"ss",MessageFlags.None);
  if(property is null) { w.WriteInt32(1820);w.WriteInt32(1060); } else { w.WriteString("org.kde.StatusNotifierItem");w.WriteString(property); }
  return w.CreateMessage();
 }
 static void Capture(Window window,string name)
 {
  using var bmp=new RenderTargetBitmap(PixelSize.FromSize(window.ClientSize,1),new Vector(96,96));bmp.Render(window);bmp.Save(Path.Combine(Output,name+".png"),PngBitmapEncoderOptions.Default);
 }
 static async Task SniTransportOnly()
 {
  using var bus=new DBusConnection(DBusAddress.Session!);await bus.ConnectAsync();
  var services=await bus.ListServicesAsync();
  if(services.Contains("org.kde.StatusNotifierWatcher")||services.Any(name=>name.StartsWith("org.x.StatusIconMonitor.",StringComparison.Ordinal))) { throw new InvalidOperationException("SNI probe requires an isolated dbus-run-session bus"); }
  var watcher=new Watcher();bus.AddMethodHandler(watcher);await bus.RequestNameAsync("org.kde.StatusNotifierWatcher");
  var activates=0;var contexts=0;
  using var icon=new StatusNotifierIcon(()=>activates++,(x,y)=> { if(x!=1820||y!=1060) { throw new InvalidOperationException("Incorrect menu coordinates"); }contexts++; },Console.WriteLine);
  var pixels=new byte[64*64*4];pixels[0]=255;pixels[2]=255;icon.Update("Test 701",64,pixels);
  var running=icon.StartAsync();for(var i=0;i<50&&!icon.IsRegistered;i++) { await Task.Delay(50); }
  if(!icon.IsRegistered||icon.UsesDesktopMonitor) { throw new InvalidOperationException("SNI did not register in isolated bus"); }
  var pix=await bus.CallMethodAsync(Call(bus,"Get","IconPixmap"),static (m,_)=>m.GetBodyReader().ReadVariantValue(),null);
  if(pix.Count!=1||pix.GetItem(0).GetItem(2).Count!=16384) { throw new InvalidOperationException("Bad pixmap encoding"); }
  await bus.CallMethodAsync(Call(bus,"ContextMenu"));await bus.CallMethodAsync(Call(bus,"Activate"));
  if(contexts!=1||activates!=1) { throw new InvalidOperationException("SNI activation/menu callbacks were not separated"); }
  Console.WriteLine("SNI isolated bus: pixmap decoded, ContextMenu/Activate dispatched separately");
  await bus.ReleaseNameAsync("org.kde.StatusNotifierWatcher");await Task.Delay(150);
  if(icon.IsRegistered) { throw new InvalidOperationException("Tray stayed registered after watcher disappeared"); }
  await bus.RequestNameAsync("org.kde.StatusNotifierWatcher");
  for(var i=0;i<50&&!icon.IsRegistered;i++) { await Task.Delay(50); }
  if(!icon.IsRegistered||watcher.Calls<2) { throw new InvalidOperationException("Watcher restart did not re-register icon"); }
  Console.WriteLine("SNI watcher restart: unavailable after disappearance; re-registered after return");
  icon.Dispose();await running;
 }
 [STAThread] static void Main()
 {
  if(Environment.GetEnvironmentVariable("ELITESIP_TEST_HISTORY")=="1") { HistoryTest();return; }
  if(SniTest) { SniTransportOnly().GetAwaiter().GetResult();return; }
  Console.WriteLine("Snapshots: "+Output);
  CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
  AppBuilder.Configure<EliteSIP.App.App>().UsePlatformDetect().SetupWithoutStarting();
  using var stop=new CancellationTokenSource();
  Dispatcher.UIThread.Post(async () => {
   try {
    var app=Application.Current!;app.RequestedThemeVariant=ThemeVariant.Dark;
    var model=new PanelViewModel { StatusTitle="701",Registration=RegistrationState.Registered };var activates=0;
    var panel=new PanelWindow(model) { ShowActivated=false, AllowsClosing=true };panel.Show();
    using var tray=new Tray(model,new Tray.TrayActions(()=>true,()=>activates++,()=>{},()=>{},()=>{},()=>{}),Console.WriteLine);
    for(var i=0;i<50&&!tray.IsAvailable;i++) { await Task.Delay(100); }
    if(!tray.IsAvailable) { throw new InvalidOperationException("SNI registration failed"); }
    var c=DBusConnection.Session;
    var query=new ProcessStartInfo("busctl") { RedirectStandardOutput=true,RedirectStandardError=true };
    foreach(var arg in new[]{"--user","--json=short","call",Service,"/StatusNotifierItem","org.freedesktop.DBus.Properties","GetAll","s","org.kde.StatusNotifierItem"}) { query.ArgumentList.Add(arg); }
    using(var check=Process.Start(query)!)
    {
      var output=await check.StandardOutput.ReadToEndAsync();var error=await check.StandardError.ReadToEndAsync();await check.WaitForExitAsync();
      using var json=System.Text.Json.JsonDocument.Parse(output);
      if(check.ExitCode!=0 || !output.Contains("IconPixmap") || !output.Contains("ToolTip")) { throw new InvalidOperationException("GetAll failed: "+error); }
      Console.WriteLine("SNI GetAll: busctl parsed properties and composite pixmap/tooltip values");
    }
    var pix=await c.CallMethodAsync(Call(c,"Get","IconPixmap"),static (m,_)=>m.GetBodyReader().ReadVariantValue(),null);
    Console.WriteLine($"SNI IconPixmap: {pix.Count} images, {pix.GetItem(0).GetItem(0).GetInt32()}x{pix.GetItem(0).GetItem(1).GetInt32()}, {pix.GetItem(0).GetItem(2).Count} bytes");
    var itemMenu=await c.CallMethodAsync(Call(c,"Get","ItemIsMenu"),static (m,_)=>m.GetBodyReader().ReadVariantValue().GetBool(),null);
    Console.WriteLine("SNI ItemIsMenu: "+itemMenu);
    await c.CallMethodAsync(Call(c,"ContextMenu"));await Task.Delay(250);
    var menu=tray.MenuWindow;
    if(!menu.IsVisible) { throw new InvalidOperationException("ContextMenu did not open product menu"); }
    Console.WriteLine($"SNI ContextMenu: visible={menu.IsVisible}, position={menu.Position}, size={menu.ClientSize}");Capture(menu,"menu-dark");Capture(panel,"panel-number");
    app.RequestedThemeVariant=ThemeVariant.Light;await Task.Delay(200);Capture(menu,"menu-light");
    if(!SniTest)
    {
    menu.Hide();await Task.Delay(100);
    var bridgeQuery=new ProcessStartInfo("busctl") { RedirectStandardOutput=true,RedirectStandardError=true };
    foreach(var arg in new[]{"--user","--json=short","call", $"org.x.StatusIcon.elitesip_{Environment.ProcessId}","/org/x/StatusIcon","org.freedesktop.DBus.ObjectManager","GetManagedObjects"}) { bridgeQuery.ArgumentList.Add(arg); }
    string? bridgePath=null;
    using(var inspect=Process.Start(bridgeQuery)!)
    {
        var output=await inspect.StandardOutput.ReadToEndAsync();await inspect.WaitForExitAsync();
        using var json=System.Text.Json.JsonDocument.Parse(output);
        foreach(var entry in json.RootElement.GetProperty("data")[0].EnumerateObject())
        {
            var values=entry.Value.GetProperty("org.x.StatusIcon");
            if(values.GetProperty("TooltipText").GetProperty("data").GetString()?.Contains("701")==true) { if(bridgePath is not null) { throw new InvalidOperationException("Ambiguous test tray icon"); }bridgePath=entry.Name; }
        }
    }
    if(bridgePath is null) { throw new InvalidOperationException("Test SNI icon not exported by Cinnamon bridge"); }
    MessageBuffer BridgeButton(string member)
    {
        using var w=c.GetMessageWriter();w.WriteMethodCallHeader( $"org.x.StatusIcon.elitesip_{Environment.ProcessId}",bridgePath,"org.x.StatusIcon",member,"iiuui",MessageFlags.None);
        w.WriteInt32(1820);w.WriteInt32(1060);w.WriteUInt32(3);w.WriteUInt32(0);w.WriteInt32(0);return w.CreateMessage();
    }
    await c.CallMethodAsync(BridgeButton("ButtonPress"));await c.CallMethodAsync(BridgeButton("ButtonRelease"));await Task.Delay(250);
    if(!menu.IsVisible) { throw new InvalidOperationException("Cinnamon XApp right-click did not open product menu"); }
    Console.WriteLine("Cinnamon XApp ButtonPress/Release(3): product menu opened directly, bypassing GTK bridge");
    }
    await c.CallMethodAsync(Call(c,"Activate"));await Task.Delay(100);
    if(activates!=1||menu.IsVisible) { throw new InvalidOperationException("Activate did not toggle panel / close menu"); }
    Console.WriteLine("SNI Activate: panel toggled once, menu hidden");
    model.IsOfflineByChoice=true;await Task.Delay(100);
    var red=await c.CallMethodAsync(Call(c,"Get","IconPixmap"),static (m,_)=>m.GetBodyReader().ReadVariantValue(),null);
    var bytes=red.GetItem(0).GetItem(2).GetArray<byte>();var colored=false;
    for(var i=0;i<bytes.Length;i+=4) { if(bytes[i]>0 && bytes[i+1]>bytes[i+2]) { colored=true;break; } }
    if(!colored) { throw new InvalidOperationException("Offline tray did not turn red"); }
    Console.WriteLine("SNI offline: red pixmap exported");
    panel.Close();
   } catch(Exception e) { Console.WriteLine(e);Environment.ExitCode=1; }
   finally { stop.Cancel(); }
  });
  Dispatcher.UIThread.MainLoop(stop.Token);
 }
}

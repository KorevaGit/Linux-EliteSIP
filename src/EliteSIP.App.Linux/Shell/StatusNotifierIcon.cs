using Tmds.DBus.Protocol;

namespace EliteSIP.App.Shell;

/// <summary>Desktop icon transport: direct XApp on Cinnamon, SNI elsewhere; menus remain product-owned.</summary>
internal sealed partial class StatusNotifierIcon : IPathMethodHandler, IDisposable
{
    private const string Interface = "org.kde.StatusNotifierItem";
    private const string Watcher = "org.kde.StatusNotifierWatcher";
    private const string ItemPath = "/StatusNotifierItem";
    private readonly Action _activate;
    private readonly Action<int, int> _menu;
    private readonly Action<string> _log;
    private readonly DBusConnection _connection = new(DBusAddress.Session!);
    private IDisposable? _watch;
    private volatile bool _connected;
    private volatile bool _registered;
    internal bool IsRegistered => _registered;
    internal event Action? AvailabilityChanged;
    private void Registered(bool value) { if (_registered != value) { _registered=value; AvailabilityChanged?.Invoke(); } }
    private volatile bool _disposed;
    private volatile Snapshot _snapshot = new("EliteSIP", 0, []);
    private sealed record Snapshot(string Title, int Size, byte[] Pixels);
    internal string ServiceName => _useXApp ? _xappService : $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    public string Path => ItemPath;
    public bool HandlesChildPaths => false;

    internal StatusNotifierIcon(Action activate, Action<int,int> menu, Action<string> log)
    { _activate=activate; _menu=menu; _log=log; }

    internal async Task StartAsync()
    {
        try
        {
            await _connection.ConnectAsync();
            if (_disposed) { return; }
            _connection.AddMethodHandler(this);
            var names=await _connection.ListServicesAsync();
            _useXApp=names.Any(name=>name.StartsWith(MonitorPrefix,StringComparison.Ordinal)) ||
                (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")?.Contains("Cinnamon",StringComparison.OrdinalIgnoreCase) is true);
            if(_useXApp)
            {
                UpdateXAppImage(_snapshot);
                _connection.AddMethodHandler(new XAppHandler(this,true));_connection.AddMethodHandler(new XAppHandler(this,false));
            }
            _watch=await _connection.AddMatchAsync(new MatchRule
            { Type=MessageType.Signal, Sender="org.freedesktop.DBus", Interface="org.freedesktop.DBus", Member="NameOwnerChanged" },
                static (message, _) => { var r=message.GetBodyReader();var name=r.ReadString();_ = r.ReadString();return (Name:name,Owner:r.ReadString()); },
                change =>
                {
                    if(!change.HasValue||_disposed) { return; }
                    if(_useXApp&&change.Value.Name.StartsWith(MonitorPrefix,StringComparison.Ordinal)) { _=RefreshMonitorAsync(); }
                    else if(!_useXApp&&change.Value.Name==Watcher) { Registered(false);if(change.Value.Owner.Length>0) { _=RegisterAsync(); } }
                });
            if (_disposed) { _watch.Dispose();return; }
            await _connection.RequestNameAsync(ServiceName);_connected=true;
            if(_useXApp) { await RefreshMonitorAsync(); } else { await RegisterAsync(); }
            await _connection.DisconnectedAsync();
            if (!_disposed) { Registered(false);_log("трей: соединение с D-Bus закрыто"); }
        }
        catch (Exception error) when (error is DBusErrorReplyException or IOException or InvalidOperationException or System.Net.Sockets.SocketException or ObjectDisposedException or UnauthorizedAccessException)
        { if (!_disposed) { Registered(false);_log("трей: " + error.Message); } }
    }

    private async Task RegisterAsync()
    {
        if (_disposed) { return; }
        try
        {
            await _connection.CallMethodAsync(RegisterMessage());
            if (!_disposed) { Registered(true); }
        }
        catch (Exception error) when (error is DBusErrorReplyException or IOException or InvalidOperationException or ObjectDisposedException)
        { if (!_disposed) { _log("регистрация трея: " + error.Message); } }
    }
    private MessageBuffer RegisterMessage()
    {
        using var w=_connection.GetMessageWriter();
        w.WriteMethodCallHeader(Watcher,"/StatusNotifierWatcher",Watcher,"RegisterStatusNotifierItem","s",MessageFlags.None);
        w.WriteString(ServiceName);return w.CreateMessage();
    }
    internal void Update(string title, int size, byte[] pixels)
    {
        _snapshot=new(title,size,pixels);
        if (!_connected || _disposed) { return; }
        if(_useXApp) { UpdateXAppImage(_snapshot);SignalXAppProperties(); }
        else { Signal("NewIcon");Signal("NewToolTip");Signal("NewTitle"); }
    }
    private void Signal(string name)
    {
        using var w=_connection.GetMessageWriter();
        w.WriteSignalHeader(null,ItemPath,Interface,name,null);
        _connection.TrySendMessage(w.CreateMessage());
    }
    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var message=context.Request;
        var iface=message.InterfaceAsString;var member=message.MemberAsString;
        if (iface==Interface && member is "Activate" or "SecondaryActivate" or "ContextMenu" or "Scroll")
        {
            var r=message.GetBodyReader();
            if(member=="ContextMenu") { var x=r.ReadInt32();var y=r.ReadInt32();_menu(x,y); }
            else if(member is "Activate" or "SecondaryActivate") { _activate(); }
            using var reply=context.CreateReplyWriter(null);context.Reply(reply.CreateMessage());return ValueTask.CompletedTask;
        }
        if(iface=="org.freedesktop.DBus.Properties" && member is "Get" or "GetAll")
        {
            var r=message.GetBodyReader();var target=r.ReadString();
            if(target!=Interface) { context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface",target);return ValueTask.CompletedTask; }
            var reply=context.CreateReplyWriter(member=="Get" ? "v" : "a{sv}");
            if(member=="Get")
            {
                var key=r.ReadString();
                if(!PropertyNames.Contains(key)) { reply.Dispose();context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty",key);return ValueTask.CompletedTask; }
                WriteProperty(ref reply,key,_snapshot);
            }
            else
            {
                var dict=reply.WriteDictionaryStart();var snapshot=_snapshot;
                foreach(var key in PropertyNames) { reply.WriteDictionaryEntryStart();reply.WriteString(key);WriteProperty(ref reply,key,snapshot); }
                reply.WriteDictionaryEnd(dict);
            }
            context.Reply(reply.CreateMessage());reply.Dispose();return ValueTask.CompletedTask;
        }
        if(iface=="org.freedesktop.DBus.Introspectable" && member=="Introspect")
        {
            using var reply=context.CreateReplyWriter("s");reply.WriteString(Introspection);context.Reply(reply.CreateMessage());return ValueTask.CompletedTask;
        }
        if(iface=="org.freedesktop.DBus.Peer" && member=="Ping")
        { using var reply=context.CreateReplyWriter(null);context.Reply(reply.CreateMessage());return ValueTask.CompletedTask; }
        context.ReplyUnknownMethodError();return ValueTask.CompletedTask;
    }
    private static readonly string[] PropertyNames = ["Category","Id","Title","Status","WindowId","ItemIsMenu","Menu","IconThemePath","IconName","IconPixmap","OverlayIconName","OverlayIconPixmap","AttentionIconName","AttentionIconPixmap","AttentionMovieName","ToolTip"];
    private static void WriteProperty(ref MessageWriter w,string key,Snapshot snapshot)
    {
        switch(key)
        {
            case "Category": w.WriteVariantString("Communications");break;
            case "Id": w.WriteVariantString("elitesip");break;
            case "Title": w.WriteVariantString("EliteSIP");break;
            case "Status": w.WriteVariantString("Active");break;
            case "WindowId": w.WriteVariantInt32(0);break;
            case "ItemIsMenu": w.WriteVariantBool(false);break;
            // No DBusMenu: hosts dispatch ContextMenu(x,y) to our themed window.
            case "Menu": w.WriteVariantObjectPath("/");break;
            case "IconPixmap": w.WriteSignature("a(iiay)");WritePixmaps(ref w,snapshot);break;
            case "OverlayIconPixmap": case "AttentionIconPixmap": w.WriteSignature("a(iiay)");WritePixmaps(ref w,null);break;
            case "ToolTip":
                w.WriteSignature("(sa(iiay)ss)");w.WriteStructureStart();w.WriteString("");WritePixmaps(ref w,null);w.WriteString("EliteSIP");w.WriteString(snapshot.Title);break;
            default: w.WriteVariantString("");break;
        }
    }
    private static void WritePixmaps(ref MessageWriter w,Snapshot? snapshot)
    {
        var array=w.WriteArrayStart(DBusType.Struct);
        if(snapshot is { Size: >0 }) { w.WriteStructureStart();w.WriteInt32(snapshot.Size);w.WriteInt32(snapshot.Size);w.WriteArray(snapshot.Pixels); }
        w.WriteArrayEnd(array);
    }
    private const string Introspection = """
    <node><interface name="org.kde.StatusNotifierItem">
    <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
    <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
    <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
    <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
    <property name="Category" type="s" access="read"/><property name="Id" type="s" access="read"/>
    <property name="Title" type="s" access="read"/><property name="Status" type="s" access="read"/>
    <property name="WindowId" type="i" access="read"/><property name="ItemIsMenu" type="b" access="read"/>
    <property name="Menu" type="o" access="read"/><property name="IconThemePath" type="s" access="read"/>
    <property name="IconName" type="s" access="read"/><property name="IconPixmap" type="a(iiay)" access="read"/>
    <property name="OverlayIconName" type="s" access="read"/><property name="OverlayIconPixmap" type="a(iiay)" access="read"/>
    <property name="AttentionIconName" type="s" access="read"/><property name="AttentionIconPixmap" type="a(iiay)" access="read"/>
    <property name="AttentionMovieName" type="s" access="read"/><property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
    <signal name="NewIcon"/><signal name="NewToolTip"/><signal name="NewTitle"/>
    </interface><interface name="org.freedesktop.DBus.Properties"><method name="Get"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method><method name="GetAll"><arg type="s" direction="in"/><arg type="a{sv}" direction="out"/></method></interface></node>
    """;
    public void Dispose() { _disposed=true;Registered(false);_watch?.Dispose();_connection.Dispose();
        if(_imageDirectory is not null) { try { Directory.Delete(_imageDirectory,true); } catch(IOException) { } catch(UnauthorizedAccessException) { } } }
}

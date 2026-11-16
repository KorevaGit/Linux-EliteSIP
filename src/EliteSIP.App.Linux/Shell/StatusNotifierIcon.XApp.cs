using Tmds.DBus.Protocol;

namespace EliteSIP.App.Shell;

// Cinnamon owns GTK menus for bridged SNI icons; direct XApp preserves product rendering.
internal sealed partial class StatusNotifierIcon
{
    private const string XAppInterface="org.x.StatusIcon";
    private const string XAppRoot="/org/x/StatusIcon";
    private const string XAppPath=XAppRoot+"/Icon";
    private const string MonitorPrefix="org.x.StatusIconMonitor.";
    private readonly string _xappService=$"org.x.StatusIcon.elitesip_{Environment.ProcessId}";
    private bool _useXApp;
    private string? _imageDirectory;
    private string _imagePath="";
    private int _imageGeneration;
    private byte[]? _lastImage;
    private volatile bool _menuOpen;
    private int _iconSize=24;
    internal bool UsesDesktopMonitor => _useXApp;
    internal void SetMenuOpen(bool value)
    { _menuOpen=value;if(_useXApp&&_connected&&!_disposed) { SignalXAppProperties(); } }
    private async Task RefreshMonitorAsync()
    {
        if(_disposed) { return; }
        try
        {
            var names=await _connection.ListServicesAsync();
            if(!_disposed) { Registered(_connected && names.Any(name=>name.StartsWith(MonitorPrefix,StringComparison.Ordinal))); }
        }
        catch(Exception error) when(error is IOException or InvalidOperationException or ObjectDisposedException or DBusErrorReplyException)
        { if(!_disposed) { Registered(false);_log("монитор трея: "+error.Message); } }
    }
    private void UpdateXAppImage(Snapshot snapshot)
    {
        if(snapshot.Size<=0 || ReferenceEquals(_lastImage,snapshot.Pixels)) { return; }
        _lastImage=snapshot.Pixels;
        _imageDirectory??=Directory.CreateTempSubdirectory("elitesip-tray-").FullName;
        File.SetUnixFileMode(_imageDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var path=System.IO.Path.Combine(_imageDirectory,$"icon-{++_imageGeneration%2}.png");
        using var image=new SkiaSharp.SKBitmap(snapshot.Size,snapshot.Size);
        for(var y=0;y<snapshot.Size;y++) for(var x=0;x<snapshot.Size;x++)
        { var at=(y*snapshot.Size+x)*4;image.SetPixel(x,y,new SkiaSharp.SKColor(snapshot.Pixels[at+1],snapshot.Pixels[at+2],snapshot.Pixels[at+3],snapshot.Pixels[at])); }
        using(var stream=File.Create(path)) { using var png=image.Encode(SkiaSharp.SKEncodedImageFormat.Png,100);png.SaveTo(stream); }
        File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);_imagePath=path;
    }
    private static readonly string[] XAppProperties=["Name","IconName","TooltipText","Label","Visible","IconSize","PrimaryMenuIsOpen","SecondaryMenuIsOpen","Metadata"];
    private void WriteXAppProperty(ref MessageWriter w,string key)
    {
        switch(key)
        {
            case "Name":w.WriteVariantString("elitesip");break;
            case "IconName":w.WriteVariantString(_imagePath);break;
            case "TooltipText":w.WriteVariantString(_snapshot.Title);break;
            case "Label":w.WriteVariantString("");break;
            case "Visible":w.WriteVariantBool(true);break;
            case "IconSize":w.WriteVariantInt32(_iconSize);break;
            case "PrimaryMenuIsOpen":w.WriteVariantBool(false);break;
            case "SecondaryMenuIsOpen":w.WriteVariantBool(_menuOpen);break;
            case "Metadata":w.WriteVariantString("{\"highlight-both-menus\":false}");break;
        }
    }
    private void WriteXAppProperties(ref MessageWriter w)
    {
        var dictionary=w.WriteDictionaryStart();
        foreach(var key in XAppProperties) { w.WriteDictionaryEntryStart();w.WriteString(key);WriteXAppProperty(ref w,key); }
        w.WriteDictionaryEnd(dictionary);
    }
    private void SignalXAppProperties()
    {
        var w=_connection.GetMessageWriter();
        try
        {
            w.WriteSignalHeader(null,XAppPath,"org.freedesktop.DBus.Properties","PropertiesChanged","sa{sv}as");
            w.WriteString(XAppInterface);WriteXAppProperties(ref w);w.WriteArray(Array.Empty<string>());
            _connection.TrySendMessage(w.CreateMessage());
        }
        finally { w.Dispose(); }
    }
    private ValueTask HandleXAppAsync(MethodContext context,bool root)
    {
        var iface=context.Request.InterfaceAsString;var member=context.Request.MemberAsString;
        if(root&&iface=="org.freedesktop.DBus.ObjectManager"&&member=="GetManagedObjects")
        {
            var w=context.CreateReplyWriter("a{oa{sa{sv}}}");
            try
            {
                var objects=w.WriteDictionaryStart();w.WriteDictionaryEntryStart();w.WriteObjectPath(XAppPath);
                var interfaces=w.WriteDictionaryStart();w.WriteDictionaryEntryStart();w.WriteString(XAppInterface);WriteXAppProperties(ref w);
                w.WriteDictionaryEnd(interfaces);w.WriteDictionaryEnd(objects);context.Reply(w.CreateMessage());
            }
            finally { w.Dispose(); }
            return ValueTask.CompletedTask;
        }
        if(!root&&iface==XAppInterface&&member is "ButtonPress" or "ButtonRelease" or "Scroll")
        {
            if(member!="Scroll")
            {
                var r=context.Request.GetBodyReader();var x=r.ReadInt32();var y=r.ReadInt32();var button=r.ReadUInt32();
                if(member=="ButtonPress"&&button==3) { _menu(x,y); }
                if(member=="ButtonRelease"&&button==1) { _activate(); }
            }
            using var w=context.CreateReplyWriter(null);context.Reply(w.CreateMessage());return ValueTask.CompletedTask;
        }
        if(!root&&iface=="org.freedesktop.DBus.Properties"&&member is "Get" or "GetAll" or "Set")
        {
            var r=context.Request.GetBodyReader();var target=r.ReadString();
            if(target!=XAppInterface) { context.ReplyError("org.freedesktop.DBus.Error.UnknownInterface",target);return ValueTask.CompletedTask; }
            var key=member=="GetAll"?"":r.ReadString();
            if(member!="GetAll"&&!XAppProperties.Contains(key)) { context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty",key);return ValueTask.CompletedTask; }
            if(member=="Set")
            {
                if(key!="IconSize") { context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly",key);return ValueTask.CompletedTask; }
                _iconSize=Math.Clamp(r.ReadVariantValue().GetInt32(),8,512);using var empty=context.CreateReplyWriter(null);context.Reply(empty.CreateMessage());return ValueTask.CompletedTask;
            }
            var w=context.CreateReplyWriter(member=="Get"?"v":"a{sv}");
            try { if(member=="Get") { WriteXAppProperty(ref w,key); } else { WriteXAppProperties(ref w); }context.Reply(w.CreateMessage()); }
            finally { w.Dispose(); }
            return ValueTask.CompletedTask;
        }
        if(iface=="org.freedesktop.DBus.Introspectable"&&member=="Introspect")
        {
            using var w=context.CreateReplyWriter("s");w.WriteString(root?XAppManagerXml:XAppXml);context.Reply(w.CreateMessage());return ValueTask.CompletedTask;
        }
        if(iface=="org.freedesktop.DBus.Peer"&&member=="Ping") { using var w=context.CreateReplyWriter(null);context.Reply(w.CreateMessage());return ValueTask.CompletedTask; }
        context.ReplyUnknownMethodError();return ValueTask.CompletedTask;
    }
    private sealed class XAppHandler(StatusNotifierIcon owner,bool root) : IPathMethodHandler
    {
        public string Path=>root?XAppRoot:XAppPath;
        public bool HandlesChildPaths=>false;
        public ValueTask HandleMethodAsync(MethodContext context)=>owner.HandleXAppAsync(context,root);
    }
    private const string XAppManagerXml="""
    <node><interface name="org.freedesktop.DBus.ObjectManager"><method name="GetManagedObjects"><arg direction="out" type="a{oa{sa{sv}}}"/></method><signal name="InterfacesAdded"><arg type="o"/><arg type="a{sa{sv}}"/></signal><signal name="InterfacesRemoved"><arg type="o"/><arg type="as"/></signal></interface><node name="Icon"/></node>
    """;
    private const string XAppXml="""
    <node><interface name="org.x.StatusIcon"><method name="ButtonPress"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="u" direction="in"/><arg type="u" direction="in"/><arg type="i" direction="in"/></method><method name="ButtonRelease"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="u" direction="in"/><arg type="u" direction="in"/><arg type="i" direction="in"/></method><method name="Scroll"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="u" direction="in"/></method><property name="Name" type="s" access="read"/><property name="IconName" type="s" access="read"/><property name="TooltipText" type="s" access="read"/><property name="Label" type="s" access="read"/><property name="Visible" type="b" access="read"/><property name="IconSize" type="i" access="readwrite"/><property name="PrimaryMenuIsOpen" type="b" access="read"/><property name="SecondaryMenuIsOpen" type="b" access="read"/><property name="Metadata" type="s" access="read"/></interface></node>
    """;
}

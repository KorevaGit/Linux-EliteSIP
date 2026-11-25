using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using EliteSIP.App.Resources;
namespace EliteSIP.App.Theme;

/// <summary>Plain text notes from the verified manifest, with their own bounded scroll area.</summary>
internal sealed class UpdateOffer : Window
{
    internal UpdateOffer(Version installed, Version available, string notes)
    {
        Title = Strings.Get("UpdateOfferInstall"); Width=520; MaxHeight=600;
        SizeToContent=SizeToContent.Height; CanResize=false; ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel { Spacing=12, Margin=new Thickness(16) };
        body.Children.Add(new BrandMark { Width=36, Height=36, Foreground=Avalonia.Media.Brush.Parse("#D8C6A5"), HorizontalAlignment=HorizontalAlignment.Left });
        body.Children.Add(new TextBlock { Text=$"EliteSIP {installed.ToString(3)} → {available.ToString(3)}", FontSize=20, FontWeight=Avalonia.Media.FontWeight.SemiBold });
        var text = new TextBlock { Text=string.IsNullOrWhiteSpace(notes) ? Strings.Get("UpdateNoNotes") : notes,
            TextWrapping=Avalonia.Media.TextWrapping.Wrap, LineHeight=21 };
        body.Children.Add(new ScrollViewer { Content=text, MaxHeight=340, HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var buttons=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8, HorizontalAlignment=HorizontalAlignment.Right };
        var later=new Button { Content=Strings.Get("UpdateLater"), Padding=new Thickness(12,8) };
        var install=new Button { IsDefault=true, Content=Strings.Get("UpdateOfferInstall"), Padding=new Thickness(12,8) };
        KeyDown+=(_,eventArgs)=> { if(eventArgs.Key==Avalonia.Input.Key.Escape) { Close(); } };
        later.Click+=(_,_)=>Close(); install.Click+=(_,_)=> { Accepted=true; Close(); };
        later.Classes.Add("settings");install.Classes.Add("settings");
        buttons.Children.Add(later);buttons.Children.Add(install);body.Children.Add(buttons); Content=body;
    }
    internal bool Accepted { get; private set; }
    internal bool Ask(Window? owner)
    {
        using var closed=new CancellationTokenSource();
        Closed+=(_,_)=>closed.Cancel();
        if (owner is { IsVisible: true })
        {
            Show(owner);
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Show();
        }
        Dispatcher.UIThread.MainLoop(closed.Token);
        return Accepted;
    }
}

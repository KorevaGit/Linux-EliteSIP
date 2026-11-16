using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
namespace EliteSIP.App.Theme;
internal sealed class StartupSplash : Window
{
    internal StartupSplash()
    {
        Width=360; Height=210; CanResize=false; ShowInTaskbar=false; ShowActivated=false;
        WindowDecorations=WindowDecorations.None; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var content=new StackPanel { Spacing=14, VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center };
        content.Children.Add(new BrandMark { Width=80, Height=80, Foreground=Brush.Parse("#D8C6A5") });
        content.Children.Add(new TextBlock { Text="EliteSIP", FontSize=26, FontWeight=FontWeight.SemiBold, HorizontalAlignment=HorizontalAlignment.Center });
        Content=new Border { CornerRadius=new CornerRadius(12), Child=content };
    }
}

using Avalonia.Controls;

namespace EliteSIP.App.Incoming;

/// <summary>Окно входящего. Показ, место и защиту ведёт <see cref="IncomingCallPresenter"/>.</summary>
public partial class IncomingCallWindow : Window
{
    public IncomingCallWindow()
    {
        InitializeComponent();
    }

    public IncomingCallWindow(IncomingCallViewModel model)
        : this()
    {
        DataContext = model;
    }
}

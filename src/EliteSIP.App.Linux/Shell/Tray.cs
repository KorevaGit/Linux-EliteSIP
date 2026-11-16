using System.ComponentModel;
using Avalonia.Media;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Shell;

/// <summary>
/// Значок в области уведомлений — Linux-половина <c>Shell/TrayIcon</c>.
/// </summary>
///
/// <remarks>Own SNI transport dispatches right-clicks to a themed Avalonia window.</remarks>
internal sealed class Tray : IDisposable
{
    private readonly PanelViewModel _model;
    private readonly TrayActions _actions;
    private readonly StatusNotifierIcon _icon;
    private readonly TrayMenuWindow _menu;
    private byte[] _pixels = [];
    private bool? _online;
    private bool _disposed;

    internal Tray(PanelViewModel model, TrayActions actions, Action<string>? log = null)
    {
        _model=model;_actions=actions;_menu=new TrayMenuWindow(model,actions);
        _icon=new StatusNotifierIcon(
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if(!_disposed) { _menu.Hide();_actions.TogglePanel(); } }),
            (x,y) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if(!_disposed) { _menu.Refresh(StatusTitle);_menu.OpenAt(x,y); } }),
            log ?? (_ => { }));
        _menu.PropertyChanged += (_,e) => { if(e.Property==Avalonia.Controls.Control.IsVisibleProperty) { _icon.SetMenuOpen(_menu.IsVisible); } };
        _icon.AvailabilityChanged += () => AvailabilityChanged?.Invoke();
        _model.PropertyChanged+=OnModelChanged;Refresh();_ = _icon.StartAsync();
    }
    internal TrayMenuWindow MenuWindow => _menu;
    internal bool UsesDesktopMonitor => _icon.UsesDesktopMonitor;
    internal bool IsAvailable => _icon.IsRegistered;
    internal event Action? AvailabilityChanged;
    internal sealed record TrayActions(Func<bool> IsPanelVisible,Action TogglePanel,Action ShowHistory,Action ShowSettings,Action ToggleOffline,Action Quit);
    internal void Refresh()
    {
        var online=!_model.IsOfflineByChoice && _model.Registration==RegistrationState.Registered;
        if(_online!=online)
        {
            _online=online;
            using var source=SkiaSharp.SKBitmap.Decode(Theme.BrandMark.TrayPng(online?Brushes.Lime:Brushes.Red));
            _pixels=new byte[64*64*4];
            for(var y=0;y<64;y++) for(var x=0;x<64;x++)
            {
                var color=source.GetPixel(x,y);var at=(y*64+x)*4;
                _pixels[at]=color.Alpha;_pixels[at+1]=color.Red;_pixels[at+2]=color.Green;_pixels[at+3]=color.Blue;
            }
        }
        _icon.Update(StatusTitle,64,_pixels);
        if(_menu.IsVisible) { _menu.Refresh(StatusTitle); }
    }
    public void Dispose()
    {
        if(_disposed) { return; }_disposed=true;
        _model.PropertyChanged-=OnModelChanged;_icon.Dispose();_menu.ClosePermanently();
    }

    private string StatusTitle
    {
        get
        {
            if (_model.IsOfflineByChoice)
            {
                return Strings.Get("TrayOffline");
            }

            var state = Strings.Get(_model.Registration switch
            {
                RegistrationState.Registered => "TrayRegistered",
                RegistrationState.Registering => "TrayRegistering",
                RegistrationState.Failed => "TrayFailed",
                _ => "TrayIdle",
            });

            return string.IsNullOrEmpty(_model.StatusTitle) || _model.StatusTitle == "—"
                ? state
                : state + " · " + _model.StatusTitle;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(PanelViewModel.Registration)
            or nameof(PanelViewModel.IsOfflineByChoice)
            or nameof(PanelViewModel.StatusTitle)
            or nameof(PanelViewModel.IsInCall))
        {
            Refresh();
        }
    }

}

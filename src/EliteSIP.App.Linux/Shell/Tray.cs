using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EliteSIP.App.Panel;
using EliteSIP.App.Resources;

namespace EliteSIP.App.Shell;

/// <summary>
/// Значок в области уведомлений — Linux-половина <c>Shell/TrayIcon</c>.
/// </summary>
///
/// <remarks>
/// <para>
/// Меню и подсказка те же, что на Windows: состояние словами первой строкой,
/// «Показать/Скрыть панель», история, настройки, «Не беспокоить», выход. Значок
/// — корона продукта с цветной точкой состояния снизу, нарисованная тем же
/// путём и в тех же долях.
/// </para>
/// <para>
/// Сам значок — StatusNotifierItem через <see cref="TrayIcon"/> Avalonia; его
/// перерегистрацию при перезапуске наблюдателя Avalonia делает сама (W-L4).
/// Есть ли вообще куда его ставить, говорит <c>TrayAvailability</c>, и по нему
/// приложение решает, прятать панель при закрытии или сворачивать.
/// </para>
/// <para>
/// Меню пересобирается на каждую смену состояния, а не при открытии: меню
/// StatusNotifierItem живёт на стороне панели (DBusMenu), и вопроса «сейчас
/// откроюсь» от неё ждать нельзя.
/// </para>
/// </remarks>
internal sealed class Tray : IDisposable
{
    /// <summary>Корона — тот же путь, что у Windows-значка.</summary>
    private const string Crown =
        "M1.6 16.2V3.4c0-.5.6-.75.95-.4l3.7 3.6L9.3 1.5c.32-.5 1.06-.5 1.38 0l3.07 5.1 "
        + "3.7-3.6c.36-.35.95-.1.95.4v12.8z";

    private readonly PanelViewModel _model;
    private readonly TrayActions _actions;
    private readonly TrayIcon _icon;
    private string? _drawnKey;

    internal Tray(Application application, PanelViewModel model, TrayActions actions)
    {
        _model = model;
        _actions = actions;
        _icon = new TrayIcon { IsVisible = true };
        _icon.Clicked += (_, _) => _actions.TogglePanel();

        TrayIcon.SetIcons(application, [_icon]);

        _model.PropertyChanged += OnModelChanged;
        Refresh();
    }

    internal sealed record TrayActions(
        Func<bool> IsPanelVisible,
        Action TogglePanel,
        Action ShowHistory,
        Action ShowSettings,
        Action ToggleOffline,
        Action Quit);

    /// <summary>Панель спрятали или показали — пункт меню меняет подпись.</summary>
    internal void Refresh()
    {
        _icon.ToolTipText = StatusTitle;
        _icon.Menu = BuildMenu();

        var key = DotKey();
        if (key != _drawnKey)
        {
            _drawnKey = key;
            _icon.Icon = Draw(Application.Current?.FindResource(key) as IBrush ?? Brushes.Gray);
        }
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
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

    private NativeMenu BuildMenu()
    {
        NativeMenu menu = [];
        menu.Items.Add(new NativeMenuItem(StatusTitle) { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());

        Add(menu, Strings.Get(_actions.IsPanelVisible() ? "TrayHidePanel" : "TrayShowPanel"), _actions.TogglePanel);
        Add(menu, Strings.Get("TrayHistory"), _actions.ShowHistory);
        Add(menu, Strings.Get("TraySettings"), _actions.ShowSettings);
        menu.Items.Add(new NativeMenuItemSeparator());

        var offline = Add(menu, Strings.Get("TrayDoNotDisturb"), _actions.ToggleOffline);
        offline.ToggleType = MenuItemToggleType.CheckBox;
        offline.IsChecked = _model.IsOfflineByChoice;

        // В разговоре недоступно: уход с линии снимает регистрацию.
        offline.IsEnabled = !_model.IsInCall;

        menu.Items.Add(new NativeMenuItemSeparator());
        Add(menu, Strings.Get("TrayQuit"), _actions.Quit);
        return menu;
    }

    private static NativeMenuItem Add(NativeMenu menu, string title, Action action)
    {
        NativeMenuItem item = new(title);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
        return item;
    }

    private string DotKey()
    {
        if (_model.IsOfflineByChoice)
        {
            return "StatusOfflineBrush";
        }

        if (_model.IsInCall)
        {
            return "StatusInCallBrush";
        }

        return _model.Registration switch
        {
            RegistrationState.Registered => "StatusRegisteredBrush",
            RegistrationState.Registering => "StatusConnectingBrush",
            RegistrationState.Failed => "StatusFailureBrush",
            _ => "StatusOfflineBrush",
        };
    }

    /// <summary>
    /// Корона и точка на 32 × 32 — доли те же, что у Windows: корона 15/18
    /// ширины и 11/18 высоты, точка 5/18.
    /// </summary>
    ///
    /// <remarks>
    /// Корона белая: панель Cinnamon в Mint по умолчанию тёмная, а узнать её
    /// цвет у StatusNotifierHost нечем. На светлой панели белую корону
    /// выручает точка состояния — она цветная на любой.
    /// </remarks>
    private static WindowIcon Draw(IBrush dot)
    {
        const int size = 32;
        const double crownWidth = size * 15.0 / 18.0;
        const double crownHeight = size * 11.0 / 18.0;
        const double dotDiameter = size * 5.0 / 18.0;

        var crown = Geometry.Parse(Crown);
        var bounds = crown.Bounds;
        var scale = Math.Min(crownWidth / bounds.Width, crownHeight / bounds.Height);

        using RenderTargetBitmap bitmap = new(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            using (context.PushTransform(
                       Matrix.CreateTranslation(-bounds.X, -bounds.Y)
                       * Matrix.CreateScale(scale, scale)
                       * Matrix.CreateTranslation((size - (bounds.Width * scale)) / 2, 0)))
            {
                context.DrawGeometry(Brushes.White, null, crown);
            }

            context.DrawEllipse(dot, null, new Point(size / 2.0, size - (dotDiameter / 2)), dotDiameter / 2, dotDiameter / 2);
        }

        using MemoryStream png = new();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        png.Position = 0;
        return new WindowIcon(png);
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace EliteSIP.App.Panel;

/// <summary>
/// Панель софтфона — перевод <c>Panel/PanelWindow.xaml.cs</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// <para>
/// Поведение то же, и почему оно такое, сказано там. Коротко:
/// </para>
/// <list type="bullet">
/// <item>место и размер, выбранные оператором, переживают перезапуск — но
/// только если окно в них ещё помещается на экран;</item>
/// <item>окно, растущее по содержимому (перевод, вторая линия), растёт вверх:
/// нижняя кромка остаётся там, где её поставили над CRM;</item>
/// <item>высота не больше рабочей области экрана: не влезающие макросы
/// ужимаются (<see cref="FitMacroHeight"/>) до 30 точек;</item>
/// <item>закрытие при живом значке прячет панель, а не выходит.</item>
/// </list>
/// <para>
/// Чего нет: увеличения содержимого вслед за шириной. В Windows-версии предел
/// увеличения стоит 1,0 — то есть оно выключено, — и переносить выключенное
/// незачем.
/// </para>
/// </remarks>
public partial class PanelWindow : Window
{
    /// <summary>Нижний предел клавиши макроса при ужатии под экран — тот же, что у Windows.</summary>
    internal const double MinimumMacroHeight = 30;

    private readonly DispatcherTimer _clock;

    /// <summary>Нижняя кромка в пикселях экрана — то, что держится при росте окна.</summary>
    private int _bottomEdge;

    private bool _opened;

    public PanelWindow()
        : this(new PanelViewModel())
    {
    }

    public PanelWindow(PanelViewModel model)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;

        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Model.Tick());

        Model.PropertyChanged += OnModelChanged;
        Model.Macros.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Relayout, DispatcherPriority.Loaded);

        StatusRow.SizeChanged += (_, change) => ProfilePill.MaxWidth = Math.Max(120, change.NewSize.Width * 0.6);
        UpdateRows();
    }

    public PanelViewModel Model { get; }

    public Func<(double Left, double Top)?>? RestorePlacement { get; init; }

    public Action<double, double>? SavePlacement { get; init; }

    public Func<(double Width, double Height)?>? RestoreSize { get; init; }

    public Action<double, double>? SaveSize { get; init; }

    /// <summary>
    /// Можно ли закрыть окно по-настоящему — только на выходе из приложения.
    /// Иначе закрытие прячет или сворачивает панель (<see cref="HidesToTray"/>).
    /// </summary>
    public bool AllowsClosing { get; set; }

    /// <summary>
    /// Прятать ли панель в значок при закрытии. Нет значка (GNOME без
    /// AppIndicator) — панель сворачивается: невидимый софтфон без выхода
    /// хуже свёрнутого. Пока наличие значка не выяснено — сворачивать.
    /// </summary>
    public bool HidesToTray { get; set; }

    /// <summary>Сколько ужать клавиши макросов, чтобы панель влезла; <c>null</c> — не нужно.</summary>
    ///
    /// <remarks>Та же формула, что у Windows: лишнее делится на ряды поровну.</remarks>
    internal static double? FitMacroHeight(double content, double room, int rows, double macroHeight)
    {
        if (content <= room || rows <= 0)
        {
            return null;
        }

        return Math.Max(MinimumMacroHeight, macroHeight - Math.Ceiling((content - room) / rows));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (!_opened)
        {
            _opened = true;

            if (RestoreSize?.Invoke() is { } size && size.Width >= MinWidth)
            {
                SizeToContent = SizeToContent.Manual;
                Width = size.Width;
                Height = Math.Max(size.Height, MinHeight);
            }

            Restore();
            _clock.Start();
        }

        Relayout();
        KeepOnScreen();
        _bottomEdge = Position.Y + PixelHeight();
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        if (!_opened || WindowState is not WindowState.Normal)
        {
            return;
        }

        if (e.Reason is WindowResizeReason.User)
        {
            // Оператор тянет край — окно больше не по содержимому, и размер
            // его, а не наш. На Windows это делал WM_ENTERSIZEMOVE.
            SizeToContent = SizeToContent.Manual;
            SaveSize?.Invoke(Width, Height);
            _bottomEdge = Position.Y + PixelHeight();
            return;
        }

        if (SizeToContent is SizeToContent.Manual)
        {
            _bottomEdge = Position.Y + PixelHeight();
            return;
        }

        // Выросло по содержимому — растём вверх.
        if (_bottomEdge > 0)
        {
            Position = new PixelPoint(Position.X, _bottomEdge - PixelHeight());
            KeepOnScreen();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (!AllowsClosing)
        {
            e.Cancel = true;
            SaveCurrentPlacement();

            if (HidesToTray)
            {
                Hide();
            }
            else
            {
                WindowState = WindowState.Minimized;
            }

            return;
        }

        SaveCurrentPlacement();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        base.OnClosed(e);
    }

    /// <summary>Запомнить место — по движению окна, а не на выходе: выход бывает и аварийным.</summary>
    internal void SaveCurrentPlacement()
    {
        if (WindowState is WindowState.Normal && _opened)
        {
            var scale = DesktopScaling;
            SavePlacement?.Invoke(Position.X / scale, Position.Y / scale);
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(PanelViewModel.IsTransferEntryVisible)
            or nameof(PanelViewModel.HasMacros)
            or nameof(PanelViewModel.ShowsMacros)
            or nameof(PanelViewModel.MacroColumns)
            or nameof(PanelViewModel.MacroHeight)
            or nameof(PanelViewModel.IsInCall)
            or nameof(PanelViewModel.Trouble))
        {
            Dispatcher.UIThread.Post(Relayout, DispatcherPriority.Loaded);
        }
    }

    private void Relayout()
    {
        UpdateRows();
        UpdateMinimumHeight();
    }

    /// <summary>
    /// Высоты строк середины — то, что WPF-разметка делала триггерами на
    /// <c>RowDefinition</c>.
    /// </summary>
    ///
    /// <remarks>
    /// С макросами клавиши управления берут сотую долю и не больше 102 точек, а
    /// остальное — сетке макросов; без макросов клавишам отдаётся всё.
    /// </remarks>
    private void UpdateRows()
    {
        var macros = Model.HasMacros && Model.ShowsMacros;
        var keys = Middle.RowDefinitions[2];
        var grid = Middle.RowDefinitions[4];

        keys.Height = macros ? new GridLength(100, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        keys.MaxHeight = macros ? 102 : double.PositiveInfinity;
        grid.Height = macros ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        grid.MinHeight = macros ? Model.MacroGridMinHeight : 0;

        Macros.IsVisible = macros;
        MacroGap.IsVisible = Model.IsTransferEntryVisible || Model.HasMacros;
    }

    /// <summary>
    /// Нижний предел высоты — содержимое на нынешней ширине, но не больше экрана.
    /// </summary>
    private void UpdateMinimumHeight()
    {
        var width = Frame.Bounds.Width > 0 ? Frame.Bounds.Width : Width;
        if (width <= 0 || double.IsNaN(width))
        {
            return;
        }

        Model.MacroHeightLimit = double.PositiveInfinity;
        var content = MeasureContent(width);
        var room = WorkAreaHeight();

        if (FitMacroHeight(content, room, MacroRows(), Model.MacroHeight) is { } limit)
        {
            Model.MacroHeightLimit = limit;
            content = MeasureContent(width);
        }

        MinHeight = Math.Ceiling(Math.Min(content, room));
        MaxHeight = Math.Max(MinHeight, room);
    }

    private double MeasureContent(double width)
    {
        Frame.Measure(new Size(width, double.PositiveInfinity));
        return Frame.DesiredSize.Height;
    }

    private int MacroRows()
    {
        if (!Model.HasMacros || !Model.ShowsMacros)
        {
            return 0;
        }

        var columns = Math.Max(1, Model.MacroColumns);
        return (Model.Macros.Count + columns - 1) / columns;
    }

    /// <summary>Рабочая область экрана окна в точках; без экрана — без предела.</summary>
    ///
    /// <remarks>
    /// Рамку окна рисует оконный менеджер, и её высоты клиент не знает до
    /// показа; FrameSize появляется после первого показа. До него берётся
    /// поправка в 40 точек — обычная полоса заголовка Muffin.
    /// </remarks>
    private double WorkAreaHeight()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return double.PositiveInfinity;
        }

        var chrome = FrameSize is { } frame ? frame.Height - ClientSize.Height : 40;
        return (screen.WorkingArea.Height / screen.Scaling) - Math.Max(0, chrome);
    }

    private int PixelHeight() => (int)Math.Round((FrameSize?.Height ?? Bounds.Height) * DesktopScaling);

    private void KeepOnScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var work = screen.WorkingArea;
        var height = PixelHeight();
        var y = Position.Y;

        if (y + height > work.Bottom)
        {
            y = Math.Max(work.Y, work.Bottom - height);
        }

        if (y < work.Y)
        {
            y = work.Y;
        }

        if (y != Position.Y)
        {
            Position = new PixelPoint(Position.X, y);
        }

        _bottomEdge = Position.Y + height;
    }

    private void Restore()
    {
        if (RestorePlacement?.Invoke() is not { } placement)
        {
            return;
        }

        // Место — только если окно туда целиком помещается: монитор, на
        // котором его оставили, могли отключить.
        var scale = DesktopScaling;
        var wanted = new PixelRect(
            (int)Math.Round(placement.Left * scale),
            (int)Math.Round(placement.Top * scale),
            (int)Math.Round(Width * scale),
            Math.Max(1, PixelHeight()));

        if (!Screens.All.Any(screen => screen.Bounds.Contains(wanted)))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = wanted.Position;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SaveCurrentPlacement();
    }

    private void OnDialedNumberKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter)
        {
            return;
        }

        if (Model.CallOrHangUp?.CanExecute(null) is true)
        {
            Model.CallOrHangUp.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDialedNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: 1 } text)
        {
            Model.KeyPressed?.Invoke(text[0]);
        }
    }

    private void OnClearNumberClick(object? sender, RoutedEventArgs e) => Model.DialedNumber = string.Empty;

    private void OnProfileClick(object? sender, RoutedEventArgs e)
    {
        FillProfiles();
        ProfileMenu.Open(ProfilePill);
    }

    /// <summary>
    /// Профили сотрудника — в начало меню капсулы, по одному на номер; один
    /// профиль — прежняя строка «номер · подпись».
    /// </summary>
    private void FillProfiles()
    {
        foreach (var stale in ProfileMenu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "profile")).ToList())
        {
            ProfileMenu.Items.Remove(stale);
        }

        var choices = Model.ReadProfiles?.Invoke() ?? [];
        var many = choices.Count > 1;
        SingleProfile.IsVisible = !many;

        if (!many)
        {
            return;
        }

        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            MenuItem item = new()
            {
                Header = choice.Title,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = choice.IsActive,
                Tag = "profile",
            };

            item.Click += (_, _) =>
            {
                if (!choice.IsActive)
                {
                    Model.SwitchProfile?.Invoke(choice.ProfileId);
                }
            };

            ProfileMenu.Items.Insert(i, item);
        }
    }

    private void OnTransferClick(object? sender, RoutedEventArgs e)
    {
        Model.ShowTransferEntry();
        Dispatcher.UIThread.Post(() => TransferField.Focus(), DispatcherPriority.Loaded);
    }

    private void OnCancelTransferClick(object? sender, RoutedEventArgs e) => Model.CancelTransferEntry();
}

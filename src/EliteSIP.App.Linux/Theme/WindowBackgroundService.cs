using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EliteSIP.App.Settings;
using SkiaSharp;

namespace EliteSIP.App.Theme;

/// <summary>One opaque background and one neutral palette per settings generation; resize does no image work.</summary>
internal sealed class WindowBackgroundService : IDisposable
{
    internal static WindowBackgroundService? Current { get; private set; }
    private readonly Application _app;
    private readonly AppearanceSettings _settings;
    private readonly Func<bool> _dark;
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly List<string> _overrides = [];
    private SKBitmap? _source;
    private Bitmap? _image;
    private string? _sourceKey;
    private string? _renderKey;
    private string? _paletteKey;
    private Color _dominant;
    private bool _disposed;
    internal int ImageBuildCount { get; private set; }
    internal int PaletteBuildCount { get; private set; }
    internal Bitmap? Image => _image;
    internal WindowBackgroundService(Application app, AppearanceSettings settings, Func<bool> dark)
    {
        _app = app; _settings = settings; _dark = dark; Current = this;
        _settings.PropertyChanged += Changed;
        _delay.Tick += (_, _) => { _delay.Stop(); Apply(); };
        Apply();
    }
    private void Changed(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(AppearanceSettings.Language)) { return; }
        _delay.Stop(); _delay.Start();
    }
    internal void Apply()
    {
        if (_disposed) { return; }
        _delay.Stop();
        var dark = _dark();
        var path = _settings.BackgroundImagePath;
        var file = File.Exists(path) ? new FileInfo(path) : null;
        var sourceKey = file is not null ? $"{file.FullName}:{file.LastWriteTimeUtc.Ticks}:{file.Length}" : string.Empty;
        if (_sourceKey != sourceKey)
        {
            _source?.Dispose(); _source = null; _sourceKey = sourceKey;
            if (sourceKey.Length > 0)
            {
                try { _source = BackgroundImages.Read(path); _dominant = Sample(_source); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { _source = null; }
            }
        }
        var renderKey = $"{sourceKey}:{_source is not null}:{dark}:{_settings.BackgroundScale}:{_settings.BackgroundBlur}:{_settings.BackgroundBrightness}";
        var paletteKey = $"{sourceKey}:{_source is not null}:{dark}:{_settings.Theme == Appearance.Wallpaper}:{_settings.BackgroundTransparency}";
        if (_renderKey != renderKey)
        {
            _renderKey = renderKey;
            var previous = _image; _image = _source is null ? null : Compose(_source, dark);
            ImageBuildCount++;
            if (_image is not null) { Put("WindowBackgroundBrush", new ImageBrush(_image) { Stretch = Stretch.UniformToFill }); }
            else { _app.Resources.Remove("WindowBackgroundBrush"); }
            previous?.Dispose();
        }
        if (_paletteKey == paletteKey) { return; }
        _paletteKey = paletteKey; PaletteBuildCount++;
        foreach (var key in _overrides) { _app.Resources.Remove(key); } _overrides.Clear();
        if (_source is null) { return; }
        var wallpaper = _settings.Theme == Appearance.Wallpaper;
        var alpha = _settings.BackgroundTransparency switch { BackgroundTransparency.Light => .96, BackgroundTransparency.Medium => .90, BackgroundTransparency.Strong => .82, _ => 1 };
        // Tint the whole neutral palette, preserving semantic status/call colors,
        // the gradient geometry and the original alpha of borders/hover layers.
        foreach (var key in new[] { "SurfaceBrush", "ControlBackgroundBrush", "ControlHoverBrush", "ControlPressedBrush", "MacroBrush", "MacroHoverBrush", "CallFieldBrush", "ProfileBrush", "ProfileHoverBrush", "SelectedBrush", "HistoryFilterBackgroundBrush", "HistoryFilterHoverBrush", "NavHoverBrush", "FieldBrush", "SettingsTrackBrush", "SettingsLineBrush", "ControlBorderBrush", "SelectionBrush", "AccentBrush", "ThemeAccentBrush" })
        {
            if (!_app.TryFindResource(key, _app.ActualThemeVariant, out var value)) { continue; }
            var surface = key is not ("FieldBrush" or "SettingsTrackBrush" or "SettingsLineBrush" or "ControlBorderBrush" or "HistoryFilterHoverBrush" or "NavHoverBrush" or "SelectionBrush" or "AccentBrush" or "ThemeAccentBrush");
            Color Tint(Color color)
            {
                var mixed = wallpaper ? WallpaperColor(color, _dominant, dark) : color;
                if (wallpaper && key is "AccentBrush" or "ThemeAccentBrush")
                {
                    // Windows keeps the focus accent bright against tinted dark surfaces.
                    mixed = Color.FromRgb((byte)(140+_dominant.R*.4),(byte)(140+_dominant.G*.4),(byte)(140+_dominant.B*.4));
                }
                if (surface && alpha < 1) { mixed = Mix(mixed, _dominant, dark ? .12 : .06); }
                return Color.FromArgb((byte)Math.Round(color.A * (surface ? alpha : 1)), mixed.R, mixed.G, mixed.B);
            }
            IBrush? brush = value switch
            {
                ILinearGradientBrush gradient => new LinearGradientBrush
                {
                    StartPoint = gradient.StartPoint, EndPoint = gradient.EndPoint,
                    GradientStops = [ .. gradient.GradientStops.Select(stop => new GradientStop(Tint(stop.Color), stop.Offset)) ],
                },
                ISolidColorBrush solid => new SolidColorBrush(Tint(solid.Color)),
                _ => null,
            };
            if (brush is not null) { Override(key, brush); }
        }
        Override("TextPrimaryBrush", new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#161616")));
        Override("TextSecondaryBrush", new SolidColorBrush(Color.Parse(dark ? "#E1E1E1" : "#414141")));
        Override("TextTertiaryBrush", new SolidColorBrush(Color.Parse(dark ? "#C3C3C3" : "#575757")));
    }
    private void Override(string key, IBrush value) { Put(key, value); _overrides.Add(key); }
    internal static Color WallpaperColor(Color color, Color hue, bool dark)
    {
        if (!dark) { return Mix(color, hue, .15); }
        // Same channel-normalized neutral tint as Windows Wallpaper mode.
        var level = Math.Clamp((color.R + color.G + color.B) / 3d, 28, 90);
        var max = Math.Max(1, (int)Math.Max(hue.R, Math.Max(hue.G, hue.B)));
        byte Channel(byte value) => (byte)Math.Clamp(level * (.35 + 1.1 * value / max), 0, 150);
        return Color.FromArgb(color.A, Channel(hue.R), Channel(hue.G), Channel(hue.B));
    }
    private void Put(string key, IBrush value) => _app.Resources[key] = value;
    private WriteableBitmap Compose(SKBitmap source, bool dark)
    {
        using var output = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(output);
        canvas.Clear(dark ? new SKColor(37,40,44) : new SKColor(232,231,228));
        var brightness = _settings.BackgroundBrightness / 100f;
        using var color = SKColorFilter.CreateColorMatrix([brightness,0,0,0,0, 0,brightness,0,0,0, 0,0,brightness,0,0, 0,0,0,1,0]);
        using var blur = SKImageFilter.CreateBlur(_settings.BackgroundBlur, _settings.BackgroundBlur);
        using var paint = new SKPaint { IsAntialias = true, ColorFilter = color, ImageFilter = blur };
        var scale = _settings.BackgroundScale / 100f;
        var dx = source.Width * (1-scale)/2; var dy = source.Height * (1-scale)/2;
        canvas.DrawBitmap(source, new SKRect(dx,dy,source.Width-dx,source.Height-dy), paint);
        canvas.DrawColor(dark ? new SKColor(24,27,31,145) : new SKColor(247,246,243,165), SKBlendMode.SrcOver);
        // Copy pixels directly: PNG encoding + decoding here blocked the UI on
        // every slider adjustment and temporarily duplicated the image buffers.
        var bitmap = new WriteableBitmap(new PixelSize(output.Width, output.Height), new Vector(96,96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        try
        {
            using var buffer = bitmap.Lock();
            using var pixels = output.PeekPixels();
            if (!pixels.ReadPixels(output.Info, buffer.Address, buffer.RowBytes))
            {
                throw new InvalidOperationException("Cannot copy wallpaper pixels");
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
    internal static Color Sample(SKBitmap source)
    {
        // Dominant quantized color, alpha-weighted. A mean washes out colorful
        // wallpaper into gray and lets transparent pixel RGB affect the palette.
        var groups = new Dictionary<int,(long R,long G,long B,long Weight)>();
        for(var y=0;y<source.Height;y+=Math.Max(1,source.Height/32))
        for(var x=0;x<source.Width;x+=Math.Max(1,source.Width/32))
        {
            var pixel=source.GetPixel(x,y);var alpha=pixel.Alpha;
            var key=(pixel.Red/32<<6)|(pixel.Green/32<<3)|pixel.Blue/32;
            groups.TryGetValue(key,out var group);
            groups[key]=(group.R+pixel.Red*alpha,group.G+pixel.Green*alpha,group.B+pixel.Blue*alpha,group.Weight+alpha);
        }
        var total = groups.Values.Sum(group => group.Weight);
        // White/gray regions often occupy the largest bucket in a colorful photo.
        // Prefer a meaningful colored region, but ignore tiny accents (<3%).
        var chromatic = groups.Values.Where(group =>
        {
            if (group.Weight == 0 || group.Weight < total * .03) { return false; }
            var r = (double)group.R / group.Weight;
            var g = (double)group.G / group.Weight;
            var b = (double)group.B / group.Weight;
            var max = Math.Max(r, Math.Max(g,b));
            var chroma = max - Math.Min(r, Math.Min(g,b));
            return max >= 32 && chroma >= 24 && chroma / max >= .15;
        }).OrderByDescending(group => group.Weight).ToArray();
        var dominant = chromatic.Length > 0 ? chromatic[0] : groups.Values.OrderByDescending(group=>group.Weight).FirstOrDefault();
        return dominant.Weight==0?Colors.Gray:Color.FromRgb((byte)(dominant.R/dominant.Weight),(byte)(dominant.G/dominant.Weight),(byte)(dominant.B/dominant.Weight));
    }
    private static Color Mix(Color a,Color b,double t) => Color.FromRgb((byte)(a.R+(b.R-a.R)*t),(byte)(a.G+(b.G-a.G)*t),(byte)(a.B+(b.B-a.B)*t));
    public void Dispose()
    {
        if (_disposed) { return; } _disposed=true;
        _settings.PropertyChanged-=Changed; _delay.Stop();
        foreach(var key in _overrides) { _app.Resources.Remove(key); }
        _app.Resources.Remove("WindowBackgroundBrush");
        _image?.Dispose(); _source?.Dispose();
        if (ReferenceEquals(Current,this)) { Current=null; }
    }
}

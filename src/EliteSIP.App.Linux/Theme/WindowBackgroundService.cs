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
        var hue = _settings.Theme == Appearance.Wallpaper ? _dominant : (dark ? Color.Parse("#30343A") : Color.Parse("#F3F2EF"));
        var amount = _settings.Theme == Appearance.Wallpaper ? 0.16 : 0;
        var alpha = _settings.BackgroundTransparency switch { BackgroundTransparency.Light => 246, BackgroundTransparency.Medium => 232, BackgroundTransparency.Strong => 218, _ => 255 };
        // Clone the palette's actual gradient. Wallpaper tint/opacity must not
        // flatten the raised surfaces into solid rectangles.
        foreach (var key in new[] { "SurfaceBrush", "ControlBackgroundBrush", "ControlHoverBrush", "ControlPressedBrush", "MacroBrush", "MacroHoverBrush", "CallFieldBrush", "ProfileBrush", "ProfileHoverBrush", "SelectedBrush" })
        {
            if (!_app.TryFindResource(key, _app.ActualThemeVariant, out var value)) { continue; }
            Color Tint(Color color) { var mixed = Mix(color, hue, amount); return Color.FromArgb((byte)alpha, mixed.R, mixed.G, mixed.B); }
            IBrush? brush = value switch
            {
                LinearGradientBrush gradient => new LinearGradientBrush
                {
                    StartPoint = gradient.StartPoint, EndPoint = gradient.EndPoint,
                    GradientStops = [ .. gradient.GradientStops.Select(stop => new GradientStop(Tint(stop.Color), stop.Offset)) ],
                },
                ISolidColorBrush solid => new SolidColorBrush(Tint(solid.Color)),
                _ => null,
            };
            if (brush is not null) { Put(key, brush); _overrides.Add(key); }
        }
    }
    private void Put(string key, IBrush value) => _app.Resources[key] = value;
    private Bitmap Compose(SKBitmap source, bool dark)
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
        using var data = output.Encode(SKEncodedImageFormat.Png,100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }
    private static Color Sample(SKBitmap source)
    {
        long red=0,green=0,blue=0; var count=0;
        for(var y=0;y<source.Height;y+=Math.Max(1,source.Height/32))
        for(var x=0;x<source.Width;x+=Math.Max(1,source.Width/32))
        { var pixel=source.GetPixel(x,y); red+=pixel.Red;green+=pixel.Green;blue+=pixel.Blue;count++; }
        return Color.FromRgb((byte)(red/count),(byte)(green/count),(byte)(blue/count));
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

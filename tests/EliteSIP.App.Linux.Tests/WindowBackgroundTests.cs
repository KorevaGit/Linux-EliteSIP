using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using SkiaSharp;

namespace EliteSIP.App.Tests;

public sealed class WindowBackgroundTests
{
    private static void Image(string path, int width=160, int height=100)
    {
        using var image=new SKBitmap(width,height);using var canvas=new SKCanvas(image);
        canvas.Clear(new SKColor(75,120,145));
        using var brush=new SKPaint { Color=new SKColor(200,145,80) };canvas.DrawCircle(width/2,height/2,height/3,brush);
        using var data=image.Encode(SKEncodedImageFormat.Png,100);using var file=File.Create(path);data.SaveTo(file);
    }
    [Fact]
    public void Import_is_transactional_and_decouples_the_stored_image_from_its_source()
    {
        var dir=Directory.CreateTempSubdirectory("elitesip-wallpaper-");
        try
        {
            var source=Path.Combine(dir.FullName,"source.png");var stored=Path.Combine(dir.FullName,"stored.png");Image(source);
            BackgroundImages.Import(source,stored);var before=File.ReadAllBytes(stored);File.Delete(source);
            using(var read=BackgroundImages.Read(stored)) { Assert.Equal(160,read.Width); }
            File.WriteAllText(source,"corrupt");Assert.Throws<InvalidDataException>(()=>BackgroundImages.Import(source,stored));
            Assert.Equal(before,File.ReadAllBytes(stored));Assert.False(File.Exists(stored+".tmp"));
        }
        finally { dir.Delete(true); }
    }
    [Fact]
    public void Large_image_is_downsampled_before_becoming_the_persistent_background()
    {
        var dir=Directory.CreateTempSubdirectory("elitesip-wallpaper-size-");
        try
        {
            var source=Path.Combine(dir.FullName,"source.png");Image(source,3200,2000);
            using var read=BackgroundImages.Read(source);Assert.InRange(Math.Max(read.Width,read.Height),1,1600);
        }
        finally { dir.Delete(true); }
    }
    [Fact]
    public void Compressed_large_png_is_rejected_before_allocating_an_unbounded_decode_buffer()
    {
        using TemporaryHome home=new();var path=Path.Combine(home.Path,"large.png");
        using(var pixels=new SKBitmap(5000,4000))
        {
            pixels.Erase(SKColors.White);using var data=pixels.Encode(SKEncodedImageFormat.Png,100);
            using var file=File.Create(path);data.SaveTo(file);
        }
        Assert.True(new FileInfo(path).Length<30*1024*1024);
        var error=Assert.Throws<InvalidDataException>(()=>BackgroundImages.Read(path));
        Assert.Contains("64 MB",error.Message,StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resize_reuses_the_opaque_bitmap_and_adjustments_do_not_rebuild_palette(bool dark)
    {
        var dir=Directory.CreateTempSubdirectory("elitesip-wallpaper-cache-");
        var app=Application.Current!;var oldTheme=app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
            var path=Path.Combine(dir.FullName,"stored.png");Image(path);
            var settings=new AppearanceSettings { BackgroundImagePath=path, Theme=Appearance.Wallpaper, BackgroundTransparency=BackgroundTransparency.Light };
            using var service=new WindowBackgroundService(app,settings,()=>dark);
            Assert.True(app.TryFindResource("ControlBackgroundBrush", app.ActualThemeVariant, out var raised));
            var gradient = Assert.IsAssignableFrom<Avalonia.Media.ILinearGradientBrush>(raised);
            Assert.True(gradient.GradientStops.Count >= 2);
            Assert.NotEqual(gradient.GradientStops[0].Color, gradient.GradientStops[^1].Color);
            var image=service.Image;Assert.NotNull(image);
            using(var encoded=new MemoryStream())
            {
                image.Save(encoded,Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                using var composed=SKBitmap.Decode(encoded.ToArray());
                Assert.Equal(255,composed.GetPixel(0,0).Alpha);
                Assert.NotEqual(composed.GetPixel(0,0),composed.GetPixel(composed.Width/2,composed.Height/2));
            }var palette=service.PaletteBuildCount;var builds=service.ImageBuildCount;
            var window=new Window { Width=640,Height=480,Content=new TextBlock { Text="EliteSIP" } };window.Show();
            var watch=System.Diagnostics.Stopwatch.StartNew();var cpu=System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            try
            {
                for(var i=0;i<40;i++) { window.Width=640+i*4;window.Height=480+i*3;Dispatcher.UIThread.RunJobs();using var frame=window.CaptureRenderedFrame(); }
                Assert.Same(image,service.Image);Assert.Equal(builds,service.ImageBuildCount);Assert.Equal(palette,service.PaletteBuildCount);
                Snapshot.Save(window.CaptureRenderedFrame()!,"background-"+(dark?"dark":"light"));
            }
            finally { window.Close(); }
            File.WriteAllText(Path.Combine(Snapshot.Directory,"background-performance-"+(dark?"dark":"light")+".json"),System.Text.Json.JsonSerializer.Serialize(new { frames=40,elapsed_ms=watch.Elapsed.TotalMilliseconds,cpu_ms=(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime-cpu).TotalMilliseconds,image_builds=service.ImageBuildCount-builds,palette_builds=service.PaletteBuildCount-palette }));
            settings.BackgroundBrightness=115;service.Apply();Assert.NotSame(image,service.Image);Assert.Equal(palette,service.PaletteBuildCount);
            settings.BackgroundBlur=12;service.Apply();Assert.Equal(palette,service.PaletteBuildCount);
            service.Apply();var count=service.ImageBuildCount;service.Apply();Assert.Equal(count,service.ImageBuildCount);
            settings.BackgroundImagePath=Path.Combine(dir.FullName,"missing.png");service.Apply();Assert.Null(service.Image);
        }
        finally { app.RequestedThemeVariant=oldTheme;dir.Delete(true); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Wallpaper_reaches_incoming_and_tints_fields_with_stronger_text(bool dark)
    {
        var dir=Directory.CreateTempSubdirectory("elitesip-wallpaper-regression-");
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        app.RequestedThemeVariant=dark?ThemeVariant.Dark:ThemeVariant.Light;
        try
        {
            var path=Path.Combine(dir.FullName,"photo.png");Image(path);
            var settings=new AppearanceSettings {BackgroundImagePath=path,Theme=Appearance.Wallpaper};
            using var service=new WindowBackgroundService(app,settings,()=>dark);
            var subject=EliteSIP.App.Incoming.IncomingCallSubject.Classify("712","Test",false,"701",true);
            var challenge=EliteSIP.CallGuard.CallGuardChallenge.Build(new EliteSIP.CallGuard.CallGuardPolicy().Normalized(),new Random(7));
            var window=new EliteSIP.App.Incoming.IncomingCallWindow(new(subject,challenge,false,_=>{},()=>{}));window.Show();Dispatcher.UIThread.RunJobs();
            try {Assert.IsType<Avalonia.Media.ImageBrush>(Assert.IsType<Border>(window.Content).Background);}
            finally {window.Close();}
            app.TryFindResource("FieldBrush",app.ActualThemeVariant,out var before);
            using(var red=new SKBitmap(32,32)) {red.Erase(new SKColor(220,40,40));using var encoded=red.Encode(SKEncodedImageFormat.Png,100);using var output=File.Create(Path.Combine(dir.FullName,"red.png"));encoded.SaveTo(output);}
            settings.BackgroundImagePath=Path.Combine(dir.FullName,"red.png");service.Apply();
            app.TryFindResource("FieldBrush",app.ActualThemeVariant,out var after);
            Assert.NotEqual(Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(before).Color,Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(after).Color);
            app.TryFindResource("TextSecondaryBrush",app.ActualThemeVariant,out var text);
            var channel=Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(text).Color.R;
            Assert.True(dark?channel>=225:channel<=65);
            app.TryFindResource("AccentBrush",app.ActualThemeVariant,out var accent);
            var focus=Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(accent).Color;
            Assert.True(focus.R>=140&&focus.G>=140&&focus.B>=140);
        }
        finally {app.RequestedThemeVariant=old;dir.Delete(true);}
    }

    [Fact]
    public void Wallpaper_uses_dominant_color_instead_of_averaging_different_colors()
    {
        using var pixels=new SKBitmap(100,100);using var canvas=new SKCanvas(pixels);
        canvas.Clear(new SKColor(30,100,180));using var accent=new SKPaint {Color=new SKColor(230,80,30)};
        canvas.DrawRect(70,0,30,100,accent);
        var dominant=WindowBackgroundService.Sample(pixels);
        Assert.Equal(Avalonia.Media.Color.FromRgb(30,100,180),dominant);
    }

    [Fact]
    public void Wallpaper_color_ignores_large_neutral_regions_but_not_grayscale_photos()
    {
        using var pixels=new SKBitmap(100,100);using var canvas=new SKCanvas(pixels);
        canvas.Clear(new SKColor(250,251,253));
        using var blue=new SKPaint {Color=new SKColor(109,202,253)};
        canvas.DrawRect(65,0,35,100,blue);
        var color=WindowBackgroundService.Sample(pixels);
        Assert.Equal(Avalonia.Media.Color.FromRgb(109,202,253),color);
        canvas.Clear(new SKColor(190,190,190));canvas.DrawRect(99,0,1,100,blue);
        Assert.Equal(Avalonia.Media.Color.FromRgb(190,190,190),WindowBackgroundService.Sample(pixels));
    }

    [AvaloniaFact]
    public void Wallpaper_color_reaches_rendered_settings_cards_and_changes_live()
    {
        using TemporaryHome home=new();
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        var settings=AppSettings.Load(home.SettingsPath);
        settings.Appearance.Theme=Appearance.Wallpaper;
        var path=Path.Combine(home.Path,"blue-white.png");
        using(var pixels=new SKBitmap(100,100))
        {
            using var canvas=new SKCanvas(pixels);canvas.Clear(new SKColor(250,251,253));
            using var blue=new SKPaint {Color=new SKColor(109,202,253)};canvas.DrawRect(65,0,35,100,blue);
            using var png=pixels.Encode(SKEncodedImageFormat.Png,100);using var output=File.Create(path);png.SaveTo(output);
        }
        settings.Appearance.BackgroundImagePath=path;
        try
        {
            using var appearance=new AppearanceService(app,new EliteSIP.Platform.Linux.DesktopAppearance(_=>{})) {Appearance=Appearance.Wallpaper};
            using var background=new WindowBackgroundService(app,settings.Appearance,()=>appearance.IsDark);
            var audio=new TestAudioPlatform();
            var model=new SettingsViewModel(settings,new EliteSIP.App.Platform.AvaloniaUiThread(),audio,new TestAutoStart()) {Section=SettingsSectionKind.Appearance};
            var window=new SettingsWindow(model,appearance,audio.CreateSignalSounds(_=>{}));window.Show();Dispatcher.UIThread.RunJobs();
            try
            {
                var cards=Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Border>().Where(b=>b.Classes.Contains("card")).ToArray();
                Assert.NotEmpty(cards);
                foreach(var card in cards)
                {
                    var colors=card.Background switch
                    {
                        Avalonia.Media.ILinearGradientBrush gradient=>gradient.GradientStops.Select(stop=>stop.Color).ToArray(),
                        Avalonia.Media.ISolidColorBrush solid=>[solid.Color],
                        _=>throw new InvalidOperationException("Missing card brush"),
                    };
                    Assert.All(colors,color=>Assert.True(color.B>color.R+15));
                }
                Snapshot.Save(window.CaptureRenderedFrame()!,"wallpaper-blue-white-settings");
                settings.Appearance.Theme=Appearance.Dark;appearance.Appearance=Appearance.Dark;background.Apply();Dispatcher.UIThread.RunJobs();
                var neutral=Assert.IsAssignableFrom<Avalonia.Media.ILinearGradientBrush>(cards[0].Background).GradientStops[0].Color;
                Assert.True(Math.Abs(neutral.R-neutral.B)<10);
            }
            finally {window.Close();}
        }
        finally {app.RequestedThemeVariant=old;}
    }

    [AvaloniaFact]
    public void Wallpaper_mode_selects_the_dark_color_palette_even_on_a_light_desktop()
    {
        var app=Application.Current!;var old=app.RequestedThemeVariant;
        try
        {
            using var appearance=new AppearanceService(app,new EliteSIP.Platform.Linux.DesktopAppearance(_=>{})) {Appearance=Appearance.Wallpaper};
            Assert.True(appearance.IsDark);Assert.Equal(ThemeVariant.Dark,app.RequestedThemeVariant);
        }
        finally {app.RequestedThemeVariant=old;}
    }

    [Fact]
    public void Automatic_choices_and_explicit_choices_are_independent_and_survive_restart()
    {
        var dir=Directory.CreateTempSubdirectory("elitesip-wallpaper-prefs-");
        try
        {
            var settings=new AppSettings();settings.Appearance.ApplyWallpaperDefaults();
            Assert.Equal(Appearance.Wallpaper,settings.Appearance.Theme);Assert.Equal(BackgroundTransparency.Light,settings.Appearance.BackgroundTransparency);
            settings.Appearance.Theme=Appearance.Dark;settings.Appearance.ThemeChosenByUser=true;
            settings.Appearance.ApplyWallpaperDefaults();Assert.Equal(Appearance.Dark,settings.Appearance.Theme);
            settings.Appearance.BackgroundTransparency=BackgroundTransparency.Off;settings.Appearance.TransparencyChosenByUser=true;
            var file=Path.Combine(dir.FullName,"settings.json");settings.Save(file);var restored=AppSettings.Load(file);
            restored.Appearance.ApplyWallpaperDefaults();Assert.Equal(Appearance.Dark,restored.Appearance.Theme);Assert.Equal(BackgroundTransparency.Off,restored.Appearance.BackgroundTransparency);
            restored.Appearance.BackgroundImagePath="test.png";restored.Appearance.ResetBackground();Assert.False(restored.Appearance.HasBackgroundImage);
        }
        finally { dir.Delete(true); }
    }
}

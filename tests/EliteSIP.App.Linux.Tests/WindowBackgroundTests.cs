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

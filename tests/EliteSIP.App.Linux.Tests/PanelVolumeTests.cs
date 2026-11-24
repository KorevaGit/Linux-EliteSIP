using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EliteSIP.App.Panel;
using EliteSIP.App.Platform;
using EliteSIP.App.Settings;
using EliteSIP.App.Theme;
using EliteSIP.Platform.Linux;

namespace EliteSIP.App.Tests;

/// <summary>
/// Громкость на панели в разговоре: те же значения, что в настройках (окно
/// настроек привязано к тем же <c>Settings.Audio</c>), и
/// значок микрофона показывает то же, что клавиша «Микрофон».
/// </summary>
public sealed class PanelVolumeTests
{
    [AvaloniaFact]
    public void Ползунки_панели_меняют_те_же_настройки_что_окно_настроек()
    {
        using var errors = new BindingErrors();
        var settings = new AppSettings();
        var muted = false;
        PanelViewModel model = null!;
        model = new PanelViewModel { Audio = settings.Audio, ToggleMicrophone = new RelayCommand(_ => { muted = !muted; model.IsMicrophoneMuted = muted; }) };
        var panel = new PanelWindow(model) { AllowsClosing = true };
        panel.Show();
        try
        {
            model.ActiveLine = new CallLineViewModel { Title = "Иванов Пётр", SecondaryNumber = "+7 ••• •••-••-67", ConnectedAt = DateTimeOffset.Now.AddMinutes(-2) };
            model.Lines.Add(model.ActiveLine);
            Dispatcher.UIThread.RunJobs();

            var peer = panel.FindControl<Slider>("PeerVolume")!;
            var mic = panel.FindControl<Slider>("MicrophoneVolume")!;

            peer.Value = 0.6;
            mic.Value = 1.25;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0.6, settings.Audio.PlaybackVolume, 3);
            Assert.Equal(1.25, settings.Audio.MicrophoneGain, 3);

            settings.Audio.PlaybackVolume = 1.5;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1.5, peer.Value, 3);

            // Значок микрофона на ползунке — то же отключение, что клавиша «Микрофон».
            model.CanControlMedia = true;
            Dispatcher.UIThread.RunJobs();
            panel.FindControl<Button>("MicrophoneVolumeIcon")!.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsMicrophoneMuted);

            Snapshot.Save(panel.CaptureRenderedFrame()!, "panel-volume");

            // «Собеседник» — звук в ноль, кнопка красная; повторно — прежняя громкость.
            var peerMute = panel.FindControl<ToggleButton>("PeerMuteKey")!;
            settings.Audio.PlaybackVolume = 0.6;
            peerMute.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, settings.Audio.PlaybackVolume, 3);
            Assert.True(peerMute.IsChecked);
            peerMute.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0.6, settings.Audio.PlaybackVolume, 3);
            Assert.False(peerMute.IsChecked);

            // Перевод — всплывающим окном: сетка макросов не прячется.
            Assert.Equal("TransferPopup", panel.FindControl<TextBox>("TransferField")!.FindLogicalAncestorOfType<Popup>()!.Name);

            // Метка 100 %: бегунок прилипает к ней, двойной щелчок — сразу 100 %.
            Assert.Contains("volume", peer.Classes);
            Assert.Equal(1, VolumeSlider.Stick(0.95));
            Assert.Equal(1, VolumeSlider.Stick(1.05));
            Assert.Equal(0.9, VolumeSlider.Stick(0.9));
            peer.Value = 1.6;
            peer.Value = VolumeSlider.Stick(1.03);
            Assert.Equal(1, settings.Audio.PlaybackVolume, 3);

            // Окно минимальной высоты: фреймы громкости не наезжают на поле номера.
            panel.SizeToContent = SizeToContent.Manual;
            panel.Height = panel.MinHeight;
            Dispatcher.UIThread.RunJobs();
            var field = panel.FindControl<Grid>("CallField")!;
            var peerFrame = panel.FindControl<Border>("PeerVolumeFrame")!;
            var micFrame = panel.FindControl<Border>("MicrophoneVolumeFrame")!;
            var frameTop = peerFrame.TranslatePoint(default, panel)!.Value.Y;
            var fieldBottom = field.TranslatePoint(new Point(0, field.Bounds.Height), panel)!.Value.Y;
            Assert.True(frameTop >= fieldBottom + 3.5, $"громкость {frameTop:0} при поле до {fieldBottom:0}");
            Assert.Equal(72, field.Bounds.Height, 0.5);
            Assert.Equal(72, peerFrame.Bounds.Height, 0.5);
            Assert.Equal(72, micFrame.Bounds.Height, 0.5);
            Assert.Equal(40, panel.FindControl<Grid>("KeysRow")!.Bounds.Height, 0.5);

            // Растягивание без макросов — поровну фреймам громкости; поле номера,
            // клавиши и «Завершить» постоянной высоты.
            panel.Height += 200;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(72, field.Bounds.Height, 0.5);
            Assert.Equal(40, panel.FindControl<Grid>("KeysRow")!.Bounds.Height, 0.5);
            Assert.True(peerFrame.Bounds.Height > 150, $"громкость {peerFrame.Bounds.Height:0}");
            Assert.Equal(peerFrame.Bounds.Height, micFrame.Bounds.Height, 0.5);
            Snapshot.Save(panel.CaptureRenderedFrame()!, "panel-volume-tall");
            // Ползунки можно убрать в оформлении: остаётся ряд клавиш с микрофоном.
            model.ShowsVolume = false;
            panel.Height = panel.MinHeight;
            Dispatcher.UIThread.RunJobs();
            Assert.False(peerFrame.IsVisible);
            Assert.False(micFrame.IsVisible);
            Assert.True(panel.FindControl<ToggleButton>("MicrophoneKey")!.IsVisible);
            panel.Height = panel.MinHeight;
            Dispatcher.UIThread.RunJobs();
            Snapshot.Save(panel.CaptureRenderedFrame()!, "panel-no-volume");
            model.ShowsVolume = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(peerFrame.IsVisible);

            // У панели и без того шумят привязки к пустым Trouble/Activity;
            // свои — громкость и меню «⋯» — обязаны быть чистыми.
            Assert.DoesNotContain(errors.Errors, error => error.Contains("Audio", StringComparison.Ordinal)
                || error.Contains("Microphone", StringComparison.Ordinal) || error.Contains("Conference", StringComparison.Ordinal) || error.Contains("Transfer", StringComparison.Ordinal));
        }
        finally
        {
            panel.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}

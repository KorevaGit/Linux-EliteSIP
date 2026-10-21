using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EliteSIP.App.Resources;
using EliteSIP.App.Theme;
using EliteSIP.Audio;

namespace EliteSIP.App.Settings;

/// <summary>
/// Настройки менеджера — перевод <c>Settings/SettingsWindow</c> Windows-версии.
/// </summary>
///
/// <remarks>
/// Всё содержание — общая <see cref="SettingsViewModel"/>; здесь то, что
/// трогает окно: выбор файла рингтона (<see cref="IStorageProvider"/> вместо
/// <c>OpenFileDialog</c>), прослушивание, вопрос о перезапуске при смене
/// языка, тема на лету.
/// </remarks>
public partial class SettingsWindow : Window
{
    private readonly ISignalSounds? _sounds;
    private readonly AppearanceService? _appearance;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    internal SettingsWindow(SettingsViewModel model, AppearanceService appearance, ISignalSounds sounds)
        : this()
    {
        _sounds = sounds;
        _appearance = appearance;
        Model = model;
        DataContext = model;

        model.Settings.Appearance.PropertyChanged += OnAppearanceChanged;
        Opened += (_, _) =>
        {
            model.UpdateMeterPolling(windowOpen: true);

            // «Системное по умолчанию» — пункт с Id = null. Avalonia на
            // SelectedValue = null снимает выбор, а не находит такой пункт,
            // и список стоял бы пустым. Выбор пункта обратно пишет null сам.
            SelectSystemDefault(InputDevices, model.Settings.Audio.InputDeviceId);
            SelectSystemDefault(OutputDevices, model.Settings.Audio.OutputDeviceId);
        };
    }

    public SettingsViewModel? Model { get; }

    /// <summary>«Открыть управление» внизу окна.</summary>
    public event Action? AdministrationRequested;

    /// <summary>Перезапуск после смены языка — его делает приложение, а не окно.</summary>
    internal Action? RestartRequested { get; set; }

    protected override void OnClosed(EventArgs e)
    {
        if (Model is not null)
        {
            Model.Settings.Appearance.PropertyChanged -= OnAppearanceChanged;
            Model.UpdateMeterPolling(windowOpen: false);
        }

        _sounds?.StopPreview();
        base.OnClosed(e);
    }

    private static void SelectSystemDefault(ComboBox list, string? chosen)
    {
        if (chosen is null && list.SelectedIndex < 0 && list.ItemCount > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(AppearanceSettings.Theme) && _appearance is not null && Model is not null)
        {
            _appearance.Appearance = Model.Settings.Appearance.Theme;
        }
    }

    private async void OnChooseRingtoneClick(object? sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        _sounds?.StopPreview();

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("SectionRingtone"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MP3, WAV, OGG") { Patterns = ["*.mp3", "*.wav", "*.ogg"] }],
        });

        if (files is not [{ } file] || file.TryGetLocalPath() is not { } path)
        {
            return;
        }

        Model.Settings.Ringtone.CustomSoundPath = path;
        Model.NotifyRingtoneName();
        ShowRingtoneProblem(null);
    }

    private void OnDefaultRingtoneClick(object? sender, RoutedEventArgs e)
    {
        if (Model is null)
        {
            return;
        }

        _sounds?.StopPreview();
        Model.Settings.Ringtone.CustomSoundPath = null;
        Model.NotifyRingtoneName();
        ShowRingtoneProblem(null);
    }

    private void OnPreviewRingtoneClick(object? sender, RoutedEventArgs e)
    {
        if (Model is null || _sounds is null)
        {
            return;
        }

        _sounds.StopPreview();

        var path = Model.Settings.Ringtone.CustomSoundPath;
        ShowRingtoneProblem(!string.IsNullOrEmpty(path) && !File.Exists(path) ? Model.RingtoneName : null);

        var ringtone = Model.Settings.Ringtone;
        var readable = _sounds.PreviewRingtone(
            path,
            ringtone.Volume,
            ringtone.Output is RingtoneOutput.CallDevice ? Model.Settings.Audio.OutputDeviceId : null);

        if (!readable)
        {
            ShowRingtoneProblem(Strings.Get("RingtoneUnreadable"));
        }
    }

    private void ShowRingtoneProblem(string? text)
    {
        RingtoneProblem.Text = text ?? string.Empty;
        RingtoneProblem.IsVisible = text is not null;
    }

    private void OnLanguageChosen(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded || Model is null || sender is not RadioButton { Tag: string name, IsChecked: true })
        {
            return;
        }

        var chosen = Enum.Parse<LanguageSetting>(name);
        if (chosen == Model.Settings.Appearance.Language)
        {
            return;
        }

        var answer = Dialog.Ask(
            this,
            Strings.Get("AppearanceLanguageQuestion"),
            Strings.Get("AppearanceLanguageQuestionBody"),
            confirmTitle: Strings.Get("AppearanceLanguageConfirm"));

        if (answer is not DialogAnswer.Confirm)
        {
            Model.NotifyLanguage();
            return;
        }

        Model.Settings.Appearance.Language = chosen;
        RestartRequested?.Invoke();
    }

    private void OnAdministrationClick(object? sender, RoutedEventArgs e) => AdministrationRequested?.Invoke();
}

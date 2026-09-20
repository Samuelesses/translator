using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameAudioTranslator.Models;
using GameAudioTranslator.Services;
using NAudio.Wave;

namespace GameAudioTranslator;

public partial class MainWindow : Window
{
    private static readonly Brush RecordingBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));

    private readonly ObservableCollection<CaptionLine> _captions = new();
    private readonly ObservableCollection<AudioDeviceOption> _devices = new();
    private readonly ObservableCollection<ProcessAudioSource> _processes = new();

    private readonly AudioCaptureService _deviceCaptureService = new();
    private readonly ProcessLoopbackCaptureService _processCaptureService = new();
    private readonly SpeechTranslationService _translationService = new();
    private readonly SegmentProcessor _segmentProcessor;
    private readonly MicRecorderService _micRecorder = new();
    private readonly ReplyVoiceService _replyVoice;
    private readonly AppSettings _settings = SettingsStore.Load();

    private OverlayWindow? _overlay;
    private HotkeyManager? _hotkeys;
    private string? _apiKey;
    private string? _lastSpokenLanguage;
    private bool _isCapturing;
    private bool _isRecordingReply;

    public MainWindow()
    {
        InitializeComponent();

        CaptionList.ItemsSource = _captions;
        DeviceCombo.ItemsSource = _devices;
        ProcessCombo.ItemsSource = _processes;
        _segmentProcessor = new SegmentProcessor(_translationService, OnCaptionReady, OnProcessingError);
        _replyVoice = new ReplyVoiceService(_translationService);

        _deviceCaptureService.SegmentReady += OnSegmentReady;
        _deviceCaptureService.StatusChanged += OnCaptureStatus;
        _processCaptureService.SegmentReady += OnSegmentReady;
        _processCaptureService.StatusChanged += OnCaptureStatus;

        LoadDevices();
        LoadProcesses();
        LoadSettingsIntoUi();

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotkeyManager();
        _hotkeys.Register(this);

        var failed = new List<string>();

        if (!_hotkeys.RegisterHotkey(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
                (uint)KeyInterop.VirtualKeyFromKey(Key.O), ToggleOverlayVisibility))
        {
            failed.Add("Ctrl+Alt+O (show/hide overlay)");
        }

        if (!_hotkeys.RegisterHotkey(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
                (uint)KeyInterop.VirtualKeyFromKey(Key.L), ToggleOverlayLock))
        {
            failed.Add("Ctrl+Alt+L (lock/unlock overlay)");
        }

        if (!_hotkeys.RegisterHotkey(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
                (uint)KeyInterop.VirtualKeyFromKey(Key.R), ToggleReplyRecording))
        {
            failed.Add("Ctrl+Alt+R (reply)");
        }

        if (failed.Count > 0)
        {
            StatusText.Text = $"Status: another app is already using {string.Join(", ", failed)} - that hotkey won't respond here until it's freed up";
        }
    }

    private void LoadDevices()
    {
        _devices.Clear();
        _devices.Add(new AudioDeviceOption("Default playback device", null));

        try
        {
            foreach (var device in AudioCaptureService.GetOutputDevices())
            {
                _devices.Add(new AudioDeviceOption(device.FriendlyName, device));
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: couldn't list audio devices ({ex.Message})";
        }

        DeviceCombo.SelectedItem = _devices.FirstOrDefault(d => d.Id == _settings.AudioDeviceId) ?? _devices[0];
    }

    private void LoadProcesses()
    {
        _processes.Clear();

        try
        {
            foreach (var process in ProcessAudioSourceProvider.GetCandidateProcesses())
            {
                _processes.Add(process);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: couldn't list running applications ({ex.Message})";
        }

        var match = _processes.FirstOrDefault(p =>
            string.Equals(p.ProcessName, _settings.TargetProcessName, StringComparison.OrdinalIgnoreCase));
        ProcessCombo.SelectedItem = match ?? _processes.FirstOrDefault();
    }

    private void LoadSettingsIntoUi()
    {
        SensitivitySlider.Value = _settings.SilenceThresholdRms;
        SpeechSpeedSlider.Value = _settings.ReplySpeechSpeed;
        _apiKey = SettingsStore.DecryptApiKey(_settings.EncryptedApiKey);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            ApiKeyBox.Password = _apiKey;
        }

        if (_settings.CaptureMode == "Application")
        {
            AppModeRadio.IsChecked = true;
        }
        else
        {
            DeviceModeRadio.IsChecked = true;
        }
    }

    private void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _apiKey = ApiKeyBox.Password;
        _settings.EncryptedApiKey = string.IsNullOrEmpty(_apiKey) ? null : SettingsStore.EncryptApiKey(_apiKey);
        SettingsStore.Save(_settings);
        StatusText.Text = "Status: API key saved";
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e) => LoadDevices();

    private void RefreshProcessesButton_Click(object sender, RoutedEventArgs e) => LoadProcesses();

    private void CaptureMode_Changed(object sender, RoutedEventArgs e)
    {
        if (DevicePanel == null || AppPanel == null)
        {
            return; // fires once during InitializeComponent before these are assigned
        }

        bool appMode = AppModeRadio.IsChecked == true;
        DevicePanel.Visibility = appMode ? Visibility.Collapsed : Visibility.Visible;
        AppPanel.Visibility = appMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCapturing)
        {
            StopCapture();
        }
        else
        {
            StartCapture();
        }
    }

    private void StartCapture()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            MessageBox.Show("Enter and save your OpenAI API key first.", "Game Audio Translator",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.SilenceThresholdRms = SensitivitySlider.Value;
        _deviceCaptureService.SilenceThresholdRms = SensitivitySlider.Value;
        _processCaptureService.SilenceThresholdRms = SensitivitySlider.Value;

        if (AppModeRadio.IsChecked == true)
        {
            var selected = ProcessCombo.SelectedItem as ProcessAudioSource;
            if (selected == null)
            {
                MessageBox.Show("Pick a running application first (click Refresh if the list is empty - it only shows apps with a visible window).",
                    "Game Audio Translator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _settings.CaptureMode = "Application";
            _settings.TargetProcessName = selected.ProcessName;
            SettingsStore.Save(_settings);

            _processCaptureService.Start(selected.ProcessId, selected.DisplayName);
        }
        else
        {
            var selected = DeviceCombo.SelectedItem as AudioDeviceOption;
            _settings.CaptureMode = "Device";
            _settings.AudioDeviceId = selected?.Id;
            SettingsStore.Save(_settings);

            try
            {
                _deviceCaptureService.Start(selected?.Device);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't start audio capture:\n{ex.Message}", "Game Audio Translator",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _isCapturing = true;
        StartStopButton.Content = "Stop Listening";

        if (ShowOverlayCheck.IsChecked == true)
        {
            EnsureOverlay();
            _overlay!.Show();
        }
    }

    private void StopCapture()
    {
        _deviceCaptureService.Stop();
        _processCaptureService.Stop();
        _isCapturing = false;
        StartStopButton.Content = "Start Listening";
        StatusText.Text = "Status: stopped";
    }

    private void OnSegmentReady(byte[] data, WaveFormat format)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            _segmentProcessor.Enqueue(data, format, _apiKey);
        }
    }

    private void OnCaptionReady(string language, string text)
    {
        Dispatcher.Invoke(() =>
        {
            _lastSpokenLanguage = language;
            LastLanguageText.Text = $"Last language heard: {Capitalize(language)} (Ctrl+Alt+R will reply in this language)";

            var line = new CaptionLine { Text = text, Language = language, Timestamp = DateTime.Now };
            _captions.Add(line);
            while (_captions.Count > 50)
            {
                _captions.RemoveAt(0);
            }
            CaptionList.ScrollIntoView(line);

            if (ShowOverlayCheck.IsChecked == true)
            {
                EnsureOverlay();
                _overlay!.AddCaption(line);
            }
        });
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);

    private void OnProcessingError(string message)
    {
        Dispatcher.Invoke(() => StatusText.Text = $"Status: {message}");
    }

    private void OnCaptureStatus(string message)
    {
        Dispatcher.Invoke(() => StatusText.Text = $"Status: {message}");
    }

    private void ShowOverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowOverlayCheck.IsChecked == true)
        {
            EnsureOverlay();
            _overlay!.Show();
        }
        else
        {
            _overlay?.Hide();
        }
    }

    private void EnsureOverlay()
    {
        _overlay ??= new OverlayWindow(_settings);
    }

    private void ToggleOverlayVisibility()
    {
        Dispatcher.Invoke(() => ShowOverlayCheck.IsChecked = !(ShowOverlayCheck.IsChecked == true));
    }

    private void ToggleOverlayLock()
    {
        Dispatcher.Invoke(() => _overlay?.ToggleLock());
    }

    private void ReplyButton_Click(object sender, RoutedEventArgs e) => ToggleReplyRecording();

    private void ReplayButton_Click(object sender, RoutedEventArgs e)
    {
        _replyVoice.Replay();
        StatusText.Text = "Status: replaying last reply";
    }

    private void SpeechSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeechSpeedLabel == null)
        {
            return; // fires once during InitializeComponent before this is assigned
        }

        SpeechSpeedLabel.Text = $"{e.NewValue:0.0}x";
        _settings.ReplySpeechSpeed = e.NewValue;
        SettingsStore.Save(_settings);
    }

    private void ToggleReplyRecording()
    {
        if (!ReplyButton.IsEnabled)
        {
            return; // still processing the previous reply
        }

        if (!_isRecordingReply)
        {
            StartReplyRecording();
        }
        else
        {
            _ = StopReplyRecordingAndSpeakAsync();
        }
    }

    private void StartReplyRecording()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            StatusText.Text = "Status: set your API key first";
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastSpokenLanguage))
        {
            StatusText.Text = "Status: no language heard yet - wait for a non-English line before replying";
            return;
        }

        try
        {
            _micRecorder.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: couldn't start the microphone ({ex.Message})";
            return;
        }

        _isRecordingReply = true;
        ReplyButton.Content = "■ Stop Reply";
        ReplyButton.Background = RecordingBrush;
        StatusText.Text = $"Status: recording your reply (will speak in {Capitalize(_lastSpokenLanguage)})... press Ctrl+Alt+R or click Stop Reply";
    }

    private async Task StopReplyRecordingAndSpeakAsync()
    {
        _isRecordingReply = false;
        ReplyButton.IsEnabled = false;
        ReplyButton.Content = "Working...";
        ReplyButton.ClearValue(Button.BackgroundProperty);

        var targetLanguage = _lastSpokenLanguage;
        var recording = _micRecorder.StopAndTakeRecording();

        try
        {
            if (recording == null || string.IsNullOrWhiteSpace(targetLanguage) || string.IsNullOrWhiteSpace(_apiKey))
            {
                StatusText.Text = "Status: didn't catch anything";
                return;
            }

            StatusText.Text = "Status: transcribing your reply...";
            var wav = AudioConverter.ConvertToWav16kMono(recording.Value.Data, recording.Value.Format);
            var transcription = await _translationService.TranscribeAsync(wav, _apiKey, languageHint: "en");

            if (string.IsNullOrWhiteSpace(transcription.Text))
            {
                StatusText.Text = "Status: didn't catch anything";
                return;
            }

            StatusText.Text = $"Status: translating to {Capitalize(targetLanguage)}...";
            var translated = await _translationService.TranslateTextAsync(transcription.Text, targetLanguage, _apiKey);

            if (string.IsNullOrWhiteSpace(translated))
            {
                StatusText.Text = "Status: translation failed";
                return;
            }

            StatusText.Text = $"Status: generating speech in {Capitalize(targetLanguage)}...";
            await _replyVoice.SpeakAsync(translated, _apiKey, _settings.ReplySpeechSpeed);
            StatusText.Text = $"Status: spoke reply in {Capitalize(targetLanguage)}";
            ReplayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Status: reply failed ({ex.Message})";
        }
        finally
        {
            ReplyButton.Content = "Start Reply";
            ReplyButton.ClearValue(Button.BackgroundProperty);
            ReplyButton.IsEnabled = true;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StopCapture();
        _micRecorder.Dispose();
        _replyVoice.Dispose();
        _hotkeys?.Dispose();
        _overlay?.Close();
        SettingsStore.Save(_settings);
    }
}

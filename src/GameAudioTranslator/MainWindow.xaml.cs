using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using GameAudioTranslator.Models;
using GameAudioTranslator.Services;
using NAudio.Wave;

namespace GameAudioTranslator;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CaptionLine> _captions = new();
    private readonly ObservableCollection<AudioDeviceOption> _devices = new();
    private readonly AudioCaptureService _captureService = new();
    private readonly SpeechTranslationService _translationService = new();
    private readonly SegmentProcessor _segmentProcessor;
    private readonly AppSettings _settings = SettingsStore.Load();

    private OverlayWindow? _overlay;
    private HotkeyManager? _hotkeys;
    private string? _apiKey;
    private bool _isCapturing;

    public MainWindow()
    {
        InitializeComponent();

        CaptionList.ItemsSource = _captions;
        DeviceCombo.ItemsSource = _devices;
        _segmentProcessor = new SegmentProcessor(_translationService, OnCaptionReady, OnProcessingError);

        _captureService.SegmentReady += OnSegmentReady;
        _captureService.StatusChanged += OnCaptureStatus;

        LoadDevices();
        LoadSettingsIntoUi();

        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hotkeys = new HotkeyManager();
        _hotkeys.Register(this);

        _hotkeys.RegisterHotkey(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.O), ToggleOverlayVisibility);

        _hotkeys.RegisterHotkey(HotkeyManager.MOD_CONTROL | HotkeyManager.MOD_ALT,
            (uint)KeyInterop.VirtualKeyFromKey(Key.L), ToggleOverlayLock);
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

    private void LoadSettingsIntoUi()
    {
        SensitivitySlider.Value = _settings.SilenceThresholdRms;
        _apiKey = SettingsStore.DecryptApiKey(_settings.EncryptedApiKey);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            ApiKeyBox.Password = _apiKey;
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

        var selected = DeviceCombo.SelectedItem as AudioDeviceOption;
        _settings.AudioDeviceId = selected?.Id;
        _settings.SilenceThresholdRms = SensitivitySlider.Value;
        SettingsStore.Save(_settings);

        _captureService.SilenceThresholdRms = SensitivitySlider.Value;

        try
        {
            _captureService.Start(selected?.Device);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't start audio capture:\n{ex.Message}", "Game Audio Translator",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
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
        _captureService.Stop();
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

    private void OnCaptionReady(string text)
    {
        Dispatcher.Invoke(() =>
        {
            var line = new CaptionLine { Text = text, Timestamp = DateTime.Now };
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

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        StopCapture();
        _hotkeys?.Dispose();
        _overlay?.Close();
        SettingsStore.Save(_settings);
    }
}

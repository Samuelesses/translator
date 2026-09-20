using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameAudioTranslator.Models;
using GameAudioTranslator.Services;

namespace GameAudioTranslator;

public partial class OverlayWindow : Window
{
    private const int MaxLines = 3;
    private static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(8);

    private readonly AppSettings _settings;
    private bool _locked = true;

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        SourceInitialized += (_, _) => WindowInterop.SetClickThrough(this, _locked);
        Loaded += OverlayWindow_Loaded;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.OverlayPositionSet)
        {
            Left = _settings.OverlayLeft;
            Top = _settings.OverlayTop;
        }
        else
        {
            var screen = SystemParameters.WorkArea;
            Left = screen.Width / 2 - Width / 2;
            Top = screen.Height - 160;
        }
    }

    public void AddCaption(CaptionLine line)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };

        if (!string.IsNullOrEmpty(line.DisplayLanguage))
        {
            text.Inlines.Add(new Run($"{line.DisplayLanguage}: ")
            {
                FontStyle = FontStyles.Italic,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
            });
        }

        text.Inlines.Add(new Run(line.Text)
        {
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });

        LinesPanel.Children.Add(text);

        while (LinesPanel.Children.Count > MaxLines)
        {
            LinesPanel.Children.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = DisplayDuration };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (LinesPanel.Children.Contains(text))
            {
                LinesPanel.Children.Remove(text);
            }
        };
        timer.Start();
    }

    public void ToggleLock()
    {
        _locked = !_locked;
        WindowInterop.SetClickThrough(this, _locked);
        RootBorder.BorderBrush = _locked ? null : Brushes.OrangeRed;
        RootBorder.BorderThickness = _locked ? new Thickness(0) : new Thickness(2);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_locked)
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _settings.OverlayLeft = Left;
        _settings.OverlayTop = Top;
        _settings.OverlayPositionSet = true;
        SettingsStore.Save(_settings);
        base.OnClosed(e);
    }
}

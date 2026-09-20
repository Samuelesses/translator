namespace GameAudioTranslator.Models;

public class AppSettings
{
    public string? EncryptedApiKey { get; set; }
    public string? AudioDeviceId { get; set; }
    public string CaptureMode { get; set; } = "Device"; // "Device" or "Application"
    public string? TargetProcessName { get; set; }
    public double SilenceThresholdRms { get; set; } = 0.02;
    public int SilenceDurationMs { get; set; } = 700;
    public int MinSegmentMs { get; set; } = 500;
    public int MaxSegmentMs { get; set; } = 15000;

    public double OverlayLeft { get; set; }
    public double OverlayTop { get; set; }
    public bool OverlayPositionSet { get; set; }

    public double ReplySpeechSpeed { get; set; } = 1.0;
}

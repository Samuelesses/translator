using System;

namespace GameAudioTranslator.Models;

public class CaptionLine
{
    public string Text { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string Display => $"[{Timestamp:HH:mm:ss}] {Text}";
}

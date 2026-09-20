using System;
using System.Globalization;

namespace GameAudioTranslator.Models;

public class CaptionLine
{
    public string Text { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public string DisplayLanguage => string.IsNullOrEmpty(Language)
        ? Language
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Language);

    public string Display => $"[{Timestamp:HH:mm:ss}] ({DisplayLanguage}) {Text}";
}

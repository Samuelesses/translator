using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;

namespace GameAudioTranslator.Services;

/// <summary>
/// Speaks translated text out loud locally (through the default speaker)
/// using Windows' built-in SAPI voices - no cloud call, no extra install.
/// Non-English languages only sound right if a matching voice/language pack
/// is installed (Settings > Time & Language > Speech); otherwise this falls
/// back to whatever voice Windows has by default, which will mispronounce
/// non-English text. <see cref="Speak"/> reports which happened so the UI
/// can tell the user instead of leaving it a silent surprise.
/// </summary>
public class ReplyVoiceService : IDisposable
{
    private static readonly Dictionary<string, string> LanguageToIsoCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["spanish"] = "es", ["french"] = "fr", ["german"] = "de",
        ["italian"] = "it", ["portuguese"] = "pt", ["russian"] = "ru", ["japanese"] = "ja",
        ["korean"] = "ko", ["chinese"] = "zh", ["arabic"] = "ar", ["dutch"] = "nl",
        ["polish"] = "pl", ["turkish"] = "tr", ["vietnamese"] = "vi", ["thai"] = "th",
        ["hindi"] = "hi", ["swedish"] = "sv", ["norwegian"] = "no", ["danish"] = "da",
        ["finnish"] = "fi", ["greek"] = "el", ["hebrew"] = "he", ["hungarian"] = "hu",
        ["czech"] = "cs", ["romanian"] = "ro", ["ukrainian"] = "uk", ["indonesian"] = "id",
        ["malay"] = "ms", ["tagalog"] = "tl", ["croatian"] = "hr", ["slovak"] = "sk",
        ["bulgarian"] = "bg", ["catalan"] = "ca", ["welsh"] = "cy"
    };

    private readonly SpeechSynthesizer _synth = new();

    public ReplyVoiceService()
    {
        _synth.SetOutputToDefaultAudioDevice();
    }

    /// <returns>true if a voice matching the requested language was found and used; false if it fell back to the default voice.</returns>
    public bool Speak(string text, string languageName)
    {
        bool matched = TrySelectVoice(languageName);
        _synth.SpeakAsync(text);
        return matched;
    }

    private bool TrySelectVoice(string languageName)
    {
        if (!LanguageToIsoCode.TryGetValue(languageName, out var iso))
        {
            return false;
        }

        try
        {
            var match = _synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled &&
                    v.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(iso, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                _synth.SelectVoice(match.VoiceInfo.Name);
                return true;
            }
        }
        catch
        {
            // Leave whatever voice is currently selected.
        }

        return false;
    }

    public void Dispose()
    {
        _synth.Dispose();
    }
}

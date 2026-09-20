using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameAudioTranslator.Services;

public class SpeechTranslationException : Exception
{
    public SpeechTranslationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Talks to OpenAI's Whisper endpoints. Language detection and translation
/// are two separate calls because the audio/translations endpoint's
/// "language" field always reports "english" (the output language) - it
/// never tells you what was actually spoken. So callers first ask
/// audio/transcriptions (with auto language detection) what language a
/// segment is in, and only then ask audio/translations for the English text.
/// </summary>
public class SpeechTranslationService
{
    private const string TranscriptionsEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const string TranslationsEndpoint = "https://api.openai.com/v1/audio/translations";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <returns>The detected spoken language (e.g. "english", "spanish"), or null if it couldn't be determined.</returns>
    public async Task<string?> DetectLanguageAsync(byte[] wavBytes, string apiKey, CancellationToken ct = default)
    {
        var body = await PostAudioAsync(TranscriptionsEndpoint, wavBytes, "verbose_json", apiKey, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
    }

    /// <returns>The English translation of the speech in the segment, or null if none was returned.</returns>
    public async Task<string?> TranslateToEnglishAsync(byte[] wavBytes, string apiKey, CancellationToken ct = default)
    {
        var body = await PostAudioAsync(TranslationsEndpoint, wavBytes, "json", apiKey, ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
    }

    private static async Task<string> PostAudioAsync(string endpoint, byte[] wavBytes, string responseFormat, string apiKey, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();

        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "segment.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent(responseFormat), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechTranslationException($"OpenAI API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        return body;
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? body;
            }
        }
        catch
        {
            // Not JSON (e.g. an HTML error page from a proxy); fall through to raw body.
        }

        return body;
    }
}

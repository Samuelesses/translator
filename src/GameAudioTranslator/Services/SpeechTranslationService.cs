using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GameAudioTranslator.Services;

public class SpeechTranslationException : Exception
{
    public SpeechTranslationException(string message) : base(message)
    {
    }
}

public record Transcription(string? Language, string? Text);

/// <summary>
/// Talks to OpenAI's Whisper and Chat Completions endpoints.
///
/// Translation deliberately does NOT use Whisper's own audio/translations
/// endpoint: in practice it's unreliable on short, noisy, radio-filtered
/// game voice chat - it frequently just transcribes instead of translating,
/// or mixes languages. Instead this transcribes in the original language
/// (audio/transcriptions, which also reports the detected language) and
/// then translates that text with a chat model, which handles casual/slangy
/// speech far more reliably than Whisper's built-in translation head.
/// </summary>
public class SpeechTranslationService
{
    private const string TranscriptionsEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string SpeechEndpoint = "https://api.openai.com/v1/audio/speech";
    private const string ChatModel = "gpt-4o-mini";
    private const string TtsModel = "tts-1";
    private const string TtsVoice = "alloy";

    private const string TranslationSystemPromptTemplate =
        "You are a translation engine for live game voice chat. Translate the user's message into " +
        "natural, colloquial {0}, preserving tone and slang where possible. Output ONLY the " +
        "translation - no quotes, no notes, no explanations.";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Transcribes a segment in its original language and reports what language that was.</summary>
    /// <param name="languageHint">Optional ISO-639-1 code (e.g. "en") if you already know the spoken language - improves accuracy/speed.</param>
    public async Task<Transcription> TranscribeAsync(byte[] wavBytes, string apiKey, string? languageHint = null, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "segment.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrEmpty(languageHint))
        {
            content.Add(new StringContent(languageHint), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionsEndpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechTranslationException($"OpenAI API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        string? language = doc.RootElement.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;
        string? text = doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        return new Transcription(language, text);
    }

    /// <returns>The translation of <paramref name="sourceText"/> into <paramref name="targetLanguage"/> (e.g. "english", "spanish"), or null if none was returned.</returns>
    public async Task<string?> TranslateTextAsync(string sourceText, string targetLanguage, string apiKey, CancellationToken ct = default)
    {
        var requestBody = new JsonObject
        {
            ["model"] = ChatModel,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = string.Format(TranslationSystemPromptTemplate, targetLanguage) },
                new JsonObject { ["role"] = "user", ["content"] = sourceText }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint)
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechTranslationException($"OpenAI API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return null;
        }

        return choices[0].GetProperty("message").GetProperty("content").GetString();
    }

    /// <returns>A WAV file (with header) of <paramref name="text"/> spoken aloud, via OpenAI's TTS.</returns>
    public async Task<byte[]> TextToSpeechAsync(string text, string apiKey, CancellationToken ct = default)
    {
        var requestBody = new JsonObject
        {
            ["model"] = TtsModel,
            ["voice"] = TtsVoice,
            ["input"] = text,
            ["response_format"] = "wav"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint)
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new SpeechTranslationException($"OpenAI API error ({(int)response.StatusCode}): {ExtractErrorMessage(errorBody)}");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
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

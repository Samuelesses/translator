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
/// Sends audio to the OpenAI audio translations endpoint, which transcribes
/// speech in any supported language directly into English text.
/// </summary>
public class SpeechTranslationService
{
    private const string Endpoint = "https://api.openai.com/v1/audio/translations";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<string?> TranslateToEnglishAsync(byte[] wavBytes, string apiKey, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();

        var audioContent = new ByteArrayContent(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", "segment.wav");
        content.Add(new StringContent("whisper-1"), "model");
        content.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechTranslationException($"OpenAI API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
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

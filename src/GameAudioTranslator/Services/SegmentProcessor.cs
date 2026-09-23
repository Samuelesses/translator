using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Runs captured audio segments through transcription + translation, up to
/// <see cref="MaxConcurrency"/> at once, so back-to-back speech (multiple
/// people talking in quick succession) doesn't queue up and make later
/// captions lag further and further behind - each segment's captions land
/// as soon as that segment finishes, not strictly in arrival order. English
/// speech is filtered out here - only non-English segments (the ones
/// actually worth subtitling) reach <c>onCaption</c>.
/// </summary>
public class SegmentProcessor
{
    private const int MaxConcurrency = 3;

    private readonly Channel<(byte[] Data, WaveFormat Format, string ApiKey)> _channel =
        Channel.CreateUnbounded<(byte[], WaveFormat, string)>();

    private readonly SpeechTranslationService _translationService;
    private readonly Action<string, string> _onCaption;
    private readonly Action<string> _onError;

    public SegmentProcessor(SpeechTranslationService translationService, Action<string, string> onCaption, Action<string> onError)
    {
        _translationService = translationService;
        _onCaption = onCaption;
        _onError = onError;
        _ = Task.Run(ProcessLoopAsync);
    }

    public void Enqueue(byte[] data, WaveFormat format, string apiKey)
    {
        _channel.Writer.TryWrite((data, format, apiKey));
    }

    private async Task ProcessLoopAsync()
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency };

        await Parallel.ForEachAsync(_channel.Reader.ReadAllAsync(), options, async (item, ct) =>
        {
            try
            {
                var wav = AudioConverter.ConvertToWav16kMono(item.Data, item.Format);

                var transcription = await _translationService.TranscribeAsync(wav, item.ApiKey, ct: ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(transcription.Text))
                {
                    return; // Nothing intelligible in this segment.
                }

                if (string.Equals(transcription.Language, "english", StringComparison.OrdinalIgnoreCase))
                {
                    return; // Already understood - nothing to subtitle.
                }

                var englishText = await _translationService.TranslateTextAsync(transcription.Text, "english", item.ApiKey, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(englishText))
                {
                    var language = string.IsNullOrWhiteSpace(transcription.Language) ? "Unknown" : transcription.Language;
                    _onCaption(language, englishText.Trim());
                }
            }
            catch (Exception ex)
            {
                _onError(ex.Message);
            }
        });
    }
}

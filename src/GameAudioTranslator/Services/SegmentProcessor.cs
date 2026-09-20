using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Serializes captured audio segments through conversion + translation so
/// captions are produced (and can be displayed) in the order they were spoken.
/// </summary>
public class SegmentProcessor
{
    private readonly Channel<(byte[] Data, WaveFormat Format, string ApiKey)> _channel =
        Channel.CreateUnbounded<(byte[], WaveFormat, string)>();

    private readonly SpeechTranslationService _translationService;
    private readonly Action<string> _onCaption;
    private readonly Action<string> _onError;

    public SegmentProcessor(SpeechTranslationService translationService, Action<string> onCaption, Action<string> onError)
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
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                var wav = AudioConverter.ConvertToWav16kMono(item.Data, item.Format);
                var text = await _translationService.TranslateToEnglishAsync(wav, item.ApiKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _onCaption(text.Trim());
                }
            }
            catch (Exception ex)
            {
                _onError(ex.Message);
            }
        }
    }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

/// <summary>
/// Speaks translated text out loud locally (through the default speaker)
/// using OpenAI's TTS API - reliable, natural-sounding speech in any
/// language automatically, unlike Windows' built-in voices which only cover
/// languages you've separately installed a pack for.
/// </summary>
public class ReplyVoiceService : IDisposable
{
    private readonly SpeechTranslationService _translationService;
    private WaveOutEvent? _player;
    private MemoryStream? _playbackStream;
    private RawSourceWaveStream? _playbackReader;

    public ReplyVoiceService(SpeechTranslationService translationService)
    {
        _translationService = translationService;
    }

    public async Task SpeakAsync(string text, string apiKey, CancellationToken ct = default)
    {
        var pcmBytes = await _translationService.TextToSpeechAsync(text, apiKey, ct).ConfigureAwait(false);

        StopPlayback();

        var stream = new MemoryStream(pcmBytes);
        var reader = new RawSourceWaveStream(stream, SpeechTranslationService.SpeechPcmFormat);
        var player = new WaveOutEvent();
        player.Init(reader);

        _playbackStream = stream;
        _playbackReader = reader;
        _player = player;

        player.Play();
    }

    private void StopPlayback()
    {
        try
        {
            _player?.Stop();
        }
        catch
        {
            // Best-effort.
        }

        _player?.Dispose();
        _playbackReader?.Dispose();
        _playbackStream?.Dispose();

        _player = null;
        _playbackReader = null;
        _playbackStream = null;
    }

    public void Dispose()
    {
        StopPlayback();
    }
}

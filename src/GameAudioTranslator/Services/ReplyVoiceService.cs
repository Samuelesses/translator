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
    private byte[]? _lastPcmBytes;

    /// <summary>True once at least one reply has been generated and can be replayed without another API call.</summary>
    public bool HasReply => _lastPcmBytes != null;

    /// <summary>Fires with the raw PCM bytes whenever a reply is generated - lets a voice mixer inject the same audio elsewhere.</summary>
    public event Action<byte[]>? ReplyGenerated;

    public ReplyVoiceService(SpeechTranslationService translationService)
    {
        _translationService = translationService;
    }

    /// <summary>Generates speech via the API (costs a call) and plays it.</summary>
    public async Task SpeakAsync(string text, string apiKey, double speed, CancellationToken ct = default)
    {
        var pcmBytes = await _translationService.TextToSpeechAsync(text, apiKey, speed, ct).ConfigureAwait(false);
        _lastPcmBytes = pcmBytes;
        ReplyGenerated?.Invoke(pcmBytes);
        Play(pcmBytes);
    }

    /// <summary>Replays the last generated reply, if any, at no extra cost - no new API call.</summary>
    public void Replay()
    {
        if (_lastPcmBytes != null)
        {
            Play(_lastPcmBytes);
        }
    }

    private void Play(byte[] pcmBytes)
    {
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

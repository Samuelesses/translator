using System.IO;
using NAudio.Utils;
using NAudio.Wave;

namespace GameAudioTranslator.Services;

public static class AudioConverter
{
    /// <summary>
    /// Downmixes/resamples a captured PCM buffer to 16 kHz mono 16-bit PCM and
    /// wraps it in a WAV container, ready to upload to the transcription API.
    /// </summary>
    public static byte[] ConvertToWav16kMono(byte[] pcmData, WaveFormat sourceFormat)
    {
        var targetFormat = new WaveFormat(16000, 16, 1);

        using var sourceMemory = new MemoryStream(pcmData);
        using var sourceStream = new RawSourceWaveStream(sourceMemory, sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat) { ResamplerQuality = 60 };

        using var outStream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(outStream), targetFormat))
        {
            var buffer = new byte[targetFormat.AverageBytesPerSecond];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, bytesRead);
            }

            writer.Flush();
        }

        return outStream.ToArray();
    }
}

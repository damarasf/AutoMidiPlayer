using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AutoMidiPlayer.WPF.Services.AudioToMidi;

/// <summary>
/// Decodes an audio file (mp3/wav/m4a/wma/...) into mono PCM samples at the model's
/// sample rate, matching librosa.load(sr=22050, mono=True).
/// </summary>
internal static class AudioDecoder
{
    public static float[] DecodeToMono22050(string audioPath)
    {
        using var reader = new MediaFoundationReader(audioPath);

        ISampleProvider sample = reader.ToSampleProvider();

        if (sample.WaveFormat.Channels > 1)
            sample = new MonoDownmixSampleProvider(sample);

        if (sample.WaveFormat.SampleRate != BasicPitchConstants.AudioSampleRate)
            sample = new WdlResamplingSampleProvider(sample, BasicPitchConstants.AudioSampleRate);

        var samples = new List<float>();
        var buffer = new float[BasicPitchConstants.AudioSampleRate]; // ~1 second per read
        int read;
        while ((read = sample.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
                samples.Add(buffer[i]);
        }

        return samples.ToArray();
    }

    /// <summary>
    /// Averages all channels into a single mono channel (handles any channel count).
    /// </summary>
    private sealed class MonoDownmixSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _sourceBuffer = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRequired = count * _channels;
            if (_sourceBuffer.Length < samplesRequired)
                _sourceBuffer = new float[samplesRequired];

            var sourceSamplesRead = source.Read(_sourceBuffer, 0, samplesRequired);
            var monoSamples = sourceSamplesRead / _channels;

            for (var i = 0; i < monoSamples; i++)
            {
                var sum = 0f;
                for (var ch = 0; ch < _channels; ch++)
                    sum += _sourceBuffer[i * _channels + ch];

                buffer[offset + i] = sum / _channels;
            }

            return monoSamples;
        }
    }
}

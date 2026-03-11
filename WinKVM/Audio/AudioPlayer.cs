using NAudio.Wave;

namespace WinKVM.Audio;

/// Streams PCM audio packets from the RAP protocol to the Windows audio device.
///
/// Uses NAudio's WasapiOut (WASAPI shared mode) with a BufferedWaveProvider
/// for low-latency playback. Handles dynamic format changes between packets.
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut?            _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat?           _currentFormat;
    private bool                  _disposed;
    private readonly object       _lock = new();

    /// Feed a PCM audio packet. May be called from any thread.
    public void Feed(byte[] pcmData, int sampleRate, int channels, int bitsPerSample, bool isSigned)
    {
        if (_disposed) return;
        lock (_lock)
        {
            var fmt = new WaveFormat(sampleRate, bitsPerSample, channels);

            // Restart playback if format changed or not yet started
            if (_currentFormat is null || !FormatsMatch(_currentFormat, fmt))
            {
                RestartPlayback(fmt);
            }

            // Feed PCM bytes into the buffer (thread-safe via internal lock in NAudio)
            _buffer?.AddSamples(pcmData, 0, pcmData.Length);
        }
    }

    private void RestartPlayback(WaveFormat fmt)
    {
        _output?.Stop();
        _output?.Dispose();
        _buffer = null;

        _buffer = new BufferedWaveProvider(fmt)
        {
            BufferDuration    = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,  // drop old data if behind; prevents audio lag
        };

        _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50 /*ms latency*/);
        _output.Init(_buffer);
        _output.Play();
        _currentFormat = fmt;
    }

    private static bool FormatsMatch(WaveFormat a, WaveFormat b) =>
        a.SampleRate      == b.SampleRate      &&
        a.Channels        == b.Channels        &&
        a.BitsPerSample   == b.BitsPerSample   &&
        a.Encoding        == b.Encoding;

    public void Stop()
    {
        lock (_lock)
        {
            _output?.Stop();
            _output?.Dispose();
            _output = null;
            _buffer = null;
            _currentFormat = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

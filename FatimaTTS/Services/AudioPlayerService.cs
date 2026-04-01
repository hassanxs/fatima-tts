using System.IO;
using NAudio.Wave;

namespace FatimaTTS.Services;

/// <summary>
/// Wraps NAudio for in-app audio playback.
/// Provides position tracking for progress bar and
/// waveform sample data for the visualizer.
/// </summary>
public class AudioPlayerService : IDisposable
{
    private WaveOutEvent?    _waveOut;
    private AudioFileReader? _reader;
    private System.Timers.Timer? _timer;

    // ── Events ────────────────────────────────────────────────────────────
    public event Action<TimeSpan, TimeSpan>? PositionChanged;  // (current, total)
    public event Action?  PlaybackStopped;
    public event Action?  PlaybackStarted;

    // ── State ─────────────────────────────────────────────────────────────
    public bool  IsPlaying  => _waveOut?.PlaybackState == PlaybackState.Playing;
    public bool  IsPaused   => _waveOut?.PlaybackState == PlaybackState.Paused;
    public bool  HasFile    => _reader is not null;

    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public TimeSpan Duration => _reader?.TotalTime   ?? TimeSpan.Zero;

    public float Volume
    {
        get => _waveOut?.Volume ?? 1f;
        set { if (_waveOut is not null) _waveOut.Volume = Math.Clamp(value, 0f, 1f); }
    }

    // ── Load ──────────────────────────────────────────────────────────────

    public void Load(string filePath)
    {
        Stop();
        DisposePlayer();

        _reader  = new AudioFileReader(filePath);
        _waveOut = new WaveOutEvent { DesiredLatency = 100 };
        _waveOut.Init(_reader);
        _waveOut.PlaybackStopped += OnPlaybackStopped;

        StartTimer();
    }

    // ── Playback controls ─────────────────────────────────────────────────

    public void Play()
    {
        if (_waveOut is null || _reader is null) return;
        _waveOut.Play();
        PlaybackStarted?.Invoke();
    }

    public void Pause()
    {
        _waveOut?.Pause();
    }

    public void Stop()
    {
        _waveOut?.Stop();
        if (_reader is not null)
            _reader.CurrentTime = TimeSpan.Zero;
    }

    public void Seek(double fraction)
    {
        if (_reader is null) return;
        _reader.CurrentTime = TimeSpan.FromSeconds(fraction * _reader.TotalTime.TotalSeconds);
    }

    public void SeekTo(TimeSpan position)
    {
        if (_reader is null) return;
        var clamped = TimeSpan.FromSeconds(
            Math.Clamp(position.TotalSeconds, 0, _reader.TotalTime.TotalSeconds));
        _reader.CurrentTime = clamped;
    }

    // ── Waveform data extraction ──────────────────────────────────────────

    /// <summary>
    /// Reads the audio file and returns normalised peak amplitudes
    /// bucketed into <paramref name="buckets"/> samples for waveform display.
    /// Returns float[] in range [0, 1].
    /// </summary>
    public static float[] ExtractWaveform(string filePath, int buckets = 200)
    {
        if (!File.Exists(filePath)) return new float[buckets];

        try
        {
            using var reader = new AudioFileReader(filePath);
            var      buffer  = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
            var      peaks   = new float[buckets];
            long     total   = reader.Length / sizeof(float);
            long     bSize   = Math.Max(1, total / buckets);
            int      bucket  = 0;
            float    max     = 0;
            long     count   = 0;
            int      read;

            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0 && bucket < buckets)
            {
                for (int i = 0; i < read && bucket < buckets; i++)
                {
                    float v = Math.Abs(buffer[i]);
                    if (v > max) max = v;
                    count++;
                    if (count >= bSize)
                    {
                        peaks[bucket++] = max;
                        max   = 0;
                        count = 0;
                    }
                }
            }

            // Normalise to [0,1]
            float globalMax = peaks.Max();
            if (globalMax > 0)
                for (int i = 0; i < buckets; i++)
                    peaks[i] /= globalMax;

            return peaks;
        }
        catch
        {
            return new float[buckets];
        }
    }

    // ── Timer ─────────────────────────────────────────────────────────────

    private void StartTimer()
    {
        _timer?.Dispose();
        _timer = new System.Timers.Timer(100);
        _timer.Elapsed += (_, _) =>
        {
            if (_reader is not null)
                PositionChanged?.Invoke(_reader.CurrentTime, _reader.TotalTime);
        };
        _timer.AutoReset = true;
        _timer.Start();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        PlaybackStopped?.Invoke();
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    private void DisposePlayer()
    {
        _timer?.Dispose();   _timer   = null;
        _waveOut?.Dispose(); _waveOut = null;
        _reader?.Dispose();  _reader  = null;
    }

    public void Dispose() => DisposePlayer();
}

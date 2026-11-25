using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KageDesk.App.Audio;

public enum SoundKind
{
    Approval,
    Done,
    Fail,
    Handoff
}

public sealed class RetroSoundEngine : IDisposable
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint PLAY_FLAGS = SND_MEMORY | SND_ASYNC | SND_NODEFAULT;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    private readonly Dictionary<SoundKind, byte[]> _wavBytes = new();
    private readonly Dictionary<SoundKind, GCHandle> _pinnedHandles = new();
    private readonly Dictionary<SoundKind, IntPtr> _pinnedPointers = new();
    private readonly Dictionary<SoundKind, long> _lastPlayedTimestamp = new();
    private readonly object _lock = new();
    private bool _disposed;

    public Func<bool>? IsSoundEnabled { get; set; }
    public Func<bool>? IsFullscreenGameDnd { get; set; }
    public Func<bool>? IsSessionLocked { get; set; }

    public RetroSoundEngine()
    {
        foreach (SoundKind kind in Enum.GetValues<SoundKind>())
        {
            byte[] bytes = BuildWavBytes(kind);
            _wavBytes[kind] = bytes;

            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            _pinnedHandles[kind] = handle;
            _pinnedPointers[kind] = handle.AddrOfPinnedObject();
            _lastPlayedTimestamp[kind] = 0;
        }
    }

    public bool Play(SoundKind kind)
    {
        if (_disposed) return false;

        // Guards
        if (IsSoundEnabled != null && !IsSoundEnabled()) return false;
        if (IsFullscreenGameDnd != null && IsFullscreenGameDnd()) return false;
        if (IsSessionLocked != null && IsSessionLocked()) return false;

        lock (_lock)
        {
            if (_disposed) return false;

            long now = Stopwatch.GetTimestamp();
            long elapsedTicks = now - _lastPlayedTimestamp[kind];
            double elapsedMs = (elapsedTicks * 1000.0) / Stopwatch.Frequency;

            if (elapsedMs < 1500.0)
            {
                // Under 1.5s cooldown
                return false;
            }

            _lastPlayedTimestamp[kind] = now;

            if (_pinnedPointers.TryGetValue(kind, out var ptr) && ptr != IntPtr.Zero)
            {
                try
                {
                    return PlaySound(ptr, IntPtr.Zero, PLAY_FLAGS);
                }
                catch
                {
                    return false;
                }
            }
        }

        return false;
    }

    public static byte[] BuildWavBytes(SoundKind kind)
    {
        const int sampleRate = 22050;
        float[] samples = kind switch
        {
            SoundKind.Approval => GenerateApproval(sampleRate),
            SoundKind.Done => GenerateDone(sampleRate),
            SoundKind.Fail => GenerateFail(sampleRate),
            SoundKind.Handoff => GenerateHandoff(sampleRate),
            _ => Array.Empty<float>()
        };

        return EncodePcmToWav(samples, sampleRate);
    }

    private static float[] GenerateApproval(int sampleRate)
    {
        // Warm dual-tone bell: E5 (659.25Hz) -> A5 (880.0Hz), 180ms, soft amplitude
        float duration = 0.180f;
        int totalSamples = (int)(sampleRate * duration);
        float[] buf = new float[totalSamples];

        float split = duration * 0.45f;
        float phase = 0f;

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            float freq = t < split ? 659.25f : 880.0f;
            phase += (2f * MathF.PI * freq) / sampleRate;

            // Warm tone: soft sine blend with 2nd harmonic
            float val = MathF.Sin(phase) * 0.75f + MathF.Sin(phase * 2f) * 0.25f;

            // Bell decay envelope
            float env = MathF.Exp(-t * 14f) * 0.32f;
            if (t < 0.012f) env *= (t / 0.012f); // 12ms soft attack

            buf[i] = val * env;
        }

        return buf;
    }

    private static float[] GenerateDone(int sampleRate)
    {
        // Uplifting 3-note 8-bit arpeggio: C5 (523.25Hz) -> E5 (659.25Hz) -> G5 (783.99Hz), 220ms
        float duration = 0.220f;
        int totalSamples = (int)(sampleRate * duration);
        float[] buf = new float[totalSamples];

        float noteLen = duration / 3f;
        float phase = 0f;

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            int noteIdx = Math.Min(2, (int)(t / noteLen));
            float freq = noteIdx switch
            {
                0 => 523.25f,
                1 => 659.25f,
                _ => 783.99f
            };

            phase += (2f * MathF.PI * freq) / sampleRate;

            // Retro 8-bit pulse with soft clipped edge
            float rawSine = MathF.Sin(phase);
            float pulse = rawSine > 0f ? 0.6f : -0.6f;
            float val = pulse * 0.45f + rawSine * 0.55f;

            // Per-note envelope + overall decay
            float noteT = t - (noteIdx * noteLen);
            float noteEnv = 1f;
            if (noteT < 0.008f) noteEnv = noteT / 0.008f;
            else if (noteT > noteLen - 0.015f) noteEnv = Math.Max(0f, (noteLen - noteT) / 0.015f);

            float globalEnv = (1f - (t / duration) * 0.35f) * 0.28f;
            buf[i] = val * noteEnv * globalEnv;
        }

        return buf;
    }

    private static float[] GenerateFail(int sampleRate)
    {
        // Soft low 2-tone buzz: A3 (220.0Hz) -> F3 (174.61Hz), 200ms
        float duration = 0.200f;
        int totalSamples = (int)(sampleRate * duration);
        float[] buf = new float[totalSamples];

        float split = duration * 0.5f;
        float phase = 0f;

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            float freq = t < split ? 220.0f : 174.61f;
            phase += (2f * MathF.PI * freq) / sampleRate;

            // Low mellow buzz: triangle-sine blend
            float tri = 2f * MathF.Abs(2f * (phase / (2f * MathF.PI) - MathF.Floor(phase / (2f * MathF.PI) + 0.5f))) - 1f;
            float val = tri * 0.6f + MathF.Sin(phase) * 0.4f;

            // Soft envelope
            float env = 0.26f;
            if (t < 0.015f) env *= (t / 0.015f);
            if (t > duration - 0.035f) env *= Math.Max(0f, (duration - t) / 0.035f);

            buf[i] = val * env;
        }

        return buf;
    }

    private static float[] GenerateHandoff(int sampleRate)
    {
        // Subtle crisp chime: G4 (392.0Hz) -> D5 (587.33Hz), 110ms
        float duration = 0.110f;
        int totalSamples = (int)(sampleRate * duration);
        float[] buf = new float[totalSamples];

        float split = duration * 0.45f;
        float phase = 0f;

        for (int i = 0; i < totalSamples; i++)
        {
            float t = (float)i / sampleRate;
            float freq = t < split ? 392.0f : 587.33f;
            phase += (2f * MathF.PI * freq) / sampleRate;

            // Crisp sparkling chime: sine + 3rd harmonic
            float val = MathF.Sin(phase) * 0.8f + MathF.Sin(phase * 3f) * 0.2f;

            // Fast chime envelope
            float env = MathF.Exp(-t * 22f) * 0.30f;
            if (t < 0.006f) env *= (t / 0.006f);

            buf[i] = val * env;
        }

        return buf;
    }

    private static byte[] EncodePcmToWav(float[] samples, int sampleRate)
    {
        int sampleCount = samples.Length;
        int dataSize = sampleCount * sizeof(short);
        byte[] wav = new byte[44 + dataSize];

        // 1. "RIFF"
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        int chunkSize = 36 + dataSize;
        BitConverter.TryWriteBytes(wav.AsSpan(4, 4), chunkSize);

        // 2. "WAVE"
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

        // 3. "fmt "
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        BitConverter.TryWriteBytes(wav.AsSpan(16, 4), 16); // Subchunk1Size = 16 for PCM
        BitConverter.TryWriteBytes(wav.AsSpan(20, 2), (short)1); // AudioFormat = 1 (PCM)
        BitConverter.TryWriteBytes(wav.AsSpan(22, 2), (short)1); // NumChannels = 1 (Mono)
        BitConverter.TryWriteBytes(wav.AsSpan(24, 4), sampleRate); // SampleRate
        BitConverter.TryWriteBytes(wav.AsSpan(28, 4), sampleRate * sizeof(short)); // ByteRate = SampleRate * NumChannels * BitsPerSample/8
        BitConverter.TryWriteBytes(wav.AsSpan(32, 2), (short)sizeof(short)); // BlockAlign = NumChannels * BitsPerSample/8
        BitConverter.TryWriteBytes(wav.AsSpan(34, 2), (short)16); // BitsPerSample = 16

        // 4. "data"
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        BitConverter.TryWriteBytes(wav.AsSpan(40, 4), dataSize);

        // Samples
        int offset = 44;
        for (int i = 0; i < sampleCount; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            short pcm = (short)(s * 32767f);
            BitConverter.TryWriteBytes(wav.AsSpan(offset, 2), pcm);
            offset += 2;
        }

        return wav;
    }

    public void StopAll()
    {
        try
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch
        {
            // Ignore if native call fails during shutdown
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAll();

        lock (_lock)
        {
            foreach (var kvp in _pinnedHandles)
            {
                if (kvp.Value.IsAllocated)
                {
                    kvp.Value.Free();
                }
            }
            _pinnedHandles.Clear();
            _pinnedPointers.Clear();
        }
    }
}

using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using KageDesk.App.Audio;
using Xunit;

namespace KageDesk.App.Tests;

public class RetroSoundEngineTests
{
    [Theory]
    [InlineData(SoundKind.Approval)]
    [InlineData(SoundKind.Done)]
    [InlineData(SoundKind.Fail)]
    [InlineData(SoundKind.Handoff)]
    public void BuildWavBytes_ProducesValidRiffWavHeader(SoundKind kind)
    {
        byte[] wav = RetroSoundEngine.BuildWavBytes(kind);

        Assert.NotNull(wav);
        Assert.True(wav.Length > 44);

        // Check RIFF header
        string riff = Encoding.ASCII.GetString(wav, 0, 4);
        Assert.Equal("RIFF", riff);

        int chunkSize = BitConverter.ToInt32(wav, 4);
        Assert.Equal(wav.Length - 8, chunkSize);

        string wave = Encoding.ASCII.GetString(wav, 8, 4);
        Assert.Equal("WAVE", wave);

        string fmt = Encoding.ASCII.GetString(wav, 12, 4);
        Assert.Equal("fmt ", fmt);

        int subchunk1Size = BitConverter.ToInt32(wav, 16);
        Assert.Equal(16, subchunk1Size);

        short audioFormat = BitConverter.ToInt16(wav, 20);
        Assert.Equal(1, audioFormat); // PCM

        short numChannels = BitConverter.ToInt16(wav, 22);
        Assert.Equal(1, numChannels); // Mono

        int sampleRate = BitConverter.ToInt32(wav, 24);
        Assert.Equal(22050, sampleRate);

        int byteRate = BitConverter.ToInt32(wav, 28);
        Assert.Equal(44100, byteRate);

        short blockAlign = BitConverter.ToInt16(wav, 32);
        Assert.Equal(2, blockAlign);

        short bitsPerSample = BitConverter.ToInt16(wav, 34);
        Assert.Equal(16, bitsPerSample);

        string dataHeader = Encoding.ASCII.GetString(wav, 36, 4);
        Assert.Equal("data", dataHeader);

        int dataSize = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataSize);
    }

    [Fact]
    public void BuildWavBytes_DurationsMatchSpecification()
    {
        // 22050 Hz, 16-bit mono = 44100 bytes/sec
        byte[] approval = RetroSoundEngine.BuildWavBytes(SoundKind.Approval);
        byte[] done = RetroSoundEngine.BuildWavBytes(SoundKind.Done);
        byte[] fail = RetroSoundEngine.BuildWavBytes(SoundKind.Fail);
        byte[] handoff = RetroSoundEngine.BuildWavBytes(SoundKind.Handoff);

        // Approval: 180ms
        int approvalData = approval.Length - 44;
        double approvalMs = (approvalData / 2.0 / 22050.0) * 1000.0;
        Assert.InRange(approvalMs, 175.0, 185.0);

        // Done: 220ms
        int doneData = done.Length - 44;
        double doneMs = (doneData / 2.0 / 22050.0) * 1000.0;
        Assert.InRange(doneMs, 215.0, 225.0);

        // Fail: 200ms
        int failData = fail.Length - 44;
        double failMs = (failData / 2.0 / 22050.0) * 1000.0;
        Assert.InRange(failMs, 195.0, 205.0);

        // Handoff: 110ms
        int handoffData = handoff.Length - 44;
        double handoffMs = (handoffData / 2.0 / 22050.0) * 1000.0;
        Assert.InRange(handoffMs, 105.0, 115.0);
    }

    [Fact]
    public void Play_GuardsPreventPlaybackWhenDisabledOrSuppressed()
    {
        using var engine = new RetroSoundEngine();

        // 1. Sound Disabled
        engine.IsSoundEnabled = () => false;
        engine.IsFullscreenGameDnd = () => false;
        engine.IsSessionLocked = () => false;
        Assert.False(engine.Play(SoundKind.Approval));

        // 2. Fullscreen Game DND
        engine.IsSoundEnabled = () => true;
        engine.IsFullscreenGameDnd = () => true;
        engine.IsSessionLocked = () => false;
        Assert.False(engine.Play(SoundKind.Approval));

        // 3. Session Locked
        engine.IsSoundEnabled = () => true;
        engine.IsFullscreenGameDnd = () => false;
        engine.IsSessionLocked = () => true;
        Assert.False(engine.Play(SoundKind.Approval));
    }

    [Fact]
    public void Play_EnforcesPerSoundCooldown()
    {
        using var engine = new RetroSoundEngine();
        engine.IsSoundEnabled = () => true;
        engine.IsFullscreenGameDnd = () => false;
        engine.IsSessionLocked = () => false;

        // First call plays (or attempts winmm PlaySoundW)
        _ = engine.Play(SoundKind.Handoff);

        // Immediate subsequent call MUST return false due to 1.5s cooldown
        bool secondCall = engine.Play(SoundKind.Handoff);
        Assert.False(secondCall);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesSafely()
    {
        var engine = new RetroSoundEngine();
        engine.Dispose();
        engine.Dispose(); // No exception

        Assert.False(engine.Play(SoundKind.Approval));
    }

    [Theory]
    [InlineData(SoundKind.Approval)]
    [InlineData(SoundKind.Done)]
    [InlineData(SoundKind.Fail)]
    [InlineData(SoundKind.Handoff)]
    public void BuildWavBytes_WaveformsAreNonSilentAndBounded(SoundKind kind)
    {
        byte[] wav = RetroSoundEngine.BuildWavBytes(kind);
        int sampleCount = (wav.Length - 44) / 2;
        Assert.True(sampleCount > 0);

        bool foundNonZero = false;
        int maxAbsSample = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(wav, 44 + i * 2);
            if (sample != 0) foundNonZero = true;
            int abs = Math.Abs((int)sample);
            if (abs > maxAbsSample) maxAbsSample = abs;
        }

        Assert.True(foundNonZero, $"Waveform for {kind} was completely silent (all zeros)");
        // Amplitude should be soft and pleasant (between 1000 and 28000), never harsh clipping
        Assert.InRange(maxAbsSample, 1500, 30000);
    }

    [Fact]
    public void SetMutedAndSetEnabled_StopsPlaybackAndMutesSafely()
    {
        using var engine = new RetroSoundEngine();
        engine.SetMuted(true);
        Assert.False(engine.Play(SoundKind.Done));

        engine.SetMuted(false);
        engine.SetEnabled(false);
        Assert.False(engine.Play(SoundKind.Done));

        engine.SetEnabled(true);
        // Play succeeds or attempts playback
        _ = engine.Play(SoundKind.Done);
    }

    [Fact]
    public async Task ConcurrentAccess_PlayMuteEnabledDispose_DoesNotThrowOrDeadlock()
    {
        var engine = new RetroSoundEngine();
        var tasks = new List<Task>();

        for (int i = 0; i < 8; i++)
        {
            int index = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    if (index % 3 == 0) engine.Play(SoundKind.Handoff);
                    else if (index % 3 == 1) engine.SetMuted(j % 2 == 0);
                    else engine.SetEnabled(j % 2 == 0);
                }
            }));
        }

        await Task.WhenAll(tasks);
        engine.Dispose();
    }

    [Fact]
    public async Task ConcurrentAccess_PlayWhileDisposing_ZeroCrashesZeroDoubleFreeAndPlaybackBlockedAfterDispose()
    {
        var engine = new RetroSoundEngine();
        using var barrier = new Barrier(6);
        var playTasks = new List<Task>();
        var exceptions = new ConcurrentBag<Exception>();

        // 4 threads actively invoking Play in a loop
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            playTasks.Add(Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                    for (int j = 0; j < 200; j++)
                    {
                        var kind = (SoundKind)(j % 4);
                        _ = engine.Play(kind);
                        if (index % 2 == 0 && j % 10 == 0)
                        {
                            engine.SetMuted(j % 20 == 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        // 1 thread invoking Dispose mid-loop
        var disposeTask = Task.Run(async () =>
        {
            try
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                await Task.Yield();
                engine.Dispose();
                engine.Dispose(); // redundant dispose safety
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        barrier.SignalAndWait(TimeSpan.FromSeconds(5));

        await Task.WhenAll(playTasks.Concat(new[] { disposeTask }));

        Assert.Empty(exceptions);
        // Zero playback calls accepted after Dispose has completed
        foreach (SoundKind kind in Enum.GetValues<SoundKind>())
        {
            Assert.False(engine.Play(kind));
        }
    }
}


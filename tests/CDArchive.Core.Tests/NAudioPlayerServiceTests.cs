using CDArchive.Core;
using CDArchive.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using NAudio.Wave;

namespace CDArchive.Core.Tests;

/// <summary>
/// Smoke tests for <see cref="NAudioPlayerService"/> that exercise the parts
/// we can validate without an audio device: state machine after construction,
/// error paths, file loading + Duration reporting, and Seek clamping.
///
/// Actual playback (Play / Pause / PlaybackEnded) is exercised manually via
/// the UI; here we just make sure Load wires up the reader correctly.
/// </summary>
public class NAudioPlayerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public NAudioPlayerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "CDArchivePlayerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a one-second 44.1kHz/16-bit mono silent WAV to <paramref name="path"/>.
    /// MediaFoundationReader on Windows handles WAV natively, so this is enough
    /// to exercise the reader without any decode dependency.
    /// </summary>
    private static void WriteSilentWav(string path, double seconds = 1.0)
    {
        var format = new WaveFormat(rate: 44100, bits: 16, channels: 1);
        using var writer = new WaveFileWriter(path, format);
        var sampleCount = (int)(format.SampleRate * seconds);
        var buffer = new byte[sampleCount * format.BlockAlign];
        writer.Write(buffer, 0, buffer.Length);
    }

    [Fact]
    public void InitialState_IsEmpty()
    {
        using var svc = new NAudioPlayerService();
        Assert.Equal(PlayerState.Empty, svc.State);
        Assert.Equal(TimeSpan.Zero, svc.Position);
        Assert.Equal(TimeSpan.Zero, svc.Duration);
        Assert.Null(svc.CurrentFilePath);
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        using var svc = new NAudioPlayerService();
        var bogus = Path.Combine(_tempDir, "does-not-exist.mp3");
        Assert.Throws<FileNotFoundException>(() => svc.Load(bogus));
        Assert.Equal(PlayerState.Empty, svc.State);
    }

    [Fact]
    public void Load_EmptyPath_Throws()
    {
        using var svc = new NAudioPlayerService();
        Assert.Throws<ArgumentException>(() => svc.Load(""));
    }

    [Fact]
    public void Load_RealWav_TransitionsToStoppedAndReportsDuration()
    {
        var path = Path.Combine(_tempDir, "silence.wav");
        WriteSilentWav(path, seconds: 1.5);

        using var svc = new NAudioPlayerService();
        bool durationRaised = false;
        svc.DurationKnown += (_, _) => durationRaised = true;

        svc.Load(path);

        Assert.Equal(PlayerState.Stopped, svc.State);
        Assert.Equal(path, svc.CurrentFilePath);
        Assert.True(durationRaised);
        // Allow a generous tolerance — exact duration depends on the WAV header maths.
        Assert.InRange(svc.Duration.TotalSeconds, 1.4, 1.7);
    }

    [Fact]
    public void Seek_ClampsToBounds()
    {
        var path = Path.Combine(_tempDir, "silence.wav");
        WriteSilentWav(path, seconds: 1.0);

        using var svc = new NAudioPlayerService();
        svc.Load(path);

        svc.Seek(TimeSpan.FromSeconds(-5));
        Assert.Equal(TimeSpan.Zero, svc.Position);

        svc.Seek(TimeSpan.FromSeconds(100));
        Assert.InRange(svc.Position.TotalSeconds, 0.9, 1.1);
    }

    [Fact]
    public void Seek_BeforeLoad_IsNoOp()
    {
        using var svc = new NAudioPlayerService();
        svc.Seek(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.Zero, svc.Position);
    }

    [Fact]
    public void Pause_BeforePlay_IsNoOp()
    {
        var path = Path.Combine(_tempDir, "silence.wav");
        WriteSilentWav(path, seconds: 1.0);

        using var svc = new NAudioPlayerService();
        svc.Load(path);
        svc.Pause();
        Assert.Equal(PlayerState.Stopped, svc.State);
    }

    [Fact]
    public void DisposingServiceProvider_DisposesSingletonPlayer()
    {
        // Locks in the contract App.OnExit relies on: disposing the DI container
        // must cascade Dispose() to NAudioPlayerService (which owns a Timer,
        // MediaFoundationReader, and WaveOutEvent — leaks the audio device on
        // crash exit if missed). See Rework.md C1.
        var services = new ServiceCollection();
        services.AddCoreServices();
        var provider = services.BuildServiceProvider();

        var player = (NAudioPlayerService)provider.GetRequiredService<IAudioPlayerService>();
        Assert.False(player.IsDisposed);

        provider.Dispose();

        Assert.True(player.IsDisposed);
    }

    [Fact]
    public void Load_AfterFirstLoad_ReplacesCurrentFile()
    {
        var a = Path.Combine(_tempDir, "a.wav");
        var b = Path.Combine(_tempDir, "b.wav");
        WriteSilentWav(a, seconds: 1.0);
        WriteSilentWav(b, seconds: 2.0);

        using var svc = new NAudioPlayerService();
        svc.Load(a);
        svc.Load(b);

        Assert.Equal(b, svc.CurrentFilePath);
        Assert.InRange(svc.Duration.TotalSeconds, 1.9, 2.2);
    }
}

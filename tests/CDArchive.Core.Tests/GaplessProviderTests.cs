using CDArchive.Core.Services;
using NAudio.Wave;

namespace CDArchive.Core.Tests;

/// <summary>
/// <see cref="GaplessProvider"/> reads a current track and, on EOF, hands over
/// to a pre-loaded next track seamlessly (within a single Read), firing the
/// transition callback. A format-mismatched next is rejected.
/// </summary>
public class GaplessProviderTests
{
    private static readonly WaveFormat Cd = new(44100, 16, 2);

    /// <summary>A finite in-memory stream that yields <c>len</c> ramp bytes then EOF.</summary>
    private sealed class FiniteStream : WaveStream
    {
        private readonly long _len;
        private long _pos;
        private readonly WaveFormat _fmt;
        public bool Disposed { get; private set; }

        public FiniteStream(long len, WaveFormat fmt) { _len = len; _fmt = fmt; }

        public override WaveFormat WaveFormat => _fmt;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _len - _pos);
            if (n <= 0) return 0;
            for (int i = 0; i < n; i++) buffer[offset + i] = (byte)((_pos + i) & 0xFF);
            _pos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Read_HandsOverToNext_Gaplessly_AndFiresTransition()
    {
        var current = new FiniteStream(100, Cd);
        var next = new FiniteStream(60, Cd);
        int transitions = 0;
        var provider = new GaplessProvider(current, () => transitions++);

        Assert.True(provider.SetNext(next));

        // One Read spans the boundary: 100 (current) + 60 (next) = 160 bytes.
        var buffer = new byte[1000];
        int read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(160, read);
        Assert.Equal(1, transitions);
        Assert.True(current.Disposed); // promoted → old current disposed

        // Nothing left → further reads return 0 (end of session).
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));

        provider.Dispose();
    }

    [Fact]
    public void Read_NoNext_EndsAtCurrentEof_NoTransition()
    {
        var current = new FiniteStream(40, Cd);
        int transitions = 0;
        var provider = new GaplessProvider(current, () => transitions++);

        var buffer = new byte[1000];
        Assert.Equal(40, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length)); // EOF
        Assert.Equal(0, transitions);

        provider.Dispose();
    }

    [Fact]
    public void SetNext_FormatMismatch_Rejected_CallerKeepsOwnership()
    {
        var provider = new GaplessProvider(new FiniteStream(10, Cd), () => { });

        var wrongRate = new FiniteStream(10, new WaveFormat(48000, 16, 2));
        Assert.False(provider.SetNext(wrongRate));
        Assert.False(wrongRate.Disposed); // provider didn't take ownership

        wrongRate.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void SetNext_Null_ClearsQueuedNext()
    {
        var queued = new FiniteStream(10, Cd);
        var provider = new GaplessProvider(new FiniteStream(10, Cd), () => { });

        Assert.True(provider.SetNext(queued));
        Assert.True(provider.SetNext(null));   // clear
        Assert.True(queued.Disposed);          // the previously-queued next is disposed

        provider.Dispose();
    }
}

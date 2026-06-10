using CDArchive.Core.Services;
using NAudio.Wave;

namespace CDArchive.Core.Tests;

/// <summary>
/// Verifies <see cref="DecodeSeekStream"/> lands seeks sample-accurately by
/// decoding to the target, even when the inner reader's <em>native</em> seek is
/// broken — which models the real bug (Media Foundation's FLAC seek undershoots
/// the requested position by several seconds while still reporting it).
/// </summary>
public class DecodeSeekStreamTests
{
    /// <summary>
    /// A deterministic byte-ramp source: the byte at absolute position P decodes
    /// to value (P &amp; 0xFF), so a reader can prove exactly where it landed.
    /// Its native Position <em>setter deliberately undershoots</em> (like MF's
    /// FLAC seek) — the wrapper must not rely on it.
    /// </summary>
    private sealed class RampStream : WaveStream
    {
        private readonly long _len;
        private long _pos;
        private readonly WaveFormat _fmt = new(8000, 8, 1); // 1 byte/sample, BlockAlign 1

        public RampStream(long len) => _len = len;

        public override WaveFormat WaveFormat => _fmt;
        public override long Length => _len;

        public override long Position
        {
            get => _pos;
            set => _pos = Math.Max(0, value - 40); // simulate a broken, undershooting native seek
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = 0;
            while (n < count && _pos < _len)
            {
                buffer[offset + n] = (byte)(_pos & 0xFF);
                _pos++;
                n++;
            }
            return n;
        }
    }

    private static int ReadOneByte(WaveStream s)
    {
        var b = new byte[1];
        int n = s.Read(b, 0, 1);
        Assert.Equal(1, n);
        return b[0];
    }

    [Fact]
    public void ForwardSeek_DecodesToExactTarget_IgnoringBrokenNativeSeek()
    {
        using var s = new DecodeSeekStream(() => new RampStream(1000));

        s.Position = 500;                       // request a seek
        Assert.Equal(500, s.Position);          // reports the target immediately

        // The first decoded byte is the one AT position 500 — proving the
        // decode-discard landed exactly there, not 40 bytes early.
        Assert.Equal(500 & 0xFF, ReadOneByte(s));
        Assert.Equal(501, s.Position);
    }

    [Fact]
    public void BackwardSeek_RebuildsAndDecodesToExactTarget()
    {
        using var s = new DecodeSeekStream(() => new RampStream(1000));

        // Move forward first.
        s.Position = 600;
        Assert.Equal(600 & 0xFF, ReadOneByte(s));

        // Now seek backward — the wrapper rebuilds from the start and decodes
        // forward to 200, landing exactly there.
        s.Position = 200;
        Assert.Equal(200 & 0xFF, ReadOneByte(s));
        Assert.Equal(201, s.Position);
    }

    [Fact]
    public void Seek_ClampsToStreamLength()
    {
        using var s = new DecodeSeekStream(() => new RampStream(1000));

        s.Position = -100;
        Assert.Equal(0, s.Position);

        s.Position = 5000;
        Assert.Equal(1000, s.Position); // clamped to Length
    }
}

using Dictate.Core.Audio;
using Xunit;

namespace Dictate.Core.Tests;

public class PreRollBufferTests
{
    private static byte[] Seq(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Keeps_everything_under_capacity()
    {
        var b = new PreRollBuffer(10);
        b.Write(Seq(0, 4));
        b.Write(Seq(4, 3));
        Assert.Equal(Seq(0, 7), b.Drain());
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Keeps_only_the_newest_bytes_when_it_wraps()
    {
        var b = new PreRollBuffer(10);
        b.Write(Seq(0, 6));
        b.Write(Seq(6, 6));
        b.Write(Seq(12, 3));
        Assert.Equal(Seq(5, 10), b.Drain());
    }

    [Fact]
    public void A_write_larger_than_capacity_keeps_its_tail()
    {
        var b = new PreRollBuffer(4);
        b.Write(Seq(0, 2));
        b.Write(Seq(10, 9));
        Assert.Equal(Seq(15, 4), b.Drain());
    }

    [Fact]
    public void Works_after_drain()
    {
        var b = new PreRollBuffer(5);
        b.Write(Seq(0, 4));
        b.Drain();
        b.Write(Seq(20, 7));
        Assert.Equal(Seq(22, 5), b.Drain());
    }

    [Fact]
    public void Duration_sizing_is_16_bit_mono()
    {
        var b = PreRollBuffer.ForDuration(TimeSpan.FromMilliseconds(500), 16000);
        b.Write(new byte[100_000]);
        Assert.Equal(16000, b.Count);
    }
}

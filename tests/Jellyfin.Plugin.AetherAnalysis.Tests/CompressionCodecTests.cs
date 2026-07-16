using System.Text;
using Jellyfin.Plugin.AetherAnalysis.Infrastructure;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class CompressionCodecTests
{
    [Fact]
    public void BrotliRoundTripPreservesDocument()
    {
        var source = Encoding.UTF8.GetBytes(new string('a', 4096));
        var compressed = CompressionCodec.Compress(source);
        var restored = CompressionCodec.Decompress(compressed, source.Length);

        Assert.Equal(source, restored);
        Assert.True(compressed.Length < source.Length);
    }
}

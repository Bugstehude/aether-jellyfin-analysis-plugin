using System.IO.Compression;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Brotli codec used for canonical analysis documents.</summary>
public static class CompressionCodec
{
    private const int CompressionQuality = 5;
    private const int WindowBits = 22;

    /// <summary>Compresses a JSON document using the contract's Brotli level.</summary>
    public static byte[] Compress(ReadOnlySpan<byte> source)
    {
        var maximumLength = BrotliEncoder.GetMaxCompressedLength(source.Length);
        var destination = new byte[maximumLength];
        if (!BrotliEncoder.TryCompress(source, destination, out var written, CompressionQuality, WindowBits))
        {
            throw new InvalidOperationException("Unable to Brotli-compress analysis document.");
        }

        return destination.AsSpan(0, written).ToArray();
    }

    /// <summary>Decompresses a JSON document with an explicit output bound.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int uncompressedBytes)
    {
        var destination = new byte[uncompressedBytes];
        if (!BrotliDecoder.TryDecompress(source, destination, out var written) || written != uncompressedBytes)
        {
            throw new InvalidDataException("Stored AETHER analysis document is corrupt.");
        }

        return destination;
    }
}

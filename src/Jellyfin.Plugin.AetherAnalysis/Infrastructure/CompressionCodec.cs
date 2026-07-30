using System.IO.Compression;

namespace Jellyfin.Plugin.AetherAnalysis.Infrastructure;

/// <summary>Brotli codec used for canonical analysis documents.</summary>
public static class CompressionCodec
{
    private const int CompressionQuality = 5;
    private const int WindowBits = 22;

    /// <summary>
    /// Largest accepted canonical document after decompression. Uploads are capped at 50 MiB;
    /// the additional room covers the generated master representation without allowing corrupt
    /// database metadata to request an unbounded allocation.
    /// </summary>
    public const int MaximumUncompressedBytes = 64 * 1024 * 1024;

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
        if (uncompressedBytes < 0 || uncompressedBytes > MaximumUncompressedBytes)
        {
            throw new InvalidDataException("Stored AETHER analysis document exceeds the decompression limit.");
        }

        var destination = new byte[uncompressedBytes];
        if (!BrotliDecoder.TryDecompress(source, destination, out var written) || written != uncompressedBytes)
        {
            throw new InvalidDataException("Stored AETHER analysis document is corrupt.");
        }

        return destination;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Offline wire envelope. Not registered in production upload or read paths.</summary>
public sealed record PackedDenseAudioDraftEnvelope(
    string Encoding,
    int PointCount,
    int StrideBytes,
    string Checksum,
    string DataBase64);

/// <summary>A single stored F64 measurement, without a materialized object-array timeline.</summary>
public readonly record struct PackedDenseAudioDraftPoint(
    uint TimestampMs,
    int WindowStartSample,
    ushort ValidSamples,
    bool HasBands,
    double RmsLinear,
    double SpectralFluxLinear,
    double BassRms,
    double MidRms,
    double TrebleRms);

/// <summary>
/// Experimental fixed-record codec. Group methods, sample time, coverage, provenance and
/// whole-document budgets still require a separate semantic validator before publication.
/// </summary>
public sealed class PackedDenseAudioDraftCodec
{
    /// <summary>The sole encoding accepted by this offline codec.</summary>
    public const string Encoding = "le-u32-i32-u16-flags-f64x5-v1-draft";

    /// <summary>Little-endian record bytes, without native alignment padding.</summary>
    public const int StrideBytes = 51;

    /// <summary>Count ceiling independent of the complete JSON byte budget.</summary>
    public const int MaximumPoints = 864000;

    private readonly byte[] _records;

    private PackedDenseAudioDraftCodec(byte[] records)
    {
        _records = records;
    }

    /// <summary>Number of validated records.</summary>
    public int Count => _records.Length / StrideBytes;

    /// <summary>
    /// Decodes one bounded payload and validates all rows without allocating point objects.
    /// The caller must bound JSON/Base64 input before constructing the envelope.
    /// </summary>
    public static PackedDenseAudioDraftCodec Decode(
        PackedDenseAudioDraftEnvelope envelope,
        long durationMs,
        int maximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationMs);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPayloadBytes);

        if (envelope.Encoding != Encoding
            || envelope.StrideBytes != StrideBytes
            || envelope.PointCount < 0
            || envelope.PointCount > MaximumPoints)
        {
            throw new InvalidDataException("packed-header-invalid");
        }

        var byteLength = checked(envelope.PointCount * StrideBytes);
        var encodedLength = checked(4 * ((byteLength + 2) / 3));
        if (byteLength > maximumPayloadBytes
            || envelope.DataBase64 is null
            || envelope.DataBase64.Length != encodedLength
            || envelope.Checksum is null
            || envelope.Checksum.Length != 71
            || !envelope.Checksum.StartsWith("sha256:", StringComparison.Ordinal))
        {
            throw new InvalidDataException("packed-length-or-checksum-invalid");
        }

        foreach (var character in envelope.Checksum.AsSpan(7))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                throw new InvalidDataException("packed-checksum-invalid");
            }
        }

        var padding = (3 - (byteLength % 3)) % 3;
        for (var index = 0; index < encodedLength; index++)
        {
            var character = envelope.DataBase64[index];
            var valid = index >= encodedLength - padding
                ? character == '='
                : character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/';
            if (!valid)
            {
                throw new InvalidDataException("packed-base64-invalid");
            }
        }

        var records = new byte[byteLength];
        if (!Convert.TryFromBase64String(envelope.DataBase64, records, out var written)
            || written != byteLength
            || Convert.ToBase64String(records) != envelope.DataBase64)
        {
            throw new InvalidDataException("packed-base64-invalid");
        }

        if (Checksum(records) != envelope.Checksum)
        {
            throw new InvalidDataException("packed-checksum-mismatch");
        }

        var series = new PackedDenseAudioDraftCodec(records);
        long previousTime = -1;
        for (var index = 0; index < series.Count; index++)
        {
            var row = records.AsSpan(index * StrideBytes, StrideBytes);
            var point = series.GetPoint(index);
            if (row[10] > 1
                || point.TimestampMs <= previousTime
                || point.TimestampMs >= durationMs
                || point.ValidSamples < 1
                || point.ValidSamples > Math.Min(2048L, 2048L + point.WindowStartSample)
                || !IsAmplitude(point.RmsLinear)
                || !IsAmplitude(point.SpectralFluxLinear)
                || !IsAmplitude(point.BassRms)
                || !IsAmplitude(point.MidRms)
                || !IsAmplitude(point.TrebleRms))
            {
                throw new InvalidDataException("packed-row-invalid");
            }

            if (!point.HasBands
                && (BinaryPrimitives.ReadInt64LittleEndian(row[27..]) != 0
                    || BinaryPrimitives.ReadInt64LittleEndian(row[35..]) != 0
                    || BinaryPrimitives.ReadInt64LittleEndian(row[43..]) != 0))
            {
                throw new InvalidDataException("packed-absent-bands-must-be-positive-zero");
            }

            previousTime = point.TimestampMs;
        }

        return series;
    }

    /// <summary>Reads one unaligned record. Missing bands remain explicitly unavailable.</summary>
    public PackedDenseAudioDraftPoint GetPoint(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        var row = _records.AsSpan(index * StrideBytes, StrideBytes);
        return new PackedDenseAudioDraftPoint(
            BinaryPrimitives.ReadUInt32LittleEndian(row),
            BinaryPrimitives.ReadInt32LittleEndian(row[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(row[8..]),
            (row[10] & 1) != 0,
            BinaryPrimitives.ReadDoubleLittleEndian(row[11..]),
            BinaryPrimitives.ReadDoubleLittleEndian(row[19..]),
            BinaryPrimitives.ReadDoubleLittleEndian(row[27..]),
            BinaryPrimitives.ReadDoubleLittleEndian(row[35..]),
            BinaryPrimitives.ReadDoubleLittleEndian(row[43..]));
    }

    /// <summary>
    /// Selects intact records by maximum F64 RMS per media-time bucket, earliest ties first.
    /// This only reduces audio payload, not document metadata, events or full-master quality.
    /// </summary>
    public PackedDenseAudioDraftCodec Reduce(int targetIntervalMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetIntervalMs);
        using var output = new MemoryStream();
        long bucket = -1;
        var selected = -1;
        double peak = -1;
        for (var index = 0; index < Count; index++)
        {
            var point = GetPoint(index);
            var nextBucket = point.TimestampMs / (long)targetIntervalMs;
            if (nextBucket != bucket)
            {
                if (selected >= 0)
                {
                    output.Write(_records.AsSpan(selected * StrideBytes, StrideBytes));
                }

                bucket = nextBucket;
                selected = index;
                peak = point.RmsLinear;
            }
            else if (point.RmsLinear > peak)
            {
                selected = index;
                peak = point.RmsLinear;
            }
        }

        if (selected >= 0)
        {
            output.Write(_records.AsSpan(selected * StrideBytes, StrideBytes));
        }

        return new PackedDenseAudioDraftCodec(output.ToArray());
    }

    /// <summary>Exports the same record bytes with a payload-only SHA256 checksum.</summary>
    public PackedDenseAudioDraftEnvelope ToEnvelope() => new(
        Encoding, Count, StrideBytes, Checksum(_records), Convert.ToBase64String(_records));

    private static bool IsAmplitude(double value) => double.IsFinite(value) && value >= 0;

    private static string Checksum(ReadOnlySpan<byte> records) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(records));
}

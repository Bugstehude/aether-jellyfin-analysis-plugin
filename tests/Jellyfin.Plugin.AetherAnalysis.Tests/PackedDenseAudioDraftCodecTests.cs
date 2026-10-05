using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class PackedDenseAudioDraftCodecTests
{
    private const int PayloadLimit = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static JsonDocument Goldens() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "draft", "packed-f64-golden.json")));

    private static PackedDenseAudioDraftEnvelope Envelope(JsonElement element) =>
        element.Deserialize<PackedDenseAudioDraftEnvelope>(Options)!;

    private static PackedDenseAudioDraftEnvelope Measured()
    {
        using var document = Goldens();
        return Envelope(document.RootElement.GetProperty("cases")[0].GetProperty("packed"));
    }

    private static PackedDenseAudioDraftEnvelope WithBytes(
        PackedDenseAudioDraftEnvelope envelope, byte[] bytes) => envelope with
        {
            DataBase64 = Convert.ToBase64String(bytes),
            Checksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))
        };

    [Fact]
    public void ReadsIndependentPythonGoldensWithoutChangingF64BitsOrSelectedRecords()
    {
        using var document = Goldens();
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var envelope = Envelope(testCase.GetProperty("packed"));
            var series = PackedDenseAudioDraftCodec.Decode(
                envelope, testCase.GetProperty("durationMs").GetInt64(), PayloadLimit);
            Assert.Equal(envelope, series.ToEnvelope());
            var points = testCase.GetProperty("points");
            Assert.Equal(points.GetArrayLength(), series.Count);
            for (var index = 0; index < series.Count; index++)
            {
                var expected = points[index];
                var actual = series.GetPoint(index);
                Assert.Equal(expected.GetProperty("timestampMs").GetUInt32(), actual.TimestampMs);
                Assert.Equal(expected.GetProperty("windowStartSample").GetInt32(), actual.WindowStartSample);
                Assert.Equal(expected.GetProperty("validSamples").GetUInt16(), actual.ValidSamples);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.GetProperty("rmsLinear").GetDouble()),
                    BitConverter.DoubleToInt64Bits(actual.RmsLinear));
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected.GetProperty("spectralFluxLinear").GetDouble()),
                    BitConverter.DoubleToInt64Bits(actual.SpectralFluxLinear));
                var hasBands = expected.TryGetProperty("bands", out var bands);
                Assert.Equal(hasBands, actual.HasBands);
                if (hasBands)
                {
                    Assert.Equal(BitConverter.DoubleToInt64Bits(bands.GetProperty("bassRms").GetDouble()),
                        BitConverter.DoubleToInt64Bits(actual.BassRms));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(bands.GetProperty("midRms").GetDouble()),
                        BitConverter.DoubleToInt64Bits(actual.MidRms));
                    Assert.Equal(BitConverter.DoubleToInt64Bits(bands.GetProperty("trebleRms").GetDouble()),
                        BitConverter.DoubleToInt64Bits(actual.TrebleRms));
                }
            }

            foreach (var (detail, interval) in new[] { ("balanced", 50), ("compact", 100) })
            {
                Assert.Equal(Envelope(testCase.GetProperty(detail).GetProperty("packed")),
                    series.Reduce(interval).ToEnvelope());
            }
        }
    }

    [Fact]
    public void RejectsHeaderByteAndHashErrorsBeforeRowsCanBeUsed()
    {
        var envelope = Measured();
        var invalid = new[]
        {
            envelope with { Encoding = "le-u32-i32-u16-flags-f32x5-v1-proposal" },
            envelope with { StrideBytes = 52 },
            envelope with { PointCount = -1 },
            envelope with { PointCount = 864001 },
            envelope with { PointCount = envelope.PointCount + 1 },
            envelope with { DataBase64 = envelope.DataBase64[..^1] },
            envelope with { Checksum = "sha256:" + new string('0', 64) },
            envelope with { Checksum = envelope.Checksum.ToUpperInvariant() }
        };
        foreach (var candidate in invalid)
        {
            Assert.Throws<InvalidDataException>(() => PackedDenseAudioDraftCodec.Decode(candidate, 2000, PayloadLimit));
        }

        Assert.Throws<InvalidDataException>(() => PackedDenseAudioDraftCodec.Decode(envelope, 2000, 5099));
    }

    [Theory]
    [InlineData("flags")]
    [InlineData("valid-samples")]
    [InlineData("duplicate-time")]
    [InlineData("outside-duration")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative-amplitude")]
    [InlineData("absent-bands-nonzero")]
    [InlineData("absent-bands-negative-zero")]
    public void RejectsMalformedRowsEvenWithRecomputedChecksum(string corruption)
    {
        var envelope = Measured();
        var bytes = Convert.FromBase64String(envelope.DataBase64);
        switch (corruption)
        {
            case "flags": bytes[10] = 2; break;
            case "valid-samples": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 2049); break;
            case "duplicate-time":
                bytes.AsSpan(0, 4).CopyTo(bytes.AsSpan(51, 4));
                break;
            case "outside-duration": BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2000); break;
            case "nan": BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(11), double.NaN); break;
            case "infinity": BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(19), double.PositiveInfinity); break;
            case "negative-amplitude": BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(27), -0.1); break;
            case "absent-bands-nonzero": bytes[10] = 0; break;
            case "absent-bands-negative-zero":
                bytes[10] = 0;
                bytes.AsSpan(27, 24).Clear();
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(27), long.MinValue);
                break;
        }

        Assert.Throws<InvalidDataException>(() =>
            PackedDenseAudioDraftCodec.Decode(WithBytes(envelope, bytes), 2000, PayloadLimit));
    }

    [Fact]
    public void RejectsBase64WhitespaceAndUnexpectedPadding()
    {
        using var document = Goldens();
        var envelope = Envelope(document.RootElement.GetProperty("cases")[1].GetProperty("packed"));
        // Every complete 51-byte record ends on a Base64 triplet boundary.
        var bytes = Convert.FromBase64String(envelope.DataBase64).AsSpan(0, 51).ToArray();
        var one = WithBytes(envelope with { PointCount = 1 }, bytes);
        Assert.Throws<InvalidDataException>(() => PackedDenseAudioDraftCodec.Decode(
            one with { DataBase64 = " " + one.DataBase64[1..] }, 100, PayloadLimit));
        // Record size 51 is divisible by three, so only exact alphabet and length are accepted.
        Assert.Throws<InvalidDataException>(() => PackedDenseAudioDraftCodec.Decode(
            one with { DataBase64 = one.DataBase64[..^1] + "=" }, 100, PayloadLimit));
    }
}

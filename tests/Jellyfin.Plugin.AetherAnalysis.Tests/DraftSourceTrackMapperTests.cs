using System.Text;
using Jellyfin.Plugin.AetherAnalysis.Application;
using Jellyfin.Plugin.AetherAnalysis.Application.Draft;

namespace Jellyfin.Plugin.AetherAnalysis.Tests;

public sealed class DraftSourceTrackMapperTests
{
    [Fact]
    public void MapsExactSparseGlobalIndexesDespiteDifferentStreamOrder()
    {
        var host = Host(
            Stream(5, "Audio", "aac", 2, 48000),
            Stream(0, "Video", null, null, null),
            Stream(2, "Audio", "ac3", 6, 48000));

        var result = DraftSourceTrackMapper.Select(host, Probe(
            ProbeStream(2, "audio", "ac3", 6, 48000),
            ProbeStream(0, "video"),
            ProbeStream(5, "audio", "AAC", 2, 48000)), requestedFfmpegStreamIndex: 2);

        Assert.Equal(new DraftTrackSelection(2, 2), result);
    }

    [Fact]
    public void AcceptsFfprobeDecimalStringSampleRateAndOrdinarySubtitleStreams()
    {
        var host = Host(
            Stream(0, "Video", null, null, null),
            Stream(1, "Audio", "aac", 2, 22050),
            Stream(2, "Subtitle", null, null, null, external: true));
        var probe = Encoding.UTF8.GetBytes(
            "{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},"
            + "{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"sample_rate\":\"22050\"},"
            + "{\"index\":2,\"codec_type\":\"subtitle\"},"
            + "{\"index\":3,\"codec_type\":\"data\"},"
            + "{\"index\":4,\"codec_type\":\"attachment\"}]}");

        Assert.Equal(new DraftTrackSelection(1, 1), DraftSourceTrackMapper.Select(host, probe));
    }

    [Fact]
    public void DoesNotMapByAudioOrdinalWhenGlobalIndexesDiffer()
    {
        var host = Host(
            Stream(0, "Video", null, null, null),
            Stream(1, "Audio", "aac", 2, 48000),
            Stream(2, "Audio", "ac3", 6, 48000));

        var error = Assert.Throws<InvalidDataException>(() => DraftSourceTrackMapper.Select(host, Probe(
            ProbeStream(0, "video"),
            ProbeStream(2, "audio", "aac", 2, 48000),
            ProbeStream(5, "audio", "ac3", 6, 48000))));

        Assert.Equal("source-track-audio-list-mismatch", error.Message);
    }

    [Fact]
    public void RejectsAnyAudioIndexSetMismatch()
    {
        var host = Host(Stream(0, "Video", null, null, null), Stream(2, "Audio", "aac", 2, 48000));
        var error = Assert.Throws<InvalidDataException>(() => DraftSourceTrackMapper.Select(host, Probe(
            ProbeStream(0, "video"), ProbeStream(3, "audio", "aac", 2, 48000))));
        Assert.Equal("source-track-audio-list-mismatch", error.Message);
    }

    [Theory]
    [InlineData("codec", 2, 48000, "ac3")]
    [InlineData("channels", 6, 48000, "aac")]
    [InlineData("sample-rate", 2, 44100, "aac")]
    public void RejectsAudioMetadataMismatch(string _, int channels, int sampleRate, string codec)
    {
        var host = Host(Stream(0, "Video", null, null, null), Stream(2, "Audio", "aac", 2, 48000));
        var error = Assert.Throws<InvalidDataException>(() => DraftSourceTrackMapper.Select(host, Probe(
            ProbeStream(0, "video"), ProbeStream(2, "audio", codec, channels, sampleRate))));
        Assert.Equal("source-track-audio-metadata-mismatch", error.Message);
    }

    [Fact]
    public void UsesHostDefaultIndexOnlyWhenItIsADeclaredVerifiedAudioStream()
    {
        var host = Host(
            [Stream(0, "Video", null, null, null), Stream(2, "Audio", "aac", 2, 48000), Stream(5, "Audio", "ac3", 6, 48000)],
            defaultAudio: 5);
        var probe = Probe(ProbeStream(0, "video"), ProbeStream(2, "audio", "aac", 2, 48000), ProbeStream(5, "audio", "ac3", 6, 48000));

        Assert.Equal(new DraftTrackSelection(5, 5), DraftSourceTrackMapper.Select(host, probe));
        Assert.Equal(new DraftTrackSelection(2, 2), DraftSourceTrackMapper.Select(host, probe, 2));
    }

    [Fact]
    public void RejectsAmbiguousMultiAudioWithoutRequestOrHostDefault()
    {
        var host = Host(Stream(0, "Video", null, null, null), Stream(2, "Audio", "aac", 2, 48000), Stream(5, "Audio", "ac3", 6, 48000));
        var error = Assert.Throws<InvalidDataException>(() => DraftSourceTrackMapper.Select(host, Probe(
            ProbeStream(0, "video"), ProbeStream(2, "audio", "aac", 2, 48000), ProbeStream(5, "audio", "ac3", 6, 48000))));
        Assert.Equal("source-track-audio-selection-ambiguous", error.Message);
    }

    [Fact]
    public void RejectsUnknownRequestedOrDefaultAudioIndexes()
    {
        var probe = Probe(ProbeStream(0, "video"), ProbeStream(2, "audio", "aac", 2, 48000));
        var host = Host(Stream(0, "Video", null, null, null), Stream(2, "Audio", "aac", 2, 48000));
        Assert.Equal("source-track-requested-audio-unknown", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, probe, 5)).Message);
        Assert.Equal("source-track-default-audio-unknown", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host([.. host.Streams], 5), probe)).Message);
    }

    [Fact]
    public void NoTrackRequiresExplicitCompleteEmptyAudioListingsAndVideoOnBothSides()
    {
        var host = Host(Stream(0, "Video", null, null, null));
        Assert.Equal(new DraftTrackSelection(null, null), DraftSourceTrackMapper.Select(host, Probe(ProbeStream(0, "video"))));

        Assert.Equal("source-track-audio-list-mismatch", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, Probe(ProbeStream(0, "video"), ProbeStream(1, "audio", "aac", 2, 48000)))).Message);
        Assert.Equal("source-track-video-required", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host(Array.Empty<DraftHostStreamDescriptor>()), Probe(ProbeStream(0, "video")))).Message);
        Assert.Equal("source-track-external-audio", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host(Stream(0, "Video", null, null, null), Stream(1, "Audio", "aac", 2, 48000, external: true)), Probe(ProbeStream(0, "video")))).Message);
    }

    [Fact]
    public void RejectsMissingHostStreamListInsteadOfTreatingItAsNoTrack()
    {
        var host = Host(Stream(0, "Video", null, null, null)) with { Streams = null! };
        var error = Assert.Throws<InvalidDataException>(() => DraftSourceTrackMapper.Select(host, Probe(ProbeStream(0, "video"))));
        Assert.Equal("source-track-source-metadata", error.Message);
    }

    [Fact]
    public void RejectsDuplicateNegativeAndExcessiveHostIndexes()
    {
        var probe = Probe(ProbeStream(0, "video"));
        Assert.Equal("source-track-host-stream-invalid", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host(Stream(0, "Video", null, null, null), Stream(0, "Audio", "aac", 2, 48000)), probe)).Message);
        Assert.Equal("source-track-host-stream-invalid", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host(Stream(-1, "Video", null, null, null)), probe)).Message);
        var excessive = Enumerable.Range(0, DraftSourceTrackMapper.MaxStreams + 1)
            .Select(index => Stream(index, "Video", null, null, null)).ToArray();
        Assert.Equal("source-track-host-stream-limit", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(Host(excessive), probe)).Message);
    }

    [Fact]
    public void RejectsProbeDuplicatesMalformedStreamsAndMalformedAudioNumbers()
    {
        var host = Host(Stream(0, "Video", null, null, null));
        AssertProbeError(host, "{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},{\"index\":0,\"codec_type\":\"video\"}]}", "source-track-probe-stream-invalid");
        AssertProbeError(host, "{\"streams\":[{\"index\":0,\"index\":1,\"codec_type\":\"video\"}]}", "source-track-probe-duplicate-key");
        AssertProbeError(host, "{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":\"2\",\"sample_rate\":\"48000\"}]}", "source-track-probe-audio-metadata");
        AssertProbeError(host, "{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":0,\"sample_rate\":48000}]}", "source-track-probe-audio-metadata");
        foreach (var sampleRate in new[] { "\"02\"", "\"2.5\"", "\"2e0\"", "\"+2\"", "\"0\"", "48000.0", "4.8e4" })
        {
            AssertProbeError(host,
                "{\"streams\":[{\"index\":0,\"codec_type\":\"video\"},{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"sample_rate\":" + sampleRate + "}]}",
                "source-track-probe-audio-metadata");
        }
        AssertProbeError(host, "{\"streams\":[{\"index\":0,\"codec_type\":\"unknown\"}]}", "source-track-probe-stream-invalid");
    }

    [Fact]
    public void RejectsOverLimitAndDeeplyNestedProbeJsonWithStableErrors()
    {
        var host = Host(Stream(0, "Video", null, null, null));
        var tooLarge = Encoding.UTF8.GetBytes("{" + new string(' ', DraftSourceTrackMapper.MaxProbeBytes) + "}");
        Assert.Equal("source-track-probe-size", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, tooLarge)).Message);
        Assert.Equal("source-track-probe-json", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, Encoding.UTF8.GetBytes("{\"streams\":[" + new string('[', 20) + "0" + new string(']', 20) + "]}"))).Message);
        Assert.Equal("source-track-probe-json", Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, Encoding.UTF8.GetBytes("{\"streams\":["))).Message);
    }

    private static DraftHostSource Host(params DraftHostStreamDescriptor[] streams) => Host(streams, null);

    private static DraftHostSource Host(IReadOnlyList<DraftHostStreamDescriptor> streams, int? defaultAudio = null) =>
        new(new MediaFingerprint(Guid.NewGuid(), "source", "sha256:identity", "strong", 1000), "/not-used", "streams-v1", streams, defaultAudio);

    private static DraftHostStreamDescriptor Stream(int index, string type, string? codec, int? channels, int? sampleRate, bool external = false) =>
        new(index, type, codec, channels, sampleRate, false, external);

    private static byte[] Probe(params string[] streams) => Encoding.UTF8.GetBytes("{\"streams\":[" + string.Join(',', streams) + "]}");

    private static string ProbeStream(int index, string type, string? codec = null, int? channels = null, int? sampleRate = null)
    {
        var value = $"{{\"index\":{index},\"codec_type\":\"{type}\"";
        if (codec is not null)
        {
            value += $",\"codec_name\":\"{codec}\",\"channels\":{channels},\"sample_rate\":{sampleRate}";
        }

        return value + "}";
    }

    private static void AssertProbeError(DraftHostSource host, string json, string expected) =>
        Assert.Equal(expected, Assert.Throws<InvalidDataException>(
            () => DraftSourceTrackMapper.Select(host, Encoding.UTF8.GetBytes(json))).Message);
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AetherAnalysis.Contracts;

/// <summary>Canonical schema version 2 upload document.</summary>
public sealed class AnalysisUploadV2
{
    /// <summary>Gets the document schema version.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Gets the producer creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the media duration in milliseconds.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Gets sampling metadata.</summary>
    public required SamplingV2 Sampling { get; init; }

    /// <summary>Gets producer metadata.</summary>
    public required ProducerV2 Producer { get; init; }

    /// <summary>Gets the server fingerprint observed before analysis began.</summary>
    public required string MediaFingerprintAtStart { get; init; }

    /// <summary>Gets the optional client content fingerprint.</summary>
    public ClientContentFingerprintV2? ClientContentFingerprint { get; init; }

    /// <summary>Gets visual feature frames.</summary>
    public required IReadOnlyList<AnalysisFrameV2> Frames { get; init; }

    /// <summary>Gets optional dense audio frames.</summary>
    public IReadOnlyList<AudioFrameV2>? AudioFrames { get; init; }

    /// <summary>Gets compatible extension data.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>Sampling metadata.</summary>
public sealed class SamplingV2
{
    /// <summary>Gets the nominal interval.</summary>
    public required int IntervalMs { get; init; }

    /// <summary>Gets the sampled frame width.</summary>
    public required int FrameWidth { get; init; }

    /// <summary>Gets the sampled frame height.</summary>
    public required int FrameHeight { get; init; }

    /// <summary>Gets the color space.</summary>
    public required string ColorSpace { get; init; }
}

/// <summary>Producer metadata.</summary>
public sealed class ProducerV2
{
    /// <summary>Gets the producer name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the producer version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the producer platform.</summary>
    public required string Platform { get; init; }
}

/// <summary>Optional client-side sparse content fingerprint.</summary>
public sealed class ClientContentFingerprintV2
{
    /// <summary>Gets the fingerprint algorithm.</summary>
    public required string Algorithm { get; init; }

    /// <summary>Gets the fingerprint value.</summary>
    public required string Value { get; init; }
}

/// <summary>One visual feature frame.</summary>
public sealed class AnalysisFrameV2
{
    /// <summary>Gets the timestamp.</summary>
    public required long TimestampMs { get; init; }

    /// <summary>Gets normalized luminance.</summary>
    public required double Luminance { get; init; }

    /// <summary>Gets normalized contrast.</summary>
    public required double Contrast { get; init; }

    /// <summary>Gets normalized saturation.</summary>
    public required double Saturation { get; init; }

    /// <summary>Gets normalized motion energy.</summary>
    public required double MotionEnergy { get; init; }

    /// <summary>Gets normalized scene-cut probability.</summary>
    public required double SceneCutProbability { get; init; }

    /// <summary>Gets optional frame-synchronous audio metrics.</summary>
    public AudioMetricsV2? Audio { get; init; }

    /// <summary>Gets up to five palette colors.</summary>
    public required IReadOnlyList<PaletteColorV2> Palette { get; init; }

    /// <summary>Gets compatible extension data.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>Normalized raw audio metrics.</summary>
public class AudioMetricsV2
{
    /// <summary>Gets normalized root mean square energy.</summary>
    public required double Rms { get; init; }

    /// <summary>Gets normalized spectral flux.</summary>
    public required double Flux { get; init; }
}

/// <summary>Dense audio feature frame.</summary>
public sealed class AudioFrameV2 : AudioMetricsV2
{
    /// <summary>Gets the timestamp.</summary>
    public required long TimestampMs { get; init; }
}

/// <summary>Palette color and coverage.</summary>
public sealed class PaletteColorV2
{
    /// <summary>Gets red.</summary>
    public required int Red { get; init; }

    /// <summary>Gets green.</summary>
    public required int Green { get; init; }

    /// <summary>Gets blue.</summary>
    public required int Blue { get; init; }

    /// <summary>Gets normalized coverage.</summary>
    public required double Coverage { get; init; }
}

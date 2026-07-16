using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AetherAnalysis.Contracts;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>Validates schema and cross-field invariants before persistence.</summary>
public sealed partial class AnalysisDocumentValidator
{
    private const int MaximumFrames = 86400;
    private const int MaximumAudioFrames = 864000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Validates untrusted upload JSON.</summary>
    public ValidationResult Validate(JsonElement element, int maxUploadBytes)
    {
        var rawBytes = JsonSerializer.SerializeToUtf8Bytes(element, SerializerOptions);
        if (rawBytes.Length > maxUploadBytes)
        {
            return ValidationResult.Invalid("payload-too-large", "Upload exceeds the configured uncompressed size limit.");
        }

        AnalysisUploadV2? value;
        try
        {
            value = element.Deserialize<AnalysisUploadV2>(SerializerOptions);
        }
        catch (JsonException exception)
        {
            return ValidationResult.Invalid("analysis-invalid", exception.Message);
        }

        if (value is null)
        {
            return ValidationResult.Invalid("analysis-invalid", "Upload body is empty.");
        }

        var error = ValidateValue(value);
        if (error is not null)
        {
            return ValidationResult.Invalid("analysis-invalid", error);
        }

        return ValidationResult.Valid(value, JsonNode.Parse(element.GetRawText())!.AsObject(), rawBytes.Length);
    }

    private static string? ValidateValue(AnalysisUploadV2 value)
    {
        if (value.SchemaVersion != 2)
        {
            return "schemaVersion must equal 2.";
        }

        if (value.DurationMs <= 0)
        {
            return "durationMs must be positive.";
        }

        if (value.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return "createdAt is too far in the future.";
        }

        if (value.Sampling.IntervalMs is < 250 or > 10000
            || value.Sampling.FrameWidth is < 16 or > 1920
            || value.Sampling.FrameHeight is < 16 or > 1080
            || !string.Equals(value.Sampling.ColorSpace, "srgb", StringComparison.Ordinal))
        {
            return "sampling is outside contract limits.";
        }

        if (value.Producer.Name.Length is < 1 or > 64
            || value.Producer.Version.Length is < 1 or > 32
            || value.Producer.Platform is not ("browser" or "server"))
        {
            return "producer is invalid.";
        }

        if (!Sha256Fingerprint().IsMatch(value.MediaFingerprintAtStart)
            || (value.ClientContentFingerprint is not null
                && (!Sha256Fingerprint().IsMatch(value.ClientContentFingerprint.Value)
                    || value.ClientContentFingerprint.Algorithm.Length is < 1 or > 64)))
        {
            return "fingerprint format is invalid.";
        }

        if (value.Frames.Count is < 1 or > MaximumFrames)
        {
            return "frames count is outside contract limits.";
        }

        long previousTimestamp = -1;
        foreach (var frame in value.Frames)
        {
            if (frame.TimestampMs <= previousTimestamp || frame.TimestampMs > value.DurationMs + value.Sampling.IntervalMs)
            {
                return "frame timestamps must be strictly increasing and within duration.";
            }

            previousTimestamp = frame.TimestampMs;
            if (!IsUnit(frame.Luminance)
                || !IsUnit(frame.Contrast)
                || !IsUnit(frame.Saturation)
                || !IsUnit(frame.MotionEnergy)
                || !IsUnit(frame.SceneCutProbability)
                || (frame.Audio is not null && (!IsUnit(frame.Audio.Rms) || !IsUnit(frame.Audio.Flux))))
            {
                return "frame features must be finite numbers in [0, 1].";
            }

            if (frame.Palette.Count > 5
                || frame.Palette.Any(color => color.Red is < 0 or > 255
                    || color.Green is < 0 or > 255
                    || color.Blue is < 0 or > 255
                    || !IsUnit(color.Coverage))
                || frame.Palette.Sum(color => color.Coverage) > 1.01)
            {
                return "palette is outside contract limits.";
            }
        }

        if (value.AudioFrames is { Count: > MaximumAudioFrames })
        {
            return "audioFrames count exceeds contract limits.";
        }

        previousTimestamp = -1;
        foreach (var frame in value.AudioFrames ?? [])
        {
            if (frame.TimestampMs <= previousTimestamp
                || frame.TimestampMs > value.DurationMs + value.Sampling.IntervalMs
                || !IsUnit(frame.Rms)
                || !IsUnit(frame.Flux))
            {
                return "audioFrames must be normalized, increasing and within duration.";
            }

            previousTimestamp = frame.TimestampMs;
        }

        return null;
    }

    private static bool IsUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Fingerprint();
}

/// <summary>Result of upload validation.</summary>
public sealed record ValidationResult(
    bool IsValid,
    string? Code,
    string? Error,
    AnalysisUploadV2? Value,
    JsonObject? Json,
    int UncompressedBytes)
{
    /// <summary>Creates a valid result.</summary>
    public static ValidationResult Valid(AnalysisUploadV2 value, JsonObject json, int bytes) =>
        new(true, null, null, value, json, bytes);

    /// <summary>Creates an invalid result.</summary>
    public static ValidationResult Invalid(string code, string error) =>
        new(false, code, error, null, null, 0);
}

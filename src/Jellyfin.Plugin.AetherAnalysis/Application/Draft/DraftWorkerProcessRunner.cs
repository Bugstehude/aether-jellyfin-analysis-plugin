using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Explicit experimental settings. The production worker is never selected implicitly.</summary>
public sealed record DraftWorkerSettings(
    string NodePath,
    string WorkerPath,
    string WorkerSha256,
    string ProducerRevision,
    string FfmpegPath,
    string FfprobePath,
    bool Enabled = false,
    int MaximumDocumentBytes = 50 * 1024 * 1024,
    long MaximumWorkerRssBytes = 512L * 1024 * 1024,
    int Fps = 2,
    int Width = 480,
    TimeSpan? Timeout = null,
    long MaximumStoredBytes = 10L * 1024 * 1024 * 1024,
    string TargetVersion = DraftAnalysisMasterBuilder.AlgorithmVersion,
    int FfmpegThreads = 0);

/// <summary>Probe and fresh Full-component execution for the experimental server adapter.</summary>
public interface IDraftWorkerProcessRunner
{
    /// <summary>Obtains a bounded, complete stream listing with the host's FFprobe executable.</summary>
    Task<byte[]> ProbeAsync(DraftHostSource source, DraftWorkerSettings settings, CancellationToken cancellationToken);

    /// <summary>Produces one private Full component using the captured host snapshot.</summary>
    Task<string> ProduceAsync(DraftHostSource source, DraftTrackSelection tracks, JsonElement snapshot,
        string mode, string directory, DraftWorkerSettings settings, CancellationToken cancellationToken);
}

/// <summary>Shell-free invocation of an explicitly pinned standalone draft CLI.</summary>
public sealed class DraftWorkerProcessRunner : IDraftWorkerProcessRunner
{
    /// <summary>Validates limits and executable configuration before any process can run.</summary>
    public static void ValidateSettings(DraftWorkerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            throw new InvalidDataException("draft-execution-disabled");
        }

        if (settings.FfmpegThreads is < 0 or > 16 || settings.TargetVersion is not ("1.2.0-draft" or "1.2.0")
            || string.IsNullOrWhiteSpace(settings.NodePath) || string.IsNullOrWhiteSpace(settings.FfmpegPath)
            || string.IsNullOrWhiteSpace(settings.FfprobePath) || !Path.IsPathFullyQualified(settings.WorkerPath)
            || !IsDigest(settings.WorkerSha256) || settings.ProducerRevision.Length != 71
            || !settings.ProducerRevision.StartsWith("sha256:", StringComparison.Ordinal)
            || !IsDigest(settings.ProducerRevision[7..])
            || settings.MaximumDocumentBytes is <= 8192 or > 50 * 1024 * 1024
            || settings.MaximumWorkerRssBytes <= 0 || settings.Fps is < 1 or > 4
            || settings.Width is < 16 or > 1920 || settings.MaximumStoredBytes <= 0
            || (settings.Timeout ?? TimeSpan.FromMinutes(60)) <= TimeSpan.Zero
            || (settings.Timeout ?? TimeSpan.FromMinutes(60)) > TimeSpan.FromHours(12))
        {
            throw new InvalidDataException("draft-execution-settings");
        }
    }

    /// <summary>Copies and verifies a bounded bundle into private job storage before executing it.</summary>
    public static async Task<DraftWorkerSettings> PinBundleAsync(
        DraftWorkerSettings settings, string directory, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        var path = Path.Combine(directory, "pinned-draft-worker.cjs");
        await using var input = new FileStream(settings.WorkerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length is 0 or > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("draft-worker-bundle-budget");
        }

        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous))
        {
            var buffer = new byte[64 * 1024];
            int read;
            long count = 0;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                count += read;
                if (count > 32 * 1024 * 1024)
                {
                    throw new InvalidDataException("draft-worker-bundle-budget");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        await using var pinned = File.OpenRead(path);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(pinned, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(digest, settings.WorkerSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("draft-worker-bundle-digest");
        }

        return settings with { WorkerPath = path };
    }

    /// <inheritdoc />
    public async Task<byte[]> ProbeAsync(DraftHostSource source, DraftWorkerSettings settings, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        var result = await DraftBoundedProcess.RunAsync(new DraftProcessRequest(settings.FfprobePath,
            ["-v", "error", "-show_entries", "stream=index,codec_type,codec_name,channels,sample_rate", "-of", "json", source.InputPath],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(30), DraftSourceTrackMapper.MaxProbeBytes, 64 * 1024,
            settings.MaximumWorkerRssBytes), cancellationToken).ConfigureAwait(false);
        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task<string> ProduceAsync(
        DraftHostSource source, DraftTrackSelection tracks, JsonElement snapshot, string mode,
        string directory, DraftWorkerSettings settings, CancellationToken cancellationToken)
    {
        ValidateSettings(settings);
        if (mode is not ("audio-only" or "video-only"))
        {
            throw new InvalidDataException("draft-worker-mode");
        }

        var output = Path.Combine(directory, mode + ".json");
        var snapshotPath = Path.Combine(directory, mode + "-snapshot.json");
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (snapshotBytes.Length > 16 * 1024)
        {
            throw new InvalidDataException("draft-worker-snapshot-budget");
        }

        await File.WriteAllBytesAsync(snapshotPath, snapshotBytes, cancellationToken).ConfigureAwait(false);
        var timeout = settings.Timeout ?? TimeSpan.FromMinutes(60);
        var args = new List<string>
        {
            settings.WorkerPath, "--input", source.InputPath, "--out", output, "--mode", mode,
            "--snapshot", snapshotPath, "--producer-revision", settings.ProducerRevision,
            "--format", "artifact", "--detail", "full", "--duration-ms", Number(source.Media.DurationMs),
            "--fps", Number(settings.Fps), "--width", Number(settings.Width),
            "--max-bytes", Number(settings.MaximumDocumentBytes), "--max-wall-ms", Number((long)timeout.TotalMilliseconds),
            "--max-rss-bytes", Number(settings.MaximumWorkerRssBytes)
        };
        if (tracks.FfmpegStreamIndex is { } ffmpegIndex)
        {
            args.AddRange(["--stream-index", Number(ffmpegIndex)]);
        }

        if (tracks.JellyfinStreamIndex is { } jellyfinIndex)
        {
            args.AddRange(["--jellyfin-stream-index", Number(jellyfinIndex)]);
        }

        var environment = new Dictionary<string, string>
        {
            ["AETHER_FFMPEG"] = settings.FfmpegPath,
            ["AETHER_FFPROBE"] = settings.FfprobePath,
            ["AETHER_FFMPEG_THREADS"] = Number(settings.FfmpegThreads),
            ["TMPDIR"] = directory,
            ["TMP"] = directory,
            ["TEMP"] = directory
        };
        await DraftBoundedProcess.RunAsync(new DraftProcessRequest(settings.NodePath, args, environment,
            timeout, 64 * 1024, 64 * 1024, settings.MaximumWorkerRssBytes, output, settings.MaximumDocumentBytes),
            cancellationToken).ConfigureAwait(false);
        var file = new FileInfo(output);
        if (!file.Exists || file.Length is 0 || file.Length > settings.MaximumDocumentBytes
            || file.LinkTarget is not null)
        {
            throw new InvalidDataException("draft-worker-output");
        }

        return output;
    }

    private static bool IsDigest(string value) => value.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

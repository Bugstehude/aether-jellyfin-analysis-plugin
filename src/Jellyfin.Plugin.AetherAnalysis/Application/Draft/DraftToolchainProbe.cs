using System.Collections.Concurrent;
using System.Text;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Actual FFmpeg/FFprobe executables handed to the worker, as reported by themselves.</summary>
/// <param name="FfmpegPath">Configured FFmpeg path.</param>
/// <param name="FfmpegVersion">First line of <c>ffmpeg -version</c>, or an unavailable marker.</param>
/// <param name="FfprobePath">Configured FFprobe path.</param>
/// <param name="FfprobeVersion">First line of <c>ffprobe -version</c>, or an unavailable marker.</param>
public sealed record DraftToolchain(string FfmpegPath, string FfmpegVersion, string FfprobePath, string FfprobeVersion);

/// <summary>
/// Diagnostic only: records which binaries a server run actually used. The result never
/// gates analysis, enters a document or replaces the worker's own validation.
/// </summary>
public static class DraftToolchainProbe
{
    private const int MaximumLineLength = 200;
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> Cache = new();

    /// <summary>Describes both configured executables. Failures yield a marker instead of an exception.</summary>
    public static async Task<DraftToolchain> DescribeAsync(DraftWorkerSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ffmpeg = await VersionAsync(settings.FfmpegPath, cancellationToken).ConfigureAwait(false);
        var ffprobe = await VersionAsync(settings.FfprobePath, cancellationToken).ConfigureAwait(false);
        return new DraftToolchain(settings.FfmpegPath, ffmpeg, settings.FfprobePath, ffprobe);
    }

    /// <summary>Runs <c>executable -version</c> once per unchanged file and returns its first line.</summary>
    public static async Task<string> VersionAsync(string executable, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return "unavailable (not configured)";
        }

        (string, long, DateTime)? key = null;
        try
        {
            var file = new FileInfo(executable);
            if (file.Exists)
            {
                key = (file.FullName, file.Length, file.LastWriteTimeUtc);
                if (Cache.TryGetValue(key.Value, out var cached))
                {
                    return cached;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            // An unreadable path is still attempted; the operating system reports the real failure.
        }

        string version;
        try
        {
            var result = await DraftBoundedProcess.RunAsync(new DraftProcessRequest(executable, ["-version"],
                new Dictionary<string, string>(), TimeSpan.FromSeconds(15), 64 * 1024, 16 * 1024,
                256L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
            version = FirstLine(result.StandardOutput) ?? "unavailable (empty version output)";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return "unavailable (" + exception.GetType().Name + ")";
        }

        if (key is { } stored)
        {
            Cache[stored] = version;
        }

        return version;
    }

    /// <summary>First non-empty line, restricted to printable ASCII and bounded in length.</summary>
    public static string? FirstLine(ReadOnlySpan<byte> output)
    {
        var text = Encoding.UTF8.GetString(output);
        foreach (var raw in text.Split('\n'))
        {
            var builder = new StringBuilder(Math.Min(raw.Length, MaximumLineLength));
            foreach (var character in raw.Trim())
            {
                if (builder.Length == MaximumLineLength)
                {
                    break;
                }

                builder.Append(character is >= ' ' and <= '~' ? character : '?');
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        return null;
    }
}

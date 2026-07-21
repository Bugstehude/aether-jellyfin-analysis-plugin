using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AetherAnalysis.Application;

/// <summary>
/// Runs the vendored Node worker bundle (<c>aether-analysis-worker.cjs</c>) as a
/// child process to produce a schema-v2 analysis document for one local media file.
/// This is the exact same shared perception-engine the browser client runs — the
/// worker is bundled from the AETHER monorepo — so server and client analyses are
/// algorithmically identical, with no HTTP round-trip and no C# reimplementation.
/// </summary>
public sealed class ServerAnalysisWorkerRunner(
    IMediaEncoder mediaEncoder,
    ILogger<ServerAnalysisWorkerRunner> logger)
{
    private const string WorkerFileName = "aether-analysis-worker.cjs";

    /// <summary>Resolved path to the vendored worker bundle next to this plugin's assembly.</summary>
    public string WorkerPath { get; } = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory,
        WorkerFileName);

    /// <summary>True when Node and the worker bundle are both resolvable.</summary>
    public bool IsAvailable => File.Exists(WorkerPath);

    /// <summary>
    /// Analyzes <paramref name="inputPath"/> and returns the raw schema-v2 document JSON on
    /// stdout. Throws <see cref="ServerAnalysisWorkerException"/> on any worker failure.
    /// </summary>
    public async Task<string> AnalyzeAsync(
        string inputPath,
        int fps,
        int maxWidth,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new ServerAnalysisWorkerException($"Worker bundle not found at '{WorkerPath}'.");
        }

        var nodePath = Plugin.Instance?.Configuration.NodePath;
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            nodePath = "node";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(WorkerPath);
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(inputPath);
        startInfo.ArgumentList.Add("--fps");
        startInfo.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--width");
        startInfo.ArgumentList.Add(maxWidth.ToString(CultureInfo.InvariantCulture));

        // Hand the worker Jellyfin's bundled ffmpeg/ffprobe so no separate install is needed.
        var encoderPath = mediaEncoder.EncoderPath;
        var probePath = mediaEncoder.ProbePath;
        if (!string.IsNullOrWhiteSpace(encoderPath))
        {
            startInfo.Environment["AETHER_FFMPEG"] = encoderPath;
        }

        if (!string.IsNullOrWhiteSpace(probePath))
        {
            startInfo.Environment["AETHER_FFPROBE"] = probePath;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new ServerAnalysisWorkerException($"Failed to start Node process '{nodePath}'.");
            }
        }
        catch (Exception exception) when (exception is not ServerAnalysisWorkerException)
        {
            throw new ServerAnalysisWorkerException(
                $"Could not launch the analysis worker via '{nodePath}'. Set the Node path in the plugin settings.",
                exception);
        }

        TryLowerPriority(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = ConsumeStderrAsync(process, progress, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderrTail = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new ServerAnalysisWorkerException(
                $"Analysis worker exited {process.ExitCode.ToString(CultureInfo.InvariantCulture)}: {stderrTail}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new ServerAnalysisWorkerException("Analysis worker produced no document on stdout.");
        }

        progress?.Report(1.0);
        return stdout;
    }

    private async Task<string> ConsumeStderrAsync(
        Process process,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Keep the last few lines for error context; parse PROGRESS lines for percentage.
        var tail = new Queue<string>(8);
        while (true)
        {
            string? line;
            try
            {
                line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal)
                && progress is not null
                && double.TryParse(
                    line.AsSpan(9),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var fraction))
            {
                progress.Report(Math.Clamp(fraction, 0, 1));
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (tail.Count == 8)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return string.Join(" | ", tail);
    }

    private void TryLowerPriority(Process process)
    {
        // Analysis is a low-priority background job; never let it starve playback.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not lower analysis worker process priority");
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not kill cancelled analysis worker process");
        }
    }
}

/// <summary>Raised when the worker subprocess cannot produce a document.</summary>
public sealed class ServerAnalysisWorkerException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ServerAnalysisWorkerException"/> class.</summary>
    public ServerAnalysisWorkerException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ServerAnalysisWorkerException"/> class.</summary>
    public ServerAnalysisWorkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

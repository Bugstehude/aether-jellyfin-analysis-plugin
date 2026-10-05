using System.Diagnostics;
using System.Text;

namespace Jellyfin.Plugin.AetherAnalysis.Application.Draft;

/// <summary>Describes a bounded, shell-free child process used by offline draft work.</summary>
/// <param name="Executable">Executable path or name resolved by the operating system.</param>
/// <param name="Arguments">Arguments passed directly to the executable.</param>
/// <param name="Environment">Environment variables added to or replacing inherited variables.</param>
/// <param name="Timeout">Maximum allowed process runtime.</param>
/// <param name="MaxOutputBytes">Maximum number of captured stdout bytes.</param>
/// <param name="MaxErrorBytes">Maximum number of captured stderr bytes.</param>
/// <param name="MaxProcessRssBytes">Maximum child working set, or zero to disable this check.</param>
/// <param name="OutputFilePath">Optional output file monitored while the process runs.</param>
/// <param name="MaxOutputFileBytes">Maximum output file size when a path is supplied.</param>
public sealed record DraftProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    int MaxOutputBytes,
    int MaxErrorBytes,
    long MaxProcessRssBytes,
    string? OutputFilePath = null,
    int? MaxOutputFileBytes = null);

/// <summary>Result of a completed bounded child process.</summary>
/// <param name="StandardOutput">Captured stdout bytes.</param>
/// <param name="StandardError">Captured stderr decoded as UTF-8.</param>
public sealed record DraftProcessResult(byte[] StandardOutput, string StandardError);

/// <summary>Runs draft helper processes without a shell and enforces resource limits.</summary>
public static class DraftBoundedProcess
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Runs a child process with bounded pipes, runtime, memory, and optional file output.</summary>
    /// <param name="request">Process and resource limit settings.</param>
    /// <param name="cancellationToken">Cancels the process and its entire process tree.</param>
    /// <returns>The captured stdout and bounded UTF-8 stderr.</returns>
    /// <exception cref="InvalidDataException">A configured process budget was exceeded or the process failed.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
    public static async Task<DraftProcessResult> RunAsync(
        DraftProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        process = new Process();
        try
        {
            process.StartInfo = CreateStartInfo(request);
            if (!process.Start())
            {
                throw Failure("draft-process-start");
            }
        }
        catch (Exception)
        {
            process.Dispose();
            throw Failure("draft-process-start");
        }

        using (process)
        {
            using var monitorStop = new CancellationTokenSource();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, monitorStop.Token);
            var stdoutTask = DrainAsync(process.StandardOutput.BaseStream, request.MaxOutputBytes, "draft-process-output-budget");
            var stderrTask = DrainAsync(process.StandardError.BaseStream, request.MaxErrorBytes, "draft-process-error-budget");
            var exitTask = process.WaitForExitAsync(CancellationToken.None);
            var timeoutTask = Task.Delay(request.Timeout, monitorStop.Token);
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
            var rssTask = request.MaxProcessRssBytes == 0
                ? Task.Delay(Timeout.InfiniteTimeSpan, monitorStop.Token)
                : MonitorRssAsync(process, request.MaxProcessRssBytes, monitorStop.Token);
            var fileTask = request.OutputFilePath is null
                ? Task.Delay(Timeout.InfiniteTimeSpan, monitorStop.Token)
                : MonitorOutputFileAsync(request.OutputFilePath, request.MaxOutputFileBytes!.Value, monitorStop.Token);

            try
            {
                var stdoutDone = false;
                var stderrDone = false;
                var exitDone = false;
                while (!exitDone || !stdoutDone || !stderrDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var active = new List<Task> { timeoutTask, rssTask, fileTask, cancellationTask };
                    if (!exitDone) active.Add(exitTask);
                    if (!stdoutDone) active.Add(stdoutTask);
                    if (!stderrDone) active.Add(stderrTask);
                    var completed = await Task.WhenAny(active).ConfigureAwait(false);
                    if (completed == stdoutTask)
                    {
                        _ = await stdoutTask.ConfigureAwait(false);
                        stdoutDone = true;
                    }
                    else if (completed == stderrTask)
                    {
                        _ = await stderrTask.ConfigureAwait(false);
                        stderrDone = true;
                    }
                    else if (completed == timeoutTask)
                    {
                        throw Failure("draft-process-timeout");
                    }
                    else if (completed == cancellationTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    else if (completed == rssTask)
                    {
                        await rssTask.ConfigureAwait(false);
                        rssTask = Task.Delay(Timeout.InfiniteTimeSpan, monitorStop.Token);
                    }
                    else if (completed == fileTask)
                    {
                        await fileTask.ConfigureAwait(false);
                        fileTask = Task.Delay(Timeout.InfiniteTimeSpan, monitorStop.Token);
                    }
                    else if (completed == exitTask)
                    {
                        await exitTask.ConfigureAwait(false);
                        exitDone = true;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutTask.IsCompleted)
                {
                    throw Failure("draft-process-timeout");
                }

                if (request.OutputFilePath is not null)
                {
                    CheckOutputFile(request.OutputFilePath, request.MaxOutputFileBytes!.Value);
                }

                if (process.ExitCode != 0)
                {
                    throw Failure("draft-process-exit");
                }

                monitorStop.Cancel();
                await ObserveMonitorAsync(rssTask).ConfigureAwait(false);
                await ObserveMonitorAsync(fileTask).ConfigureAwait(false);
                await ObserveMonitorAsync(timeoutTask).ConfigureAwait(false);
                await ObserveMonitorAsync(cancellationTask).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new DraftProcessResult(await stdoutTask.ConfigureAwait(false), Encoding.UTF8.GetString(await stderrTask.ConfigureAwait(false)));
            }
            catch
            {
                monitorStop.Cancel();
                KillTree(process);
                await DrainAfterStopAsync(process, stdoutTask, stderrTask, exitTask).ConfigureAwait(false);
                await ObserveMonitorsAfterFailureAsync(rssTask, fileTask, timeoutTask, cancellationTask).ConfigureAwait(false);
                throw;
            }
            finally
            {
                monitorStop.Cancel();
            }
        }
    }

    private static ProcessStartInfo CreateStartInfo(DraftProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.Executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    private static void Validate(DraftProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Executable)
            || request.Arguments is null
            || request.Environment is null
            || request.Arguments.Any(static value => value is null)
            || request.Environment.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
            || request.Timeout <= TimeSpan.Zero
            || request.Timeout == Timeout.InfiniteTimeSpan
            || request.MaxOutputBytes < 0
            || request.MaxErrorBytes < 0
            || request.MaxProcessRssBytes < 0
            || (request.OutputFilePath is null) != (request.MaxOutputFileBytes is null)
            || request.MaxOutputFileBytes is < 0)
        {
            throw Failure("draft-process-invalid-request");
        }
    }

    private static async Task<byte[]> DrainAsync(Stream stream, int limit, string errorCode)
    {
        using var output = new MemoryStream(Math.Min(limit, 8192));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (read > limit - output.Length)
            {
                throw Failure(errorCode);
            }

            output.Write(buffer, 0, read);
        }
    }

    private static async Task MonitorRssAsync(Process process, long limit, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(MonitorInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                if (process.HasExited)
                {
                    return;
                }

                process.Refresh();
                if (process.WorkingSet64 > limit)
                {
                    throw Failure("draft-process-rss-budget");
                }
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    private static async Task MonitorOutputFileAsync(string path, int limit, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(MonitorInterval, cancellationToken).ConfigureAwait(false);
            CheckOutputFile(path, limit);
        }
    }

    private static void CheckOutputFile(string path, int limit)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > limit)
            {
                throw Failure("draft-process-output-file-budget");
            }
        }
        catch (FileNotFoundException)
        {
            // The producer may not have created the file yet.
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Failure("draft-process-output-file");
        }
        catch (UnauthorizedAccessException)
        {
            throw Failure("draft-process-output-file");
        }
    }

    private static async Task ObserveMonitorAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Monitors are canceled after success or after the process tree is stopped.
        }
    }

    private static async Task ObserveMonitorsAfterFailureAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await ObserveMonitorAsync(task).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Keep the first process failure as the reported reason while observing every monitor.
            }
        }
    }

    private static InvalidDataException Failure(string code) => new(code);

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process may already have exited as the tree was being stopped.
        }
    }

    private static async Task DrainAfterStopAsync(Process process, Task<byte[]> stdout, Task<byte[]> stderr, Task exit)
    {
        KillTree(process);
        try
        {
            await Task.WhenAll(exit, stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception) when (process.HasExited || !process.StartInfo.RedirectStandardOutput)
        {
            // Preserve the original error after the process tree has been stopped.
        }
        catch (TimeoutException)
        {
            // Preserve the original error if a platform does not close redirected pipes promptly.
        }
    }
}

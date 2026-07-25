using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DeployController.Services;

public sealed record ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed record CommandResult(
    string CommandLine,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;

    public string FailureMessage => TimedOut
        ? $"`{CommandLine}` timed out after {Duration.TotalSeconds:F0}s."
        : $"`{CommandLine}` exited with code {ExitCode}.";
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a child process, streaming stdout/stderr line by line to <paramref name="onOutput"/>
    /// (second argument is <c>true</c> for stderr) while also buffering them in the result.
    /// </summary>
    Task<CommandResult> RunAsync(
        ProcessRequest request,
        Action<string, bool>? onOutput = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deadlock-free process execution: output is consumed through the asynchronous
/// <see cref="Process.OutputDataReceived"/> / <see cref="Process.ErrorDataReceived"/> events
/// (never by a synchronous ReadToEnd on one stream while the other fills its buffer),
/// and stdin is closed immediately so interactive prompts fail fast instead of hanging.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger) => _logger = logger;

    public async Task<CommandResult> RunAsync(
        ProcessRequest request,
        Action<string, bool>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var commandLine = FormatCommandLine(request.FileName, request.Arguments);
        var stopwatch = Stopwatch.StartNew();

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutClosed.TrySetResult();
                return;
            }

            stdout.AppendLine(e.Data);
            SafeInvoke(onOutput, e.Data, false);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrClosed.TrySetResult();
                return;
            }

            stderr.AppendLine(e.Data);
            SafeInvoke(onOutput, e.Data, true);
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessLaunchException(
                $"Could not start '{request.FileName}'. Make sure it is installed and available on PATH. ({ex.Message})",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Nothing is ever written to the child's stdin; closing it makes credential
        // prompts (git) fail immediately instead of blocking the deployment forever.
        try { process.StandardInput.Close(); } catch (IOException) { /* already gone */ }

        var timedOut = false;
        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            KillProcessTree(process, commandLine);

            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Process {Command} did not exit after kill.", commandLine);
            }

            if (!timedOut)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Give the redirected readers a moment to flush their final lines.
        try
        {
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Timed out waiting for output flush of {Command}.", commandLine);
        }

        stopwatch.Stop();

        var exitCode = timedOut ? -1 : SafeExitCode(process);
        _logger.LogDebug("{Command} -> exit {ExitCode} in {Elapsed}ms", commandLine, exitCode, stopwatch.ElapsedMilliseconds);

        return new CommandResult(
            commandLine,
            exitCode,
            stdout.ToString(),
            stderr.ToString(),
            stopwatch.Elapsed,
            timedOut);
    }

    private void SafeInvoke(Action<string, bool>? onOutput, string line, bool isError)
    {
        if (onOutput is null) return;

        try
        {
            onOutput(line, isError);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Output callback threw while handling a process line.");
        }
    }

    private void KillProcessTree(Process process, string commandLine)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            _logger.LogWarning(ex, "Failed to kill process tree for {Command}.", commandLine);
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    public static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(Quote(fileName));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value) =>
        value.Length > 0 && !value.Any(char.IsWhiteSpace) ? value : $"\"{value.Replace("\"", "\\\"")}\"";
}

public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(string message, Exception? inner = null) : base(message, inner) { }
}

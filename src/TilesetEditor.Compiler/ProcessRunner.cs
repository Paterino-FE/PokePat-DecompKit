using System.Diagnostics;
using System.Text;

namespace TilesetEditor.Compiler;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration, bool TimedOut)
{
    public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : StdOut + (StdOut.EndsWith('\n') || StdOut.Length == 0 ? "" : "\n") + StdErr;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Runs a process with argument-list quoting (safe for paths with spaces) and UTF-8 output.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not start '{fileName}': {ex.Message}", ex);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!timedOut) throw;
        }
        p.WaitForExit(); // flush async readers
        return new ProcessResult(timedOut ? -1 : p.ExitCode, stdout.ToString(), stderr.ToString(), sw.Elapsed, timedOut);
    }
}

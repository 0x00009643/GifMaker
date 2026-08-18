using System.Diagnostics;
using System.IO;

namespace GifMaker.Services;

public sealed record RunResult(int ExitCode, string ErrorTail);

/// <summary>进程运行助手：并发读取 stdout/stderr，支持取消时杀进程。</summary>
public static class ProcessRunner
{
    public static async Task<RunResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        string? workingDir,
        CancellationToken ct,
        Stream? stdoutSink = null,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe) ?? ""
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {exe}");

        var stderrLines = new List<string>(32);
        Task? stderrTask = null;

        try
        {
            var stdoutTask = stdoutSink != null
                ? p.StandardOutput.BaseStream.CopyToAsync(stdoutSink, ct)
                : ReadLinesAsync(p.StandardOutput, onStdoutLine, ct);

            stderrTask = ReadLinesAsync(p.StandardError, line =>
            {
                if (line != null)
                {
                    stderrLines.Add(line);
                    onStderrLine?.Invoke(line);
                }
            }, ct);
            await p.WaitForExitAsync(ct);
            await stdoutTask;
            await stderrTask;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new RunResult(p.ExitCode, string.Join(Environment.NewLine, stderrLines.TakeLast(12)));
    }

    private static async Task ReadLinesAsync(
        StreamReader reader, Action<string>? onLine, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
            onLine?.Invoke(line);
    }
}
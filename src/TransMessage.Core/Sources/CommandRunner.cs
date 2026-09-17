using System.Diagnostics;
using System.Text;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Sources;

public sealed record RunResult(int ExitCode, TimeSpan Duration, string Tail);

public sealed class CommandRunner(Sanitizer? sanitizer = null)
{
    public async Task<RunResult> RunAsync(IReadOnlyList<string> command, TextWriter stdout, TextWriter stderr, int tailLines = 12, CancellationToken ct = default)
    {
        if (command.Count == 0) throw new AppException("RUN_COMMAND", "在 -- 后指定要执行的命令。");
        var start = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };
        if (Path.GetExtension(command[0]).ToLowerInvariant() is ".bat" or ".cmd")
        {
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            // cmd /s needs a surrounding quote pair around a quoted batch path.
            if (command.Any(s => s.IndexOfAny(['\r', '\n', '"']) >= 0)) throw new AppException("RUN_BATCH_QUOTE", "BAT 参数包含引号或换行，请显式使用 cmd /c 执行。");
            start.Arguments = "/d /s /c \"" + string.Join(" ", command.Select(s => "\"" + s + "\"")) + "\"";
        }
        else foreach (var arg in command.Skip(1)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        var timer = Stopwatch.StartNew();
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new AppException("RUN_START", "无法启动命令。检查文件名、路径和引号。"); }
        var tail = new StringBuilder();
        var gate = new object();
        async Task CopyAsync(StreamReader reader, TextWriter output)
        {
            var buffer = new char[2048]; int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read)); await output.FlushAsync();
                lock (gate)
                {
                    tail.Append(buffer, 0, read);
                    if (tail.Length > 12000) tail.Remove(0, tail.Length - 12000);
                }
            }
        }
        var outputs = Task.WhenAll(CopyAsync(process.StandardOutput, stdout), CopyAsync(process.StandardError, stderr));
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            // Console Ctrl+C already reaches attached children. Give them time to clean up.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await process.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
        }
        // Do not hang indefinitely when a detached descendant retains an inherited output handle.
        try { await outputs.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { process.StandardOutput.Close(); process.StandardError.Close(); }
        string last;
        lock (gate) last = string.Join("\n", (sanitizer ?? new Sanitizer()).Redact(tail.ToString()).Split('\n').TakeLast(tailLines + 1));
        return new(process.ExitCode, timer.Elapsed, Sanitizer.Truncate(last, 2000));
    }

    public static Notification NotificationFor(IReadOnlyList<string> command, RunResult result, AppConfig config) => new()
    {
        Source = "runner",
        Level = result.ExitCode == 0 ? "success" : "error",
        Title = MessageTemplate.Expand(result.ExitCode == 0 ? config.Notifications.CommandCompleted : config.Notifications.CommandFailed,
            ("command", command[0]), ("exit_code", result.ExitCode.ToString()), ("duration", result.Duration.ToString("g"))),
        Message = $"命令：{Sanitizer.Truncate(string.Join(" ", command), 500)}\n耗时：{result.Duration:g}\n退出码：{result.ExitCode}"
            + (result.ExitCode != 0 && config.Notifications.TailLines > 0 ? "\n\n最后输出：\n" + result.Tail : "")
    };
}

using System.Diagnostics;
using System.Text.Json;
using TransMessage.Core.Hosting;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class CliIntegrationTests
{
    private static string Exe => Environment.GetEnvironmentVariable("TM_TEST_EXE")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/TransMessage.App/bin/Debug/net10.0-windows/TransMessage.exe"));
    private static async Task<(int Exit, string Output, string Error)> InvokeAsync(TestData data, string[] args, Dictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(data.Paths.Root);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (env is not null) foreach (var (key, value) in env) info.Environment[key] = value;
        using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); return (process.ExitCode, await output, await error);
    }
    [Fact]
    public async Task PublishedCliSendRunnerNotifyReachOneLocalDispatcher()
    {
        using var data = new TestData(); var channel = new RecordingChannel();
        var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        using var stop = new CancellationTokenSource(); var pipe = new InstancePipe(data.Paths);
        var running = pipe.RunAsync(async (request, ct) =>
        {
            if (request.Notification is null) return new(true, "test background");
            var delivery = await dispatcher.SendAsync(request.Notification, ct); return new(delivery.Ok, delivery.Status, delivery);
        }, stop.Token);
        try
        {
            Assert.True(File.Exists(Exe), "Build TransMessage.App first, or set TM_TEST_EXE to the published executable.");
            var version = await InvokeAsync(data, ["version"]); Assert.Equal(0, version.Exit); Assert.Contains(TransMessage.Core.BuildInfo.Version, version.Output);
            var doctor = await InvokeAsync(data, ["doctor"]); Assert.Equal(0, doctor.Exit); Assert.Contains("WARN", doctor.Output);
            var send = await InvokeAsync(data, ["send", "test"]); Assert.Equal(0, send.Exit); Assert.Single(channel.Sent);
            var success = await InvokeAsync(data, ["run", "--", "cmd", "/c", "echo hello"]); Assert.Equal(0, success.Exit); Assert.Contains("hello", success.Output);
            var failure = await InvokeAsync(data, ["run", "--", "cmd", "/c", "exit 1"]); Assert.Equal(1, failure.Exit); Assert.Equal(3, channel.Sent.Count);
            var home = Path.Combine(data.Paths.Root, "codex"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
            await File.WriteAllTextAsync(Path.Combine(sessions, $"rollout-{CodexTests.ThreadId}.jsonl"), CodexTests.Meta() + "\n");
            var env = new Dictionary<string, string> { ["CODEX_HOME"] = home };
            Assert.Equal(0, (await InvokeAsync(data, ["codex", "notify", CodexTests.Payload()], env)).Exit);
            Assert.Equal(0, (await InvokeAsync(data, ["codex", "notify", CodexTests.Payload()], env)).Exit);
            Assert.Equal(0, (await InvokeAsync(data, ["codex", "notify", CodexTests.Payload("Generate a concise, single-line task title", "hidden")], env)).Exit);
            Assert.Equal(4, channel.Sent.Count);
        }
        finally { stop.Cancel(); await running; }
    }
    [Fact]
    public async Task RunnerExitCodeSurvivesQqFailure()
    {
        using var data = new TestData();
        var success = await InvokeAsync(data, ["run", "--", "cmd", "/c", "exit 0"]); Assert.Equal(0, success.Exit); Assert.Contains("通知警告", success.Error);
        var failure = await InvokeAsync(data, ["run", "--", "cmd", "/c", "exit 17"]); Assert.Equal(17, failure.Exit); Assert.Contains("通知警告", failure.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundSurvivesWatchedDirectoryRemoval(bool codex)
    {
        using var data = new TestData();
        var home = Path.Combine(data.Paths.Root, "codex");
        var watched = Path.Combine(home, "sessions"); Directory.CreateDirectory(watched);
        data.Config.Update(c =>
        {
            c.Codex.Enabled = codex; c.Downloads.Enabled = !codex; c.Downloads.AutoDownloads = false;
            c.Downloads.Directories = [watched]; c.Update.Check = false; c.Server.Enabled = false;
        });
        var start = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["CODEX_HOME"] = home;
        foreach (var arg in new[] { "--data-dir", data.Paths.Root, "--background" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var error = process.StandardError.ReadToEndAsync();
        var pipe = new InstancePipe(data.Paths);
        try
        {
            var ready = false;
            for (var i = 0; i < 40; i++)
            {
                var status = await pipe.TrySendAsync(new("status"));
                if (status?.Message.Contains(codex ? "当前会话格式" : "正在监控 1 个目录", StringComparison.Ordinal) == true) { ready = true; break; }
                Assert.False(process.HasExited); await Task.Delay(150);
            }
            Assert.True(ready);
            Directory.Delete(watched);
            var reported = false;
            for (var i = 0; i < 40; i++)
            {
                Assert.False(process.HasExited, process.HasExited ? await error : "");
                if ((await pipe.TrySendAsync(new("status")))?.Message.Contains("监听已停止", StringComparison.Ordinal) == true) { reported = true; break; }
                await Task.Delay(150);
            }
            Assert.True(reported);
            Assert.True((await pipe.TrySendAsync(new("show")))?.Ok);
            Directory.CreateDirectory(watched);
            Assert.True((await pipe.TrySendAsync(new("reload")))?.Ok);
            var restored = false;
            for (var i = 0; i < 40; i++)
            {
                var status = await pipe.TrySendAsync(new("status"));
                if (status?.Message.Contains(codex ? "当前会话格式" : "正在监控 1 个目录", StringComparison.Ordinal) == true) { restored = true; break; }
                await Task.Delay(150);
            }
            Assert.True(restored);
            Assert.True((await pipe.TrySendAsync(new("stop")))?.Ok);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0, process.ExitCode);
            var log = string.Join("\n", Directory.EnumerateFiles(data.Paths.Logs).Select(File.ReadAllText));
            Assert.Contains("GUI started", log); Assert.Contains("GUI exit requested", log); Assert.Contains("GUI stopped", log);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedBackgroundIsSingleInstanceAndStopsCleanly(bool remoteEnabled)
    {
        using var data = new TestData();
        data.Config.Update(c =>
        {
            c.Codex.Enabled = false; c.Downloads.Enabled = false; c.Update.Check = false; c.Server.Port = TestData.Port();
            c.Qq.Targets.Add(new() { Id = "test-owner" }); c.RemoteCodex.OwnerId = "test-owner"; c.RemoteCodex.Enabled = remoteEnabled;
            c.RemoteCodex.Executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TransMessage.GuiTests/bin/Debug/net10.0-windows/TransMessage.GuiTests.exe"));
        });
        var start = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true };
        var pidFile = Path.Combine(data.Paths.Root, "codex.pid"); start.Environment["TM_TEST_CODEX_PID_FILE"] = pidFile;
        foreach (var arg in new[] { "--data-dir", data.Paths.Root, "--background" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var pipe = new InstancePipe(data.Paths);
        try
        {
            PipeReply? status = null;
            for (var i = 0; i < 40; i++)
            {
                status = await pipe.TrySendAsync(new("status"));
                if (status?.Message.Contains("API：仅本机", StringComparison.Ordinal) == true) break;
                Assert.False(process.HasExited); await Task.Delay(150);
            }
            Assert.Contains("API：仅本机", status?.Message ?? "");
            using var child = remoteEnabled ? Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile))) : null;
            var second = await InvokeAsync(data, ["--background"]); Assert.Equal(0, second.Exit); Assert.False(process.HasExited);
            Assert.True((await pipe.TrySendAsync(new("show")))?.Ok);
            await TestData.UntilAsync(() => { process.Refresh(); return process.MainWindowHandle != IntPtr.Zero; });
            var stopped = await pipe.TrySendAsync(new("stop")); Assert.True(stopped?.Ok);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0, process.ExitCode);
            Assert.True(child?.HasExited ?? true);
            using var restarted = Process.Start(start)!;
            try
            {
                PipeReply? ready = null;
                for (var i = 0; i < 40; i++)
                {
                    ready = await pipe.TrySendAsync(new("status"));
                    if (ready?.Message.Contains("API：仅本机", StringComparison.Ordinal) == true) break;
                    Assert.False(restarted.HasExited); await Task.Delay(150);
                }
                Assert.False(restarted.HasExited); Assert.Contains("API：仅本机", ready?.Message ?? "");
                using var newChild = remoteEnabled ? Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile))) : null;
                Assert.True((await pipe.TrySendAsync(new("show")))?.Ok);
                await TestData.UntilAsync(() => { restarted.Refresh(); return restarted.MainWindowHandle != IntPtr.Zero; });
                Assert.True((await pipe.TrySendAsync(new("stop")))?.Ok);
                await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0, restarted.ExitCode);
                Assert.True(newChild?.HasExited ?? true);
            }
            finally { if (!restarted.HasExited) { restarted.Kill(true); await restarted.WaitForExitAsync(); } }
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}

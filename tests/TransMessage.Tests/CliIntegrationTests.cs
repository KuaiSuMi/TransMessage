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
            var version = await InvokeAsync(data, ["version"]); Assert.Equal(0, version.Exit); Assert.Contains("0.1.0", version.Output);
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
}

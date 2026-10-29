using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class CodexTests
{
    public const string ThreadId = "12345678-1234-1234-1234-123456789012";
    private static readonly SessionIdentity User = new(ThreadId, @"D:\项目\测试", "codex-tui", true);
    public static string Payload(string input = "实现功能", string turn = "turn-1") => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["type"] = "agent-turn-complete",
        ["thread-id"] = ThreadId,
        ["turn-id"] = turn,
        ["input-messages"] = new[] { input },
        ["last-assistant-message"] = "已完成",
        ["unknown-future-field"] = "ignored"
    });
    public static string Meta(string source = "cli") => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = ThreadId, cwd = @"D:\项目\测试", originator = "codex-tui", source } });
    public static string Event(string type, object payload) => JsonSerializer.Serialize(new { type, payload });
    [Fact]
    public void ValidAndMissingOptionalFields()
    {
        var n = CodexEvents.ParseNotify(Payload(), new(), User); Assert.NotNull(n); Assert.Equal($"codex:{ThreadId}:turn-1", n.Id); Assert.Contains("实现功能", n.Message);
        Assert.Null(CodexEvents.ParseNotify("not json", new(), User)); Assert.Null(CodexEvents.ParseNotify("{}", new(), User));
        Assert.Null(CodexEvents.ParseNotify(Payload(), new(), null));
    }
    [Theory]
    [InlineData("Generate a concise, single-line task title for the following request")]
    [InlineData("You are a task title generator. Write one title")]
    [InlineData("You are creating a CONTEXT CHECKPOINT")]
    public void InternalRequestsAreIgnored(string text) => Assert.Null(CodexEvents.ParseNotify(Payload(text), new(), User));
    [Fact]
    public void CompletionKeepsRolloutTimestampAndLabelsMissingHookTime()
    {
        const string original = "2026-10-14T21:34:56.1234567-04:00";
        var reader = new CodexTurnReader(); var cfg = new AppConfig(); reader.ReadLine(Meta(), cfg);
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "a" }), cfg);
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "检查" }), cfg);
        var line = JsonSerializer.Serialize(new { timestamp = original, type = "event_msg", payload = new { type = "task_complete", turn_id = "a", last_agent_message = "完成" } });
        var notification = reader.ReadLine(line, cfg)!;
        Assert.Equal(original, notification.Timestamp.ToString("O")); Assert.Equal("original", notification.Metadata["timestamp_source"]);
        Assert.StartsWith("原始时间：2026-10-15 09:34:56（北京时间）\n", TransMessage.Core.Security.Sanitizer.QqText(notification));
        var hook = CodexEvents.ParseNotify(Payload(), cfg, User)!; Assert.Equal("received", hook.Metadata["timestamp_source"]);
        var timestampedHook = Payload()[..^1] + ",\"timestamp\":\"" + original + "\"}";
        Assert.Equal(original, CodexEvents.ParseNotify(timestampedHook, cfg, User)!.Timestamp.ToString("O"));
    }
    [Fact]
    public void SubagentAndInternalMetadataAreRejected()
    {
        using var json = JsonDocument.Parse("""{"id":"12345678-1234-1234-1234-123456789012","source":{"subagent":"review"},"originator":"codex-tui"}""");
        Assert.False(CodexEvents.ReadIdentity(json.RootElement)!.IsUser);
        Assert.Null(CodexEvents.ParseNotify(Payload(), new(), User with { IsUser = false }));
    }
    [Theory]
    [InlineData("task_complete", "success")]
    [InlineData("turn_aborted", "warning")]
    public void SessionRequiresMatchingStartedUserTurn(string completion, string level)
    {
        var reader = new CodexTurnReader(); var cfg = new AppConfig(); reader.ReadLine(Meta(), cfg);
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "a" }), cfg);
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "测试程序" }), cfg);
        Assert.Null(reader.ReadLine(Event("event_msg", new { type = "agent_message", message = "中间结果" }), cfg));
        var n = reader.ReadLine(Event("event_msg", new { type = completion, turn_id = "a", last_agent_message = "完成", duration_ms = 1200 }), cfg);
        Assert.NotNull(n); Assert.Equal(level, n.Level); Assert.Contains("耗时", n.Message);
        Assert.Null(reader.ReadLine(Event("event_msg", new { type = completion, turn_id = "a" }), cfg));
    }
    [Fact]
    public void FailureAndMalformedEventsAreHandled()
    {
        var reader = new CodexTurnReader(); var cfg = new AppConfig(); reader.ReadLine(Meta(), cfg);
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "x" }), cfg);
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "test" }), cfg);
        var n = reader.ReadLine(Event("event_msg", new { type = "task_complete", turn_id = "x", error = new { message = "failed" } }), cfg);
        Assert.Equal("error", n!.Level);
        Assert.Null(reader.ReadLine("{broken", cfg));
    }
    [Fact]
    public void TransientInputEventsAreNotTreatedAsDurableDesktopState()
    {
        var reader = new CodexTurnReader(); var cfg = new AppConfig(); reader.ReadLine(Meta(), cfg);
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "a" }), cfg);
        var waiting = Event("event_msg", new { type = "request_user_input", turn_id = "a", call_id = "call", questions = new[] { new { question = "sensitive-question", isSecret = true } } });
        Assert.Null(reader.ReadLine(waiting, cfg));
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "实现测试" }), cfg);
        Assert.Null(reader.ReadLine(waiting, cfg));
        reader.ReadLine(Event("event_msg", new { type = "turn_aborted", turn_id = "a" }), cfg); Assert.Null(reader.ReadLine(waiting, cfg));
    }
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void DesktopManagedCompletionUsesOnlyActiveManagedChannel(bool enabled, bool managed)
    {
        var cfg = new AppConfig(); cfg.RemoteCodex.Enabled = enabled;
        cfg.RemoteCodex.Threads.Add(new() { Id = ThreadId, Managed = managed });
        var meta = JsonSerializer.SerializeToElement(new { id = ThreadId, cwd = @"C:\Code", originator = "Codex Desktop", source = "vscode", thread_source = "user" });
        var identity = CodexEvents.ReadIdentity(meta)!; Assert.True(identity.IsUser);
        var hook = CodexEvents.ParseNotify(Payload(), cfg, identity);
        var reader = new CodexTurnReader(); reader.ReadLine(Event("session_meta", meta), cfg);
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "turn-1" }), cfg);
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "实现功能" }), cfg);
        var rollout = reader.ReadLine(Event("event_msg", new { type = "task_complete", turn_id = "turn-1", last_agent_message = "已完成" }), cfg);
        Assert.True(reader.RecognizedCompletion);
        if (enabled && managed) { Assert.Null(hook); Assert.Null(rollout); }
        else { Assert.NotNull(hook); Assert.NotNull(rollout); Assert.Equal(hook.Id, rollout.Id); }

        // Disabling control restores monitoring without removing the Managed record or recreating the reader.
        cfg.RemoteCodex.Enabled = false;
        reader.ReadLine(Event("event_msg", new { type = "task_started", turn_id = "turn-2" }), cfg);
        reader.ReadLine(Event("event_msg", new { type = "user_message", message = "继续处理" }), cfg);
        var resumed = reader.ReadLine(Event("event_msg", new { type = "task_complete", turn_id = "turn-2" }), cfg);
        Assert.NotNull(resumed);
        Assert.Equal(CodexEvents.ParseNotify(Payload("继续处理", "turn-2"), cfg, identity)!.Id, resumed.Id);
    }
    [Fact]
    public void ManagedSessionsDoNotBroadcastDuplicateNotifications()
    {
        var meta = JsonSerializer.SerializeToElement(new { id = ThreadId, cwd = "C:\\Code", originator = "transmessage", source = "exec" });
        Assert.False(CodexEvents.ReadIdentity(meta)!.IsUser);
    }
    [Theory]
    [InlineData("# 中文 comment\nmodel = 'example'\n[projects.'D:\\项目']\ntrust_level = 'trusted'\n")]
    [InlineData("# keep\nnotify = [ 'old.exe', 'arg' ] # existing hook\nmodel = 'example'\n[table]\nvalue = 42\n")]
    [InlineData("\"notify\" = [\n 'old.exe',\n 'arg',\n]\nmodel='other'\n")]
    public void IntegrationPreservesConfigAndUninstalls(string original)
    {
        using var data = new TestData(); var home = Path.Combine(data.Paths.Root, "codex"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "config.toml"); File.WriteAllText(path, original);
        var integration = new CodexIntegration(data.Paths, home); integration.Install(@"C:\测试 软件\TransMessage.exe");
        var installed = File.ReadAllText(path); Assert.True(integration.IsInstalled()); Assert.Contains("model", installed);
        integration.Install(@"C:\测试 软件\TransMessage.exe"); Assert.Equal(installed, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(home, "*.transmessage-backup-*"));
        Assert.True(integration.Uninstall()); Assert.Equal(original, File.ReadAllText(path));
    }
    [Fact]
    public void InvalidTomlAndExternalEditAreNeverOverwritten()
    {
        using var data = new TestData(); var home = Path.Combine(data.Paths.Root, "codex"); Directory.CreateDirectory(home);
        var path = Path.Combine(home, "config.toml"); File.WriteAllText(path, "[broken"); var integration = new CodexIntegration(data.Paths, home);
        Assert.Throws<AppException>(() => integration.Install(@"C:\TransMessage.exe")); Assert.Equal("[broken", File.ReadAllText(path));
        File.WriteAllText(path, "model = 'a'\n"); integration.Install(@"C:\TransMessage.exe");
        File.WriteAllText(path, "notify=['custom.exe']\n"); Assert.Throws<AppException>(() => integration.Uninstall()); Assert.Equal("notify=['custom.exe']\n", File.ReadAllText(path));
    }
    [Fact]
    public async Task SessionWatcherSkipsHistoryAndDeduplicatesHook()
    {
        using var data = new TestData(); var home = Path.Combine(data.Paths.Root, "codex"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var file = Path.Combine(sessions, $"rollout-{ThreadId}.jsonl"); await File.WriteAllTextAsync(file, Meta() + "\n");
        var channel = new RecordingChannel(); var dispatcher = new Dispatcher(data.Config, new(data.Paths), new(), channel, TestData.Logger);
        using var watcher = new CodexSessionWatcher(data.Config, dispatcher.SendAsync, TestData.Logger, home); using var stop = new CancellationTokenSource(); var running = watcher.RunAsync(stop.Token);
        await TestData.UntilAsync(() => watcher.Status.StartsWith("已监听", StringComparison.Ordinal));
        var lines = new[] { Event("event_msg", new { type = "task_started", turn_id = "turn-1" }), Event("event_msg", new { type = "user_message", message = "hello" }), Event("event_msg", new { type = "task_complete", turn_id = "turn-1", last_agent_message = "done" }) };
        await File.AppendAllTextAsync(file, string.Join("\n", lines) + "\n"); await TestData.UntilAsync(() => channel.Sent.Count == 1);
        var result = await dispatcher.SendAsync(CodexEvents.ParseNotify(Payload("hello"), data.Config.Load(), User)!); Assert.Equal("duplicate", result.Status);
        stop.Cancel(); await running; Assert.Single(channel.Sent);
    }
}

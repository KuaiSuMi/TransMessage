using System.Collections.Concurrent;
using System.Text.Json;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class DesktopNotificationTests
{
    private static string Meta(string thread, string source = "user") => CodexTests.Event("session_meta", new { id = thread, cwd = @"C:\Project", originator = "Codex Desktop", source = "vscode", thread_source = source });
    private static string Started(string turn) => CodexTests.Event("event_msg", new { type = "task_started", turn_id = turn, root_turn_id = turn });
    private static string Input(string thread, string turn, string text) => CodexTests.Event("event_msg", new
    {
        type = "item_completed",
        thread_id = thread,
        turn_id = turn,
        item = new { type = "UserMessage", content = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } } }
    });
    private static string Completed(string turn) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-10-15T04:47:50.396Z",
        type = "event_msg",
        payload = new { type = "task_complete", turn_id = turn, last_agent_message = "完成", duration_ms = 1200 }
    });

    [Theory]
    [InlineData("user", "same", "same", "检查测试", true)]
    [InlineData("user", "other", "same", "检查测试", false)]
    [InlineData("user", "same", "other", "检查测试", false)]
    [InlineData("subagent", "same", "same", "检查测试", false)]
    [InlineData("user", "same", "same", "Generate a concise, single-line task title", false)]
    public void DesktopInputRequiresMatchingUserThreadAndTurn(string source, string inputThread, string inputTurn, string text, bool expected)
    {
        var thread = Guid.NewGuid().ToString(); var reader = new CodexTurnReader(); var config = new TransMessage.Core.Configuration.AppConfig();
        reader.ReadLine(Meta(thread, source), config);
        reader.ReadLine(Started("turn"), config);
        reader.ReadLine(Input(inputThread == "same" ? thread : Guid.NewGuid().ToString(), inputTurn == "same" ? "turn" : "other", text), config);
        var result = reader.ReadLine(Completed("turn"), config);
        Assert.Equal(expected, result is not null);
        if (result is not null)
        {
            Assert.Contains(text, result.Message); Assert.Equal(DateTimeOffset.Parse("2026-10-15T04:47:50.396Z"), result.Timestamp);
            Assert.Null(reader.ReadLine(Completed("turn"), config));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentDesktopCompletionsSurviveLongActiveSessionAndSlowDelivery(bool duringStartup)
    {
        using var data = new TestData(); var home = Path.Combine(data.Paths.Root, "codex"); var sessions = Path.Combine(home, "sessions"); Directory.CreateDirectory(sessions);
        var threads = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var files = threads.Select(t => Path.Combine(sessions, $"rollout-{t}.jsonl")).ToArray();
        var noise = CodexTests.Event("response_item", new { type = "function_call_output", output = new string('x', 1024 * 1024) }) + "\n";
        for (var i = 0; i < 4; i++)
        {
            await File.WriteAllTextAsync(files[i], Meta(threads[i]) + "\n" + Started("old") + "\n" + Input(threads[i], "old", "旧任务") + "\n" + Completed("old") + "\n" + Started("active") + "\n" + Input(threads[i], "active", $"任务 {i}") + "\n");
            if (i == 0) for (var j = 0; j < 5; j++) await File.AppendAllTextAsync(files[i], noise);
        }
        var sent = new ConcurrentQueue<Notification>();
        async Task<DeliveryResult> Send(Notification notification, CancellationToken ct)
        {
            await Task.Delay(40, ct); sent.Enqueue(notification); return new(true, notification.Id, "sent");
        }
        using var watcher = new CodexSessionWatcher(data.Config, Send, TestData.Logger, home);
        using var stop = new CancellationTokenSource(); var running = watcher.RunAsync(stop.Token);
        try
        {
            if (!duringStartup) await TestData.UntilAsync(() => watcher.Status.StartsWith("已监听", StringComparison.Ordinal));
            Assert.Empty(sent);
            await Task.WhenAll(threads.Select(async (thread, i) =>
            {
                if (i >= 4) await File.WriteAllTextAsync(files[i], Meta(thread) + "\n" + Started("active") + "\n" + Input(thread, "active", $"任务 {i}") + "\n");
                var completion = Completed("active");
                await File.AppendAllTextAsync(files[i], completion[..20]);
                await File.AppendAllTextAsync(files[i], completion[20..] + "\n" + completion + "\n");
            }));
            await TestData.UntilAsync(() => sent.Count == threads.Length);
            Assert.Equal(threads.Order(), sent.Select(n => n.Metadata["thread_id"]).Order());
            Assert.All(sent, n => Assert.Equal("active", n.Metadata["turn_id"]));
        }
        finally { stop.Cancel(); await running; }
    }
}

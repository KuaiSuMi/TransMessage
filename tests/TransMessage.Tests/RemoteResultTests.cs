using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using TransMessage.Core.Channels;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Tests;

public sealed class RemoteResultTests
{
    private const string ThreadId = "aaaaaaaa-1111-4111-8111-111111111111";
    private static readonly DateTimeOffset OriginalTime = DateTimeOffset.Parse("2026-10-29T12:34:56+08:00");

    [Fact]
    public async Task QuotedFullReadsExactTurnAcrossPagesAndKeepsOriginalTimestampAndReference()
    {
        await using var h = await Harness.CreateAsync();
        h.Server.ReplyResult = (method, args) => method == "thread/items/list" ?
            Page("old-turn", args.GetProperty("cursor").ValueKind == JsonValueKind.Null ? "first" : "second",
                args.GetProperty("cursor").ValueKind == JsonValueKind.Null ? "next" : null) : null;
        await h.PostAsync("/full");
        var notice = Assert.Single(h.Sent).Notice;
        Assert.Equal("first\n\nsecond", notice.Message); Assert.Equal(OriginalTime, notice.Timestamp);
        Assert.Equal("old-turn", notice.Metadata["turn_id"]);
        var reference = h.References.Find("owner", "out-1")!;
        Assert.Equal("old-turn", reference.TurnId); Assert.Equal(OriginalTime, reference.Timestamp);
        Assert.Equal(2, h.Server.Calls.Count(c => CodexEvents.Text(c, "method") == "thread/items/list"));
        Assert.All(h.Server.Calls.Where(c => CodexEvents.Text(c, "method") == "thread/items/list"), c => Assert.Equal("old-turn", c.GetProperty("params").GetProperty("turnId").GetString()));
        Assert.DoesNotContain(h.Server.Calls, c => CodexEvents.Text(c, "method") is "thread/resume" or "turn/start" or "thread/queue/add");
    }

    [Fact]
    public async Task FullExpandsReferenceLinksBeforeSeparatingTheirDefinitionsIntoAnotherPart()
    {
        await using var h = await Harness.CreateAsync();
        h.Text = "[发布 `报告😀`][r] [附件][f]\n\n" + new string('x', 2590)
            + "\n\n[r]: https://example.com/release?q=1&n=2#top\n[f]: <D:/文档/报告 (1).txt>";
        await h.PostAsync("/full", expected: 2);
        var first = h.Sent.First().Notice;
        Assert.Contains("[发布 `报告😀`](https://example.com/release?q=1&n=2#top)", first.Message);
        Assert.Contains("[附件](<D:/文档/报告 (1).txt>)", first.Message);
        Assert.Equal("old-turn", first.Metadata["turn_id"]);
        Assert.DoesNotContain(h.Sent, s => s.Notice.Message.Contains("[r]", StringComparison.Ordinal) || s.Notice.Message.Contains("[f]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LongFullIsCompleteAcrossUserRequestedPagesWithoutExceedingFourMessages()
    {
        await using var h = await Harness.CreateAsync();
        var text = "    开始\n" + string.Concat(Enumerable.Repeat("一段中文🙂 and text\n", 550)) + "\n结束 \n\n";
        h.Text = text;
        await h.PostAsync("/full", expected: 4);
        Assert.Equal(4, h.Sent.Count); Assert.Contains("/full 2", h.Sent.Last().Notice.Message);
        for (var page = 2; page <= (ReplyContent.Split(text).Count + 3) / 4; page++)
            await h.PostAsync("/full " + page, "out-1", Math.Min(4, ReplyContent.Split(text).Count - (page - 1) * 4));
        var parts = h.Sent.Select(s => s.Notice.Message.Split("\n\n下一页：", StringSplitOptions.None)[0]);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(h.Sent.GroupBy(s => s.ReplyId), group => Assert.InRange(group.Count(), 1, 4));
        Assert.All(h.Sent, s => Assert.DoesNotContain("已截断", Sanitizer.QqText(s.Notice)));
    }

    [Theory]
    [InlineData("/full 0", "original")]
    [InlineData("/full nope", "original")]
    [InlineData("/full", "thread-only")]
    [InlineData("/file", null)]
    public async Task RetrievalNeedsACompletedTurnQuoteAndAValidPage(string command, string? reference)
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync(command, reference);
        await TestData.UntilAsync(() => h.Sent.Count == 1);
        Assert.DoesNotContain(h.Server.Calls, c => CodexEvents.Text(c, "method") == "thread/items/list");
        Assert.Empty(h.Files);
    }

    [Fact]
    public async Task FilesComeFromQuotedReplyAndUseThreeAttachmentsPerPage()
    {
        await using var h = await Harness.CreateAsync();
        var paths = Enumerable.Range(1, 4).Select(i => Path.Combine(h.Data.Paths.Root, $"报告 {i}.txt")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "artifact");
        h.Text = string.Join("\n", paths.Select(p => $"[附件](<{p.Replace('\\', '/')}>)"));
        await h.PostAsync("/file", expected: 4);
        Assert.Equal(3, h.Files.Count); Assert.Contains("/file 2", Assert.Single(h.Sent).Notice.Message);
        Assert.Equal(OriginalTime, h.Sent.Single().Notice.Timestamp);
        await h.PostAsync("/file 2", "out-1", 2);
        Assert.Equal(paths, h.Files.Select(f => f.Path));
        Assert.All(h.Files, f => Assert.Equal("old-turn", f.Notice.Metadata["turn_id"]));
        Assert.Equal(4, h.Sent.Count(s => s.ReplyId == h.Files.First().ReplyId) + h.Files.Count(f => f.ReplyId == h.Files.First().ReplyId));
    }

    [Fact]
    public async Task MissingFilesAndCrossTurnResponsesDoNotExportAnything()
    {
        await using var h = await Harness.CreateAsync();
        h.Text = "[missing](missing.pdf) [online](https://example.com/report.pdf)";
        await h.PostAsync("/file"); await TestData.UntilAsync(() => h.Sent.Count == 1); Assert.Empty(h.Files);
        h.Server.ReplyResult = (method, _) => method == "thread/items/list" ? Page("different-turn", "wrong") : null;
        await h.PostAsync("/full"); await TestData.UntilAsync(() => h.Sent.Count == 2);
        Assert.DoesNotContain(h.Sent, s => s.Notice.Message == "wrong");
    }

    [Fact]
    public async Task FullIgnoresCommentaryAndRedactsBeforeSplitting()
    {
        await using var h = await Harness.CreateAsync();
        h.Text = "result " + new string('x', 2590) + " sk-proj-secretsecret123456789 rest";
        await h.PostAsync("/full", expected: 2);
        var text = string.Concat(h.Sent.Select(s => s.Notice.Message));
        Assert.DoesNotContain("commentary", text); Assert.DoesNotContain("secretsecret", text); Assert.Contains("[REDACTED]", text);
    }

    [Fact]
    public async Task ManagedCompletionCarriesTurnIdForLaterQuotedRetrieval()
    {
        await using var h = await Harness.CreateAsync();
        h.Data.Config.Update(c => c.RemoteCodex.Threads.Add(new() { Id = ThreadId, Managed = true, Name = "test" }));
        await h.Server.SendAsync(new { method = "turn/completed", @params = new { threadId = ThreadId, turn = new { id = "new-turn", status = "completed", completedAt = OriginalTime.ToUnixTimeSeconds() } } });
        await TestData.UntilAsync(() => h.Sent.Count == 1);
        Assert.Equal("new-turn", h.References.Find("owner", "out-1")!.TurnId);
    }

    [Fact]
    public async Task FileUploadDoesNotBlockAnotherDesktopCommand()
    {
        await using var h = await Harness.CreateAsync();
        var path = Path.Combine(h.Data.Paths.Root, "attachment.txt"); await File.WriteAllTextAsync(path, "attachment");
        h.Text = $"[附件](<{path.Replace('\\', '/')}>)";
        var uploading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeFile = async ct => { uploading.SetResult(); await release.Task.WaitAsync(ct); };
        await h.PostAsync("/file"); await uploading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await h.PostAsync("下一步", "thread-only");
            Assert.Single(h.Server.Calls, c => CodexEvents.Text(c, "method") == "thread/queue/add");
            Assert.Empty(h.Files);
        }
        finally { release.SetResult(); }
        await TestData.UntilAsync(() => h.Files.Count == 1);
    }

    [Fact]
    public async Task ExportDeadlineCancelsUploadAndDoesNotReplyWithExpiredMessageId()
    {
        await using var h = await Harness.CreateAsync();
        var path = Path.Combine(h.Data.Paths.Root, "attachment.txt"); await File.WriteAllTextAsync(path, "attachment");
        h.Text = $"[附件](<{path.Replace('\\', '/')}>)";
        h.BeforeFile = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct);
        await h.Remote.ProcessAsync(new(Guid.NewGuid().ToString(), "owner", "/file", DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(2)) { ReferenceIndex = "original" });
        await TestData.UntilAsync(() => h.Remote.Status.Contains("请求已过期", StringComparison.Ordinal));
        Assert.Single(h.Sent); Assert.Empty(h.Files);
    }

    private static object Page(string turnId, string text, string? next = null) => new
    {
        data = new object[] {
            new { turnId, item = new { type = "agentMessage", text = "commentary", phase = "commentary" }, completedAtMs = OriginalTime.ToUnixTimeMilliseconds() },
            new { turnId, item = new { type = "agentMessage", text, phase = "final_answer" }, completedAtMs = OriginalTime.ToUnixTimeMilliseconds() }
        },
        nextCursor = next
    };

    private sealed class Harness : IAsyncDisposable
    {
        public TestData Data { get; } = new();
        public UnifiedSessionTests.SessionServer Server { get; }
        public QqReferenceStore References { get; }
        public RemoteCodex Remote { get; }
        public ConcurrentQueue<(Notification Notice, string? ReplyId)> Sent { get; } = new();
        public ConcurrentQueue<(string Path, Notification Notice, string ReplyId)> Files { get; } = new();
        public Func<CancellationToken, Task>? BeforeFile { get; set; }
        public string Text { get; set; } = "result";
        private Harness(UnifiedSessionTests.SessionServer server)
        {
            Server = server; References = new(Data.Paths);
            Data.Config.Update(c => { c.Qq.Targets.Add(new() { Id = "owner" }); c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "owner"; });
            References.Save("owner", "original", ThreadId, turnId: "old-turn", timestamp: OriginalTime);
            References.Save("owner", "thread-only", ThreadId);
            Server.Pages = [[new { id = ThreadId, name = "test", cwd = Data.Paths.Root, source = "vscode" }]];
            Server.ReplyResult = (method, _) => method switch
            {
                "thread/items/list" => Page("old-turn", Text),
                "thread/read" => new { thread = new { id = ThreadId, cwd = Data.Paths.Root, name = "test" } },
                _ => null
            };
            Remote = new(Data.Config, new(), (_, notice, reply, _) =>
            {
                References.Save("owner", "out-" + (Sent.Count + 1), ThreadId, turnId: notice.Metadata.GetValueOrDefault("turn_id"), timestamp: notice.Timestamp);
                Sent.Enqueue((notice, reply));
                return Task.FromResult(new DeliveryResult(true, notice.Id, "sent"));
            }, async (_, ct) => { await Server.Client.InitializeAsync(ct); return Server.Client; },
                sendFile: async (_, path, notice, reply, ct) => { if (BeforeFile is not null) await BeforeFile(ct); Files.Enqueue((path, notice, reply)); return new DeliveryResult(true, notice.Id, "sent"); },
                wakeDesktop: (_, _) => Task.CompletedTask);
        }
        public static async Task<Harness> CreateAsync() { var h = new Harness(await UnifiedSessionTests.SessionServer.StartAsync()); await h.Remote.ApplySettingsAsync(); return h; }
        public async Task PostAsync(string text, string? reference = "original", int expected = 1)
        {
            var before = Sent.Count + Files.Count;
            await Remote.ProcessAsync(new(Guid.NewGuid().ToString(), "owner", text, DateTimeOffset.UtcNow) { ReferenceIndex = reference });
            await TestData.UntilAsync(() => Sent.Count + Files.Count >= before + expected);
        }
        public async ValueTask DisposeAsync() { await Remote.DisposeAsync(); await Server.DisposeAsync(); Data.Dispose(); }
    }
}

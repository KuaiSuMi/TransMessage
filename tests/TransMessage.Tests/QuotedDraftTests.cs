using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TransMessage.Core.Channels;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class QuotedDraftTests
{
    private const string First = "aaaaaaaa-1111-4111-8111-111111111111";
    private const string Second = "bbbbbbbb-2222-4222-8222-222222222222";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aQf8AAAAASUVORK5CYII=";

    [Theory]
    [InlineData("/d", false)]
    [InlineData("/draft", true)]
    public async Task QuotedDraftCombinesTextAndImageInEitherOrderAndSubmitsOnce(string command, bool imageFirst)
    {
        await using var h = await Harness.CreateAsync();
        Assert.Contains("草稿已收 0 字、0 张图片", (await h.PostAsync(command, "first")).Message);
        if (imageFirst) { await h.PostAsync("", image: true); await h.PostAsync("修复截图里的按钮"); }
        else { await h.PostAsync("修复截图里的按钮"); await h.PostAsync("", image: true); }
        Assert.Empty(h.Turns);
        Assert.Contains("已开始执行", (await h.PostAsync("/s")).Message);
        var turn = Assert.Single(h.Turns).GetProperty("params");
        Assert.Equal(First, turn.GetProperty("threadId").GetString());
        var input = turn.GetProperty("input").EnumerateArray().ToArray(); Assert.Equal(2, input.Length);
        Assert.Contains(input, i => i.GetProperty("type").GetString() == "text" && i.GetProperty("text").GetString() == "修复截图里的按钮");
        var path = Assert.Single(input, i => i.GetProperty("type").GetString() == "localImage").GetProperty("path").GetString()!;
        Assert.Equal(Convert.FromBase64String(Png), await File.ReadAllBytesAsync(path));
        Assert.Contains("没有草稿", (await h.PostAsync("/s", "first")).Message); Assert.Single(h.Turns);
    }

    [Fact]
    public async Task InterleavedDraftsStaySeparateAndAmbiguousUnquotedInputIsRejected()
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync("/d 第一份需求", "first"); await h.PostAsync("/d 第二份需求", "second");
        Assert.Contains("多个图文草稿", (await h.PostAsync("不能猜测目标")).Message);
        await h.PostAsync("", "first", image: true); await h.PostAsync("/s", "first"); await h.PostAsync("/s", "second");
        Assert.Equal(2, h.Turns.Count);
        var a = h.Turns.Single(c => c.GetProperty("params").GetProperty("threadId").GetString() == First).GetProperty("params").GetProperty("input");
        var b = h.Turns.Single(c => c.GetProperty("params").GetProperty("threadId").GetString() == Second).GetProperty("params").GetProperty("input");
        Assert.Equal(2, a.GetArrayLength()); Assert.Single(b.EnumerateArray());
        var aText = string.Join("\n", a.EnumerateArray().Select(i => CodexEvents.Text(i, "text")));
        var bText = string.Join("\n", b.EnumerateArray().Select(i => CodexEvents.Text(i, "text")));
        Assert.Contains("第一份需求", aText); Assert.DoesNotContain("第二份需求", aText);
        Assert.Contains("第二份需求", bText); Assert.DoesNotContain("不能猜测目标", aText + bText);
    }

    [Fact]
    public async Task CancelAndUnknownImageTargetNeverStartATurn()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Contains("请引用", (await h.PostAsync("", image: true)).Message);
        await h.PostAsync("/d", "first");
        Assert.Contains("1 张图片", (await h.PostAsync("", image: true)).Message);
        Assert.Contains("已取消", (await h.PostAsync("/c")).Message);
        Assert.Contains("没有草稿", (await h.PostAsync("/s", "first")).Message);
        Assert.Empty(h.Turns);
    }

    [Fact]
    public async Task ConcurrentManagedCompletionsKeepTheirOwnNamesResultsAndReferences()
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync("运行任务甲", "first"); await h.PostAsync("运行任务乙", "second"); Assert.Equal(2, h.Turns.Count);
        await h.Server.SendAsync(new { method = "item/completed", @params = new { threadId = First, item = new { type = "agentMessage", text = "甲的结果" } } });
        await h.Server.SendAsync(new { method = "item/completed", @params = new { threadId = Second, item = new { type = "agentMessage", text = "乙的结果" } } });
        await h.Server.SendAsync(new { method = "turn/completed", @params = new { threadId = Second, turn = new { status = "completed" } } });
        await h.Server.SendAsync(new { method = "turn/completed", @params = new { threadId = First, turn = new { status = "completed" } } });
        await TestData.UntilAsync(() => h.Notices.Count(n => n.Notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal)) == 2);
        foreach (var (id, result, other) in new[] { (First, "甲的结果", "乙的结果"), (Second, "乙的结果", "甲的结果") })
        {
            var delivered = Assert.Single(h.Notices, n => n.Notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal) && n.Notice.Metadata["thread_id"] == id);
            Assert.Contains(id[^12..], delivered.Notice.Title); Assert.Contains(result, delivered.Notice.Message); Assert.DoesNotContain(other, delivered.Notice.Message);
            Assert.Equal(id, h.References.Find("owner", delivered.Index)!.ThreadId);
        }
    }

    [Fact]
    public async Task FailedSecondTurnWithoutOutputDoesNotReuseFirstTurnsResult()
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync("第一轮", "first");
        await h.Server.SendAsync(new { method = "item/completed", @params = new { threadId = First, item = new { type = "agentMessage", text = "只属于第一轮的结果" } } });
        await h.Server.SendAsync(new { method = "turn/completed", @params = new { threadId = First, turn = new { status = "completed" } } });
        await TestData.UntilAsync(() => h.Notices.Count(n => n.Notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal)) == 1);
        await h.PostAsync("第二轮", "first");
        await h.Server.SendAsync(new { method = "turn/completed", @params = new { threadId = First, turn = new { status = "failed" } } });
        await TestData.UntilAsync(() => h.Notices.Count(n => n.Notice.Title.StartsWith("Codex 本轮结束", StringComparison.Ordinal)) == 2);
        var completion = h.Notices.Last().Notice;
        Assert.Contains("failed", completion.Message); Assert.Contains("无结果摘要", completion.Message);
        Assert.DoesNotContain("只属于第一轮的结果", completion.Message); Assert.Equal(2, h.Turns.Count);
    }

    [Fact]
    public async Task QuotedApprovalAcceptsPlainApproveOnceForTheOriginalRequest()
    {
        await using var h = await Harness.CreateAsync();
        var question = await h.ApprovalAsync(First, 72);
        Assert.Contains("确认处理", (await h.PostAsync("批准", question.Index)).Message);
        var answer = Assert.Single(h.Server.Replies);
        Assert.Equal(72, answer.GetProperty("id").GetInt32()); Assert.Equal("accept", answer.GetProperty("result").GetProperty("decision").GetString());
        var receipt = h.Notices.Last(); Assert.Equal(question.Notice.Metadata["question_code"], receipt.Notice.Metadata["question_code"]);
        await h.PostAsync("批准", question.Index); await h.PostAsync("批准", receipt.Index);
        Assert.Single(h.Server.Replies); Assert.Empty(h.Turns);
    }

    [Fact]
    public async Task AttachmentInputErrorCannotSubmitOrModifyDraftOrAnswerAQuestion()
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync("/d 保留的有效文字", "first");
        var before = h.Server.Calls.Count;
        Assert.Contains("整条消息未提交", (await h.PostAsync("/s 不能追加的文字", "first", invalidAttachment: true)).Message);
        Assert.Equal(before, h.Server.Calls.Count); Assert.Empty(h.Turns);
        await h.PostAsync("/s", "first");
        var input = Assert.Single(h.Turns).GetProperty("params").GetProperty("input");
        Assert.Equal("保留的有效文字", Assert.Single(input.EnumerateArray()).GetProperty("text").GetString());
        var question = await h.QuestionAsync(First, 73);
        before = h.Server.Calls.Count;
        await h.PostAsync("2", question.Index, invalidAttachment: true);
        Assert.Equal(before, h.Server.Calls.Count); Assert.Empty(h.Server.Replies); Assert.Single(h.Turns);
    }

    [Fact]
    public async Task QuotedQuestionAnswersOriginalRequestAndOldReferencesNeverStartNewTurns()
    {
        await using var h = await Harness.CreateAsync();
        var question = await h.QuestionAsync(First, 71);
        var reply = await h.PostAsync("2", question.Index);
        Assert.Contains("确认处理", reply.Message);
        var answer = Assert.Single(h.Server.Replies); Assert.Equal(71, answer.GetProperty("id").GetInt32());
        Assert.Equal("完整检查", answer.GetProperty("result").GetProperty("answers").GetProperty("scope").GetProperty("answers")[0].GetString());
        var receipt = h.Notices.Last(); Assert.Equal(question.Notice.Metadata["question_code"], receipt.Notice.Metadata["question_code"]);
        Assert.Contains("已", (await h.PostAsync("1", question.Index)).Message);
        await h.PostAsync("再次回答", receipt.Index);
        Assert.Single(h.Server.Replies); Assert.Empty(h.Turns);
    }

    [Fact]
    public async Task ConflictingQuotedThreadOrQuestionIsRejectedWithoutSending()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Contains("不同会话", (await h.PostAsync("#" + Second[^12..] + " 错误目标", "first")).Message);
        var questionA = await h.QuestionAsync(First, 81); var questionB = await h.QuestionAsync(Second, 82);
        Assert.Contains("不同会话", (await h.PostAsync("回答 " + questionB.Notice.Metadata["question_code"] + " 1", questionA.Index)).Message);
        Assert.Empty(h.Server.Replies); Assert.Empty(h.Turns);
    }

    [Fact]
    public async Task DraftCannotBeReassignedToAnotherQuestionOnTheSameThread()
    {
        await using var h = await Harness.CreateAsync();
        var firstQuestion = await h.QuestionAsync(First, 91); var secondQuestion = await h.QuestionAsync(First, 92);
        await h.PostAsync("/d 原问题答案", firstQuestion.Index);
        Assert.Contains("另一", (await h.PostAsync("/d 新问题答案", secondQuestion.Index)).Message);
        Assert.Contains("另一", (await h.PostAsync("/s", secondQuestion.Index)).Message);
        Assert.Empty(h.Server.Replies); Assert.Empty(h.Turns);
        await h.PostAsync("/s", firstQuestion.Index);
        Assert.Equal(91, Assert.Single(h.Server.Replies).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task QuotedModelCommandsOnlyChangeThatThreadsNextTurn()
    {
        await using var h = await Harness.CreateAsync();
        await h.PostAsync("/model vision", "first"); await h.PostAsync("/reasoning high", "first");
        var settings = h.Data.Config.Load().RemoteCodex.Threads;
        Assert.Equal("vision", settings.Single(t => t.Id == First).Model); Assert.Equal("high", settings.Single(t => t.Id == First).Effort);
        Assert.Empty(settings.Single(t => t.Id == Second).Model); Assert.Empty(settings.Single(t => t.Id == Second).Effort);
        Assert.Empty(h.Turns);
        await h.PostAsync("按新设置工作", "first");
        var turn = Assert.Single(h.Turns).GetProperty("params"); Assert.Equal("vision", turn.GetProperty("model").GetString()); Assert.Equal("high", turn.GetProperty("effort").GetString());
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TestData Data { get; } = new();
        public UnifiedSessionTests.SessionServer Server { get; }
        public QqReferenceStore References { get; }
        public RemoteCodex Remote { get; }
        public ConcurrentQueue<(string Index, Notification Notice)> Notices { get; } = new();
        public IReadOnlyList<JsonElement> Turns => Server.Calls.Where(c => c.GetProperty("method").GetString() == "turn/start").ToArray();
        private readonly HttpClient _http;
        private int _nextReference;
        private Harness(UnifiedSessionTests.SessionServer server)
        {
            Server = server; References = new(Data.Paths);
            Data.Config.Update(c =>
            {
                c.Qq.Targets.Add(new() { Id = "owner" }); c.RemoteCodex.OwnerId = "owner"; c.RemoteCodex.Enabled = true;
                c.RemoteCodex.Threads.Add(new() { Id = First, Name = "任务甲", Managed = true }); c.RemoteCodex.Threads.Add(new() { Id = Second, Name = "任务乙", Managed = true });
            });
            References.Save("owner", "first", First); References.Save("owner", "second", Second);
            _http = new(new ScriptedHttp((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(Png)) })));
            Remote = new(Data.Config, new(), (owner, notice, _, _) =>
            {
                var index = "out-" + Interlocked.Increment(ref _nextReference);
                if (notice.Metadata.TryGetValue("thread_id", out var thread)) References.Save(owner, index, thread, notice.Metadata.GetValueOrDefault("question_code"));
                Notices.Enqueue((index, notice)); return Task.FromResult(new DeliveryResult(true, notice.Id, "sent"));
            }, async (_, ct) => { await server.Client.InitializeAsync(ct); return server.Client; }, new QqImageCache(Data.Paths, _http));
        }
        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness(await UnifiedSessionTests.SessionServer.StartAsync()); await harness.Remote.ApplySettingsAsync(); return harness;
        }
        public async Task<Notification> PostAsync(string text, string? reference = null, bool image = false, bool invalidAttachment = false)
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                id = Guid.NewGuid().ToString("N"),
                author = new { user_openid = "owner" },
                content = text,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                message_type = reference is null ? 0 : 103,
                message_scene = new { ext = reference is null ? Array.Empty<string>() : new[] { "ref_msg_idx=" + reference } },
                attachments = image || invalidAttachment ? new[] { new { content_type = invalidAttachment ? "application/pdf" : "image/png", url = "https://example.com/image.png", size = Convert.FromBase64String(Png).Length } } : [],
                msg_elements = new[] { new { text = new { text = "旧引用正文不能成为新要求" } } }
            });
            var message = QqMessages.Parse("C2C_MESSAGE_CREATE", payload); Assert.NotNull(message); Assert.Equal(text, message.Text);
            if (invalidAttachment) Assert.NotNull(message.InputError);
            var before = Notices.Count; await Remote.ProcessAsync(message); await TestData.UntilAsync(() => Notices.Count > before);
            return Notices.Last().Notice;
        }
        public async Task<(string Index, Notification Notice)> QuestionAsync(string threadId, int id)
        {
            var before = Notices.Count; await Server.QuestionAsync(threadId, id);
            await TestData.UntilAsync(() => Notices.Count > before && Notices.Last().Notice.Metadata.ContainsKey("question_code"));
            return Notices.Last();
        }
        public async Task<(string Index, Notification Notice)> ApprovalAsync(string threadId, int id)
        {
            var before = Notices.Count; await Server.ApprovalAsync(threadId, id);
            await TestData.UntilAsync(() => Notices.Count > before && Notices.Last().Notice.Metadata.ContainsKey("question_code"));
            return Notices.Last();
        }
        public async ValueTask DisposeAsync() { await Remote.DisposeAsync(); await Server.DisposeAsync(); _http.Dispose(); Data.Dispose(); }
    }
}

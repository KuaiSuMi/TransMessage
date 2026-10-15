using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class RemoteCodexTests
{
    private static void Configure(TestData data, bool managed = false) => data.Config.Update(c =>
    {
        c.Qq.AppId = "test-app"; c.Qq.Targets.Add(new() { Id = "owner" }); c.Qq.Targets.Add(new() { Id = "other" });
        c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "owner"; c.RemoteCodex.Mode = managed ? "managed" : "desktop";
        c.RemoteCodex.Threads.Add(new() { Id = CodexTests.ThreadId, Name = "测试任务", Managed = managed });
    });
    private static QqPrivateMessage Message(string text, string sender = "owner", string? id = null) => new(id ?? Guid.NewGuid().ToString(), sender, text, DateTimeOffset.UtcNow);
    private static RemoteCodex Remote(TestData data, RpcFixture server, ConcurrentQueue<Notification>? notices = null) => new(data.Config, new(), (owner, notice, reply, ct) =>
    {
        Assert.Equal("owner", owner); notices?.Enqueue(notice); return Task.FromResult(new DeliveryResult(true, notice.Id, "sent"));
    }, async (_, ct) => { await server.Client.InitializeAsync(ct); return server.Client; });

    [Fact]
    public async Task DesktopContinuationOnlyQueuesAndRejectsOtherUsersThreadsStaleOrRepeatedMessages()
    {
        using var data = new TestData(); Configure(data); await using var server = await RpcFixture.CreateAsync();
        var notices = new ConcurrentQueue<Notification>(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        var valid = Message("继续 12345678 检查测试");
        await remote.ProcessAsync(valid with { Sender = "other" });
        await remote.ProcessAsync(valid with { Time = DateTimeOffset.UtcNow.AddMinutes(-6) });
        await remote.ProcessAsync(Message("继续 87654321 不允许"));
        await remote.ProcessAsync(valid); await remote.ProcessAsync(valid);
        await remote.ProcessAsync(Message("状态 12345678"));
        await TestData.UntilAsync(() => notices.Any(n => n.Message.Contains("队列中 0 条", StringComparison.Ordinal)));
        Assert.Single(server.Calls, x => x.GetProperty("method").GetString() == "thread/queue/add");
        Assert.DoesNotContain(server.Calls, x => x.GetProperty("method").GetString() is "thread/resume" or "turn/start");
        data.Config.Update(c => c.RemoteCodex.Enabled = false);
        await remote.ProcessAsync(Message("继续 12345678 授权已撤销"));
        Assert.Single(server.Calls, x => x.GetProperty("method").GetString() == "thread/queue/add");
    }
    [Fact]
    public async Task PrivateTaskNamesAreShortWithoutRenamingStoredTasks()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        var name = "修复" + new string('名', 60);
        data.Config.Update(c => c.RemoteCodex.Threads.Single().Name = name);
        var notices = new ConcurrentQueue<Notification>(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        await remote.ProcessAsync(Message("任务")); await remote.ProcessAsync(Message("状态 12345678"));
        await server.SendAsync(new { method = "turn/completed", @params = new { threadId = CodexTests.ThreadId, turn = new { status = "completed" } } });
        await TestData.UntilAsync(() => notices.Count == 3);
        Assert.All(notices, n => { Assert.Contains(TaskName.Short(name), n.Message); Assert.DoesNotContain(name, n.Message); });
        Assert.Equal(name, data.Config.Load().RemoteCodex.Threads.Single().Name);
    }
    [Fact]
    public async Task PrivateRepliesKeepCommandTimeAndCompletionKeepsCodexTime()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        var notices = new ConcurrentQueue<Notification>(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        var original = DateTimeOffset.UtcNow.AddMinutes(-2).ToOffset(TimeSpan.FromHours(5.5));
        var payload = JsonSerializer.SerializeToElement(new { id = "timestamped-command", author = new { user_openid = "owner" }, content = "帮助", timestamp = original.ToString("O") });
        await remote.ProcessAsync(RemoteCodex.ParseMessage("C2C_MESSAGE_CREATE", payload)!);
        await TestData.UntilAsync(() => notices.Count == 1);
        Assert.Equal(original.ToString("O"), notices.Single().Timestamp.ToString("O"));
        var beijing = original.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.StartsWith($"原始时间：{beijing}（北京时间）\n", TransMessage.Core.Security.Sanitizer.QqText(notices.Single()));
        const long completedAt = 1760486400;
        await server.SendAsync(new { method = "turn/completed", @params = new { threadId = CodexTests.ThreadId, turn = new { status = "completed", completedAt } } });
        await TestData.UntilAsync(() => notices.Count == 2);
        var completion = notices.Last(); Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(completedAt), completion.Timestamp); Assert.Equal("original", completion.Metadata["timestamp_source"]);
        await server.SendAsync(new { method = "turn/completed", @params = new { threadId = CodexTests.ThreadId, turn = new { status = "completed", completedAt = (long?)null } } });
        await TestData.UntilAsync(() => notices.Count == 3); Assert.Equal("received", notices.Last().Metadata["timestamp_source"]);
        await server.QuestionAsync(101); await TestData.UntilAsync(() => notices.Count == 4); Assert.Equal("received", notices.Last().Metadata["timestamp_source"]);
    }
    [Fact]
    public void QuestionDeadlineUsesBeijingTimeAcrossMidnight()
    {
        var pending = new PendingInput("A1B2C3D4", CodexTests.ThreadId, "turn", "item/tool/requestUserInput", "请选择", [], false,
            DateTimeOffset.Parse("2026-10-15T23:59:59.1234567Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("有效至 2026-10-16 07:59:59（北京时间）\n", RemoteCodex.Describe(pending));
    }
    [Fact]
    public async Task ManagedContinuationUsesWorkspaceSandboxAndDoesNotRestartBusyTurn()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        await remote.ProcessAsync(Message("继续 12345678 实现功能"));
        var resume = Assert.Single(server.Calls, x => x.GetProperty("method").GetString() == "thread/resume");
        Assert.Equal("workspace-write", resume.GetProperty("params").GetProperty("sandbox").GetString());
        Assert.Equal("on-request", resume.GetProperty("params").GetProperty("approvalPolicy").GetString());
        server.State = "active"; await remote.ProcessAsync(Message("继续 12345678 不应重复启动"));
        Assert.Single(server.Calls, x => x.GetProperty("method").GetString() == "turn/start");
        Assert.DoesNotContain(server.Calls, x => x.GetProperty("method").GetString() == "thread/queue/add");
    }
    [Fact]
    public async Task QuestionsRoundTripToOriginalRequestWithOptionMappingAndSingleUse()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        var notices = new ConcurrentQueue<Notification>(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        await server.QuestionAsync(41, false, true); await TestData.UntilAsync(() => remote.Pending.Count == 1 && !notices.IsEmpty);
        var request = remote.Pending.Single();
        await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(request.Code, "1", false));
        await remote.ProcessAsync(Message($"回答 {request.Code} 2 | 额外说明"));
        await TestData.UntilAsync(() => notices.Any(n => n.Message.Contains("确认处理", StringComparison.Ordinal)));
        var reply = Assert.Single(server.Replies);
        Assert.Equal(41, reply.GetProperty("id").GetInt32());
        Assert.Equal("完整检查", reply.GetProperty("result").GetProperty("answers").GetProperty("scope").GetProperty("answers")[0].GetString());
        Assert.Equal("额外说明", reply.GetProperty("result").GetProperty("answers").GetProperty("note").GetProperty("answers")[0].GetString());
        Assert.Empty(remote.Pending); await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(request.Code, "1 | 重复", false));
    }
    [Fact]
    public async Task ResolvedAndDisconnectedQuestionsCannotBeReusedAndSecretRequiresLocalAnswer()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        var notices = new ConcurrentQueue<Notification>(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        await server.QuestionAsync(5, true); await TestData.UntilAsync(() => remote.Pending.Count == 1 && !notices.IsEmpty);
        var secret = remote.Pending.Single(); Assert.DoesNotContain("sensitive-prompt", notices.Single().Message);
        await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(secret.Code, "private-value", false));
        Assert.Empty(server.Replies);
        await remote.AnswerAsync(secret.Code, "private-value", true); Assert.Single(server.Replies);
        await server.QuestionAsync(6); await TestData.UntilAsync(() => remote.Pending.Count == 1);
        var old = remote.Pending.Single().Code;
        await server.SendAsync(new { method = "serverRequest/resolved", @params = new { threadId = CodexTests.ThreadId, requestId = 6 } });
        await TestData.UntilAsync(() => remote.Pending.Count == 0);
        await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(old, "old-answer", false));
        await server.QuestionAsync(7); await TestData.UntilAsync(() => remote.Pending.Count == 1);
        old = remote.Pending.Single().Code; await server.Client.DisposeAsync(); await TestData.UntilAsync(() => remote.Pending.Count == 0);
        await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(old, "old-answer", false));
        Assert.DoesNotContain(notices, n => n.Message.Contains("private-value", StringComparison.Ordinal));
    }
    [Fact]
    public async Task ApprovalOnlyAcceptsDisplayedSingleOperationAndDisablingInvalidatesIt()
    {
        using var data = new TestData(); Configure(data, true); await using var server = await RpcFixture.CreateAsync();
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        await server.SendAsync(new { id = 8, method = "item/commandExecution/requestApproval", @params = new { threadId = CodexTests.ThreadId, turnId = "turn", command = "dotnet test", cwd = data.Paths.Root, reason = "运行测试", additionalPermissions = (object?)null } });
        await TestData.UntilAsync(() => remote.Pending.Count == 1); var request = remote.Pending.Single(); Assert.True(request.CanApprove);
        await remote.AnswerAsync(request.Code, "accept", false);
        Assert.Equal("accept", server.Replies.Single().GetProperty("result").GetProperty("decision").GetString());
        await server.QuestionAsync(9); await TestData.UntilAsync(() => remote.Pending.Count == 1); var code = remote.Pending.Single().Code;
        data.Config.Update(c => c.RemoteCodex.Enabled = false); await remote.ApplySettingsAsync();
        Assert.Empty(remote.Pending); await Assert.ThrowsAsync<AppException>(() => remote.AnswerAsync(code, "late", false));
    }
    [Theory]
    [InlineData("item/fileChange/requestApproval", "dotnet test", false)]
    [InlineData("item/commandExecution/requestApproval", "echo password=hidden", false)]
    [InlineData("item/commandExecution/requestApproval", "dotnet test", true)]
    public void RedactedOrIncompleteOperationsCannotBeApproved(string method, string command, bool expected)
    {
        var data = JsonSerializer.SerializeToElement(new { threadId = CodexTests.ThreadId, turnId = "turn", command, cwd = "C:\\Code", reason = "test" });
        Assert.Equal(expected, RemoteCodex.ParseRequest(method, data, new()).CanApprove);
    }
    [Fact]
    public void PrivateMessageParserRejectsGroupsAndMalformedInput()
    {
        var payload = JsonSerializer.SerializeToElement(new { id = "1", author = new { user_openid = "owner" }, content = "  任务 ", timestamp = DateTimeOffset.UtcNow });
        Assert.Equal("任务", RemoteCodex.ParseMessage("C2C_MESSAGE_CREATE", payload)!.Text);
        Assert.Null(RemoteCodex.ParseMessage("GROUP_AT_MESSAGE_CREATE", payload));
        Assert.Null(RemoteCodex.ParseMessage("C2C_MESSAGE_CREATE", JsonSerializer.SerializeToElement(new { content = "任务" })));
    }
    [Fact]
    public async Task NewlyCreatedManagedThreadStartsWithoutReadingMissingRolloutAndCanContinueAfterCompletion()
    {
        using var data = new TestData(); Configure(data, true); data.Config.Update(c => c.RemoteCodex.Threads.Clear());
        var notices = new ConcurrentQueue<Notification>();
        await using var server = await RpcFixture.CreateAsync(); await using var remote = Remote(data, server, notices); await remote.ApplySettingsAsync();
        var thread = await remote.CreateManagedAsync(data.Paths.Root, "新会话");
        Assert.Equal("空闲", (await remote.TasksAsync()).Single().State);
        await remote.ContinueAsync(thread.Id, "第一次");
        Assert.DoesNotContain(server.Calls, x => x.GetProperty("method").GetString() is "thread/read" or "thread/resume");
        await Assert.ThrowsAsync<AppException>(() => remote.ContinueAsync(thread.Id, "忙碌时重复"));
        await server.SendAsync(new { method = "turn/completed", @params = new { threadId = thread.Id, turn = new { status = "completed" } } });
        await TestData.UntilAsync(() => notices.Any(n => n.Title.Contains("本轮结束", StringComparison.Ordinal)));
        Assert.Equal("空闲", (await remote.TasksAsync()).Single().State);
        await remote.ContinueAsync(thread.Id, "第二次"); Assert.Equal(2, server.Calls.Count(x => x.GetProperty("method").GetString() == "turn/start"));
    }
    [Fact]
    public void ExtraPermissionsOrMissingAcceptDecisionCannotBeApproved()
    {
        var permissions = JsonSerializer.SerializeToElement(new { threadId = CodexTests.ThreadId, turnId = "turn", command = "dotnet test", additionalPermissions = new { fileSystem = "all" } });
        Assert.False(RemoteCodex.ParseRequest("item/commandExecution/requestApproval", permissions, new()).CanApprove);
        var decisions = JsonSerializer.SerializeToElement(new { threadId = CodexTests.ThreadId, turnId = "turn", command = "dotnet test", availableDecisions = new[] { "decline" } });
        Assert.False(RemoteCodex.ParseRequest("item/commandExecution/requestApproval", decisions, new()).CanApprove);
    }
    [Fact]
    public void OwnerMustBeEnabledPrivateTarget()
    {
        using var data = new TestData(); Assert.False(data.Config.Load().RemoteCodex.Enabled);
        Assert.Throws<AppException>(() => data.Config.Update(c => { c.Qq.Targets.Add(new() { Id = "group", Kind = "group" }); c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "group"; }));
        Assert.False(data.Config.Load().RemoteCodex.Enabled);
    }
}

internal sealed class RpcFixture : IAsyncDisposable
{
    private readonly TcpClient _peer;
    private readonly TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Task _serving;
    public CodexRpc Client { get; }
    public ConcurrentQueue<JsonElement> Calls { get; } = new();
    public ConcurrentQueue<JsonElement> Replies { get; } = new();
    public string State { get; set; } = "idle";
    private RpcFixture(TcpClient client, TcpClient peer)
    {
        _peer = peer; _client = client;
        _reader = new(peer.GetStream(), Encoding.UTF8); _writer = new(peer.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        Client = new(client.GetStream(), client.GetStream()); _serving = ServeAsync();
    }
    public static async Task<RpcFixture> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var peer = await listener.AcceptTcpClientAsync(); return new(client, peer); }
        finally { listener.Stop(); }
    }
    public async Task SendAsync(object value)
    {
        await _write.WaitAsync(); try { await _writer.WriteLineAsync(JsonSerializer.Serialize(value)); } finally { _write.Release(); }
    }
    public Task QuestionAsync(int id, bool secret = false, bool multiple = false)
    {
        var questions = new List<object> { new { id = "scope", question = secret ? "sensitive-prompt" : "检查范围？", isSecret = secret, options = new[] { new { label = "快速检查" }, new { label = "完整检查" } } } };
        if (multiple) questions.Add(new { id = "note", question = "补充说明？", isSecret = false, options = Array.Empty<object>() });
        return SendAsync(new { id, method = "item/tool/requestUserInput", @params = new { threadId = CodexTests.ThreadId, turnId = "turn", questions } });
    }
    private async Task ServeAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
                if (!message.TryGetProperty("id", out var id)) continue;
                if (message.TryGetProperty("method", out var method))
                {
                    Calls.Enqueue(message.Clone());
                    object result = method.GetString() is "thread/read" or "thread/resume" or "thread/start"
                        ? new { thread = new { id = CodexTests.ThreadId, name = "测试任务", cwd = "C:\\Code", status = new { type = State } } }
                        : method.GetString() == "thread/queue/list" ? new { data = Array.Empty<object>() } : new { };
                    await SendAsync(new { id, result });
                }
                else
                {
                    Replies.Enqueue(message.Clone());
                    await SendAsync(new { method = "serverRequest/resolved", @params = new { threadId = CodexTests.ThreadId, requestId = id } });
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync() { await Client.DisposeAsync(); _client.Dispose(); _peer.Dispose(); await _serving; _reader.Dispose(); _writer.Dispose(); }
}

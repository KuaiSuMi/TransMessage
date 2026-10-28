using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class UnifiedSessionTests
{
    private const string DesktopId = "11111111-1111-4111-8111-111111111111";
    private const string OtherId = "22222222-2222-4222-8222-222222222222";
    private const string ManagedId = "33333333-3333-4333-8333-333333333333";
    private static object Thread(string id, object? source = null, string? parent = null, string? preview = null) => new
    {
        id,
        name = "测试会话",
        cwd = @"C:\Project",
        source = source ?? "vscode",
        parentThreadId = parent,
        preview = preview ?? "修复测试",
        model = "vision",
        status = new { type = "idle" }
    };

    private static RemoteCodex Remote(TestData data, SessionServer server, bool managed = false)
    {
        data.Config.Update(c =>
        {
            c.Qq.Targets.Add(new() { Id = "owner" }); c.RemoteCodex.OwnerId = "owner"; c.RemoteCodex.Enabled = true;
            if (managed) c.RemoteCodex.Threads.Add(new() { Id = ManagedId, Name = "托管任务", Managed = true });
        });
        return new(data.Config, new(), (_, notification, _, _) => Task.FromResult(new DeliveryResult(true, notification.Id, "sent")),
            async (_, ct) => { await server.Client.InitializeAsync(ct); return server.Client; });
    }

    [Fact]
    public async Task DiscoveryPagesAndFiltersInternalThreadsWithoutRegistration()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        server.Pages = [[Thread(DesktopId), Thread(Guid.NewGuid().ToString(), new { subAgent = "review" }), Thread(Guid.NewGuid().ToString(), parent: DesktopId),
            Thread(Guid.NewGuid().ToString(), preview: "Generate a concise, single-line task title")], [Thread(OtherId, new { custom = "desktop" })]];
        await using var remote = Remote(data, server, true); await remote.ApplySettingsAsync();
        var tasks = await remote.TasksAsync(); Assert.Equal(3, tasks.Count);
        Assert.Contains(tasks, t => t.Id == ManagedId); Assert.Contains(tasks, t => t.Id == OtherId);
        Assert.False((await remote.ResolveAsync(DesktopId[^12..])).Managed);
        Assert.True((await remote.ResolveAsync(ManagedId[^12..])).Managed);
        Assert.Single(data.Config.Load().RemoteCodex.Threads);
        Assert.Contains(server.Calls, c => c.GetProperty("method").GetString() == "thread/list" && c.GetProperty("params").GetProperty("cursor").GetString() == "page-2");
        Assert.All(server.Calls.Where(c => c.GetProperty("method").GetString() == "thread/list"), c => Assert.False(c.GetProperty("params").GetProperty("archived").GetBoolean()));
    }

    [Fact]
    public async Task DesktopAcceptsMixedInputOnlyThroughSharedQueue()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        server.Pages = [[Thread(DesktopId)]];
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        var input = new object[] { new { type = "text", text = "检查截图" }, new { type = "image", url = "https://example.com/screenshot.png" } };
        Assert.Contains("共享消息队列", await remote.ContinueInputAsync(DesktopId[^12..], input));
        Assert.Equal("CONTROL_DESKTOP_SETTINGS", (await Assert.ThrowsAsync<AppException>(() => remote.ModelCommandAsync(DesktopId, "vision"))).Code);
        Assert.Equal("CONTROL_DESKTOP_SETTINGS", (await Assert.ThrowsAsync<AppException>(() => remote.ReasoningCommandAsync(DesktopId, "high"))).Code);
        var queued = Assert.Single(server.Calls, c => c.GetProperty("method").GetString() == "thread/queue/add");
        Assert.Equal(2, queued.GetProperty("params").GetProperty("input").GetArrayLength());
        Assert.DoesNotContain(server.Calls, c => c.GetProperty("method").GetString() is "thread/resume" or "turn/start" or "config/value/write");
        Assert.Empty(data.Config.Load().RemoteCodex.Threads);
    }

    [Fact]
    public async Task ManagedSettingsPersistAndApplyToTheNextMixedInputTurn()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        await using var remote = Remote(data, server, true); await remote.ApplySettingsAsync();
        Assert.Contains("vision", await remote.ModelCommandAsync(ManagedId, null));
        await remote.ModelCommandAsync(ManagedId, "vision");
        await remote.ReasoningCommandAsync(ManagedId, "high");
        Assert.Equal("CONTROL_REASONING", (await Assert.ThrowsAsync<AppException>(() => remote.ReasoningCommandAsync(ManagedId, "unsupported"))).Code);
        Assert.Equal("CONTROL_MODEL", (await Assert.ThrowsAsync<AppException>(() => remote.ModelCommandAsync(ManagedId, "not-a-model"))).Code);
        var settings = data.Config.Load().RemoteCodex.Threads.Single(); Assert.Equal("vision", settings.Model); Assert.Equal("high", settings.Effort);
        await remote.ContinueInputAsync(ManagedId, [new { type = "text", text = "解释截图" }, new { type = "image", url = "https://example.com/screenshot.png" }]);
        var started = Assert.Single(server.Calls, c => c.GetProperty("method").GetString() == "turn/start").GetProperty("params");
        Assert.Equal("vision", started.GetProperty("model").GetString()); Assert.Equal("high", started.GetProperty("effort").GetString());
        Assert.Equal(2, started.GetProperty("input").GetArrayLength());
        await remote.ReasoningCommandAsync(ManagedId, "low");
        Assert.Equal("high", started.GetProperty("effort").GetString()); Assert.Equal("low", data.Config.Load().RemoteCodex.Threads.Single().Effort);
        Assert.Equal("CONTROL_BUSY", (await Assert.ThrowsAsync<AppException>(() => remote.ContinueAsync(ManagedId, "下一件事"))).Code);
    }

    [Fact]
    public async Task ImageInputRequiresAnAdvertisedImageModel()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        await using var remote = Remote(data, server, true); await remote.ApplySettingsAsync();
        await remote.ModelCommandAsync(ManagedId, "text-only");
        var error = await Assert.ThrowsAsync<AppException>(() => remote.ContinueInputAsync(ManagedId, [new { type = "image", url = "https://example.com/image.png" }]));
        Assert.Equal("CONTROL_MODEL_IMAGE", error.Code);
        Assert.DoesNotContain(server.Calls, c => c.GetProperty("method").GetString() is "turn/start" or "thread/resume");
    }

    [Fact]
    public async Task ManySmallTextFragmentsUseTheSameCharacterBudgetAsOneMessage()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        await using var remote = Remote(data, server, true); await remote.ApplySettingsAsync();
        await remote.ContinueInputAsync(ManagedId, Enumerable.Range(0, 25).Select(i => (object)new { type = "text", text = "补充" + i }).ToArray());
        var turn = Assert.Single(server.Calls, c => c.GetProperty("method").GetString() == "turn/start");
        Assert.Equal(25, turn.GetProperty("params").GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task CreatingManagedSessionDoesNotHideDesktopSessions()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync(); server.Pages = [[Thread(DesktopId)]];
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        var created = await remote.CreateManagedAsync(data.Paths.Root, "新会话"); Assert.Equal(ManagedId, created.Id);
        Assert.True(data.Config.Load().RemoteCodex.Threads.Single().Managed);
        var tasks = await remote.TasksAsync(); Assert.Equal(2, tasks.Count); Assert.Contains(tasks, t => t.Id == DesktopId);
    }

    [Fact]
    public async Task SamePrefixSessionsRouteBySuffixAndAmbiguousSuffixesAreRejected()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        const string samePrefix = "11111111-2222-4222-8222-222222222222";
        server.Pages = [[Thread(DesktopId), Thread(samePrefix)]];
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        Assert.Equal(DesktopId, (await remote.ResolveAsync(DesktopId[^12..])).Id);
        Assert.Equal(samePrefix, (await remote.ResolveAsync(samePrefix[^12..])).Id);
        await remote.ContinueAsync(samePrefix[^12..], "只发送到第二个会话");
        var queued = Assert.Single(server.Calls, c => c.GetProperty("method").GetString() == "thread/queue/add");
        Assert.Equal(samePrefix, queued.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("CONTROL_THREAD", (await Assert.ThrowsAsync<AppException>(() => remote.ResolveAsync(DesktopId[..8]))).Code);
        server.Pages = [[Thread(DesktopId), Thread("22222222-2222-4222-8222-111111111111")]];
        Assert.Equal("CONTROL_THREAD", (await Assert.ThrowsAsync<AppException>(() => remote.ContinueAsync(DesktopId[^12..], "不能猜测"))).Code);
        Assert.Equal(DesktopId, (await remote.ResolveAsync(DesktopId)).Id);
        Assert.Single(server.Calls, c => c.GetProperty("method").GetString() == "thread/queue/add");
    }

    [Fact]
    public async Task RepeatedPageCursorsFailWithoutSendingInput()
    {
        using var data = new TestData(); await using var server = await SessionServer.StartAsync();
        server.Pages = [[Thread(DesktopId)]];
        await using var remote = Remote(data, server); await remote.ApplySettingsAsync();
        server.RepeatCursor = true;
        Assert.Equal("CODEX_SCHEMA", (await Assert.ThrowsAsync<AppException>(() => remote.DiscoverAsync())).Code);
        Assert.DoesNotContain(server.Calls, c => c.GetProperty("method").GetString() is "thread/queue/add" or "turn/start");
    }

    internal sealed class SessionServer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _peer;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly Task _serving;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly ConcurrentDictionary<string, string> _questionThreads = new();
        public CodexRpc Client { get; }
        public object[][] Pages { get; set; } = [[]];
        public bool RepeatCursor { get; set; }
        public ConcurrentQueue<JsonElement> Calls { get; } = new();
        public ConcurrentQueue<JsonElement> Replies { get; } = new();
        private SessionServer(TcpClient client, TcpClient peer)
        {
            _client = client; _peer = peer; Client = new(client.GetStream(), client.GetStream());
            _reader = new(peer.GetStream(), Encoding.UTF8); _writer = new(peer.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            _serving = ServeAsync();
        }
        public static async Task<SessionServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try { var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); return new(client, await listener.AcceptTcpClientAsync()); }
            finally { listener.Stop(); }
        }
        public async Task SendAsync(object value)
        {
            await _write.WaitAsync();
            try { await _writer.WriteLineAsync(JsonSerializer.Serialize(value)); }
            finally { _write.Release(); }
        }
        public Task QuestionAsync(string threadId, int id)
        {
            _questionThreads[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = threadId;
            return SendAsync(new
            {
                id,
                method = "item/tool/requestUserInput",
                @params = new
                {
                    threadId,
                    turnId = "turn-" + threadId,
                    questions = new[] { new { id = "scope", question = "选择范围？", isSecret = false, options = new[] { new { label = "快速检查" }, new { label = "完整检查" } } } }
                }
            });
        }
        public Task ApprovalAsync(string threadId, int id)
        {
            _questionThreads[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = threadId;
            return SendAsync(new
            {
                id,
                method = "item/commandExecution/requestApproval",
                @params = new { threadId, turnId = "turn-" + threadId, command = "dotnet test", cwd = @"C:\Project", reason = "验证改动", availableDecisions = new[] { "accept", "decline" } }
            });
        }
        private async Task ServeAsync()
        {
            try
            {
                while (await _reader.ReadLineAsync() is { } line)
                {
                    using var document = JsonDocument.Parse(line); var call = document.RootElement;
                    if (!call.TryGetProperty("id", out var id)) continue;
                    if (!call.TryGetProperty("method", out var methodElement))
                    {
                        Replies.Enqueue(call.Clone());
                        if (_questionThreads.TryGetValue(id.ToString(), out var threadId))
                            await SendAsync(new { method = "serverRequest/resolved", @params = new { threadId, requestId = id } });
                        continue;
                    }
                    Calls.Enqueue(call.Clone()); var method = methodElement.GetString(); var parameters = call.GetProperty("params");
                    object result;
                    if (method == "thread/list")
                    {
                        var index = parameters.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.String ? 1 : 0;
                        result = new { data = Pages[Math.Min(index, Pages.Length - 1)], nextCursor = RepeatCursor || index + 1 < Pages.Length ? "page-2" : null };
                    }
                    else if (method == "model/list") result = new
                    {
                        data = new[]
                    {
                        new { id = "vision-id", model = "vision", isDefault = true, defaultReasoningEffort = "low", inputModalities = new[] { "text", "image" }, supportedReasoningEfforts = new[] { new { reasoningEffort = "low" }, new { reasoningEffort = "high" } } },
                        new { id = "text-only", model = "text-only", isDefault = false, defaultReasoningEffort = "low", inputModalities = new[] { "text" }, supportedReasoningEfforts = new[] { new { reasoningEffort = "low" } } }
                    },
                        nextCursor = (string?)null
                    };
                    else if (method is "thread/read" or "thread/resume" or "thread/start") result = new { thread = Thread(method == "thread/start" ? ManagedId : parameters.GetProperty("threadId").GetString()!) };
                    else result = new { };
                    await SendAsync(new { id, result });
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync(); _client.Dispose(); _peer.Dispose(); await _serving; _reader.Dispose(); _writer.Dispose();
        }
    }
}

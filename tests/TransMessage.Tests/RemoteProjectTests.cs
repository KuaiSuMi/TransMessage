using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Channels;
using TransMessage.Core.Codex;
using TransMessage.Core.Notifications;

namespace TransMessage.Tests;

public sealed class RemoteProjectTests
{
    private const string NewThread = "aaaaaaaa-1111-4111-8111-123456789abc";

    [Theory]
    [InlineData("/p", "/new 1 检查测试")]
    [InlineData("项目", "新建 1 检查测试")]
    public async Task EnglishAndChineseCommandsCreateTheSelectedProjectAndStartItsFirstRequest(string list, string create)
    {
        await using var h = await Harness.CreateAsync(); var project = h.AddProject("测试项目");
        await h.PostAsync(list); var receipt = await h.PostAsync(create);
        var start = Assert.Single(h.Calls("thread/start")).GetProperty("params");
        Assert.Equal(project.Id, start.GetProperty("projectId").GetString());
        var turn = Assert.Single(h.Calls("turn/start")).GetProperty("params");
        Assert.Equal(NewThread, turn.GetProperty("threadId").GetString());
        Assert.Equal("检查测试", Assert.Single(turn.GetProperty("input").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal(NewThread, receipt.Notice.Metadata["thread_id"]);
        Assert.True(Assert.Single(h.Data.Config.Load().RemoteCodex.Threads).Managed);
    }

    [Fact]
    public async Task PagedNumberSnapshotKeepsDuplicateNamesDistinctWhenNativeOrderChanges()
    {
        await using var h = await Harness.CreateAsync();
        var projects = Enumerable.Range(1, 10).Select(i => h.AddProject(i <= 2 ? "同名项目" : "项目" + i)).ToArray();
        var first = await h.PostAsync("/p");
        Assert.Contains("第 1/2 页", first.Notice.Message); Assert.Contains("项目8", first.Notice.Message); Assert.DoesNotContain("项目9", first.Notice.Message);
        Assert.Equal(4, h.Calls("project/list").Count);
        Assert.Contains(h.Calls("project/list"), c => CodexEvents.Text(c.GetProperty("params"), "cursor") == "projects:9");
        h.Projects.Reverse();
        var second = await h.PostAsync("/p 2");
        Assert.Contains("第 2/2 页", second.Notice.Message); Assert.Contains("项目9", second.Notice.Message); Assert.Contains("项目10", second.Notice.Message);
        Assert.DoesNotContain("同名项目", second.Notice.Message); Assert.Equal(4, h.Calls("project/list").Count);
        await h.PostAsync("/new 2");
        var start = Assert.Single(h.Calls("thread/start")).GetProperty("params");
        Assert.Equal(projects[1].Id, start.GetProperty("projectId").GetString());
        Assert.Equal(projects[1].Roots[0], start.GetProperty("cwd").GetString());
    }

    [Fact]
    public async Task RepeatedBackendCursorStopsWithoutCreatingAThread()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); h.RepeatCursor = true;
        var error = await Assert.ThrowsAsync<AppException>(() => h.Remote.ProjectListAsync());
        Assert.Equal("CODEX_SCHEMA", error.Code); Assert.Empty(h.Calls("thread/start"));
    }

    [Fact]
    public async Task CreationRequiresAPreviouslyDisplayedProjectSnapshot()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("存在但未列出");
        var result = await h.PostAsync("/new 1");
        Assert.Contains("/p", result.Notice.Message); Assert.Empty(h.Calls("thread/start"));
        Assert.Empty(h.Data.Config.Load().RemoteCodex.Threads);
    }

    [Theory]
    [InlineData("/new 0")]
    [InlineData("/new 99")]
    [InlineData("/new abc")]
    public async Task InvalidProjectNumbersDoNotCreateThreads(string command)
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目");
        await h.PostAsync("/p"); await h.PostAsync(command);
        Assert.Empty(h.Calls("thread/start")); Assert.Empty(h.Data.Config.Load().RemoteCodex.Threads);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("changed-root")]
    [InlineData("missing-directory")]
    public async Task ADeletedOrChangedProjectCannotBeOpenedUsingAnOldNumber(string change)
    {
        await using var h = await Harness.CreateAsync(); var project = h.AddProject("测试项目"); await h.PostAsync("/p");
        if (change == "deleted") h.Projects.Clear();
        else if (change == "changed-root") h.Projects[0] = project with { Roots = [h.Directory("replacement")] };
        else Directory.Delete(project.Roots[0]);
        await h.PostAsync("/new 1");
        Assert.Empty(h.Calls("thread/start")); Assert.Empty(h.Data.Config.Load().RemoteCodex.Threads);
    }

    [Fact]
    public async Task EveryRootMustBeAvailableBeforeCreatingAMultiRootThread()
    {
        await using var h = await Harness.CreateAsync();
        h.AddProject("多目录项目", [h.Directory("first"), Path.Combine(h.Data.Paths.Root, "missing")]);
        await h.PostAsync("/p"); await h.PostAsync("/new 1");
        Assert.Empty(h.Calls("thread/start")); Assert.Empty(h.Data.Config.Load().RemoteCodex.Threads);
    }

    [Theory]
    [InlineData("/new 1", false)]
    [InlineData("/p", false)]
    [InlineData("/new 1", true)]
    public async Task CommandAttachmentsAreRejectedBeforeAnyThreadIsCreated(string command, bool unsupported)
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); await h.PostAsync("/p");
        var before = h.Server.Calls.Count;
        await h.PostAsync(new(Guid.NewGuid().ToString(), "owner", command, DateTimeOffset.UtcNow)
        {
            Images = unsupported ? [] : [new("https://example.invalid/image.png", "image/png")],
            InputError = unsupported ? "这条消息包含不支持的附件。" : null
        });
        Assert.Equal(before, h.Server.Calls.Count); Assert.Empty(h.Calls("thread/start")); Assert.Equal(0, h.ImageHttp.Requests);
    }

    [Fact]
    public async Task OversizedInitialRequestDoesNotLeaveAnEmptyCreatedThread()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); await h.PostAsync("/p");
        await h.PostAsync("/new 1 " + new string('x', 6001));
        Assert.Empty(h.Calls("thread/start")); Assert.Empty(h.Calls("turn/start")); Assert.Empty(h.Data.Config.Load().RemoteCodex.Threads);
    }

    [Fact]
    public async Task OtherSendersCannotReadOrCreateProjects()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); await h.PostAsync("/p");
        var calls = h.Server.Calls.Count; var notices = h.Notices.Count;
        foreach (var command in new[] { "/p", "/new 1 执行" })
            await h.Remote.ProcessAsync(new(Guid.NewGuid().ToString(), "other", command, DateTimeOffset.UtcNow));
        Assert.Equal(calls, h.Server.Calls.Count); Assert.Equal(notices, h.Notices.Count); Assert.Empty(h.Calls("thread/start"));
    }

    [Fact]
    public async Task DuplicateQqDeliveryCreatesOnlyOneThread()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); await h.PostAsync("/p");
        var message = new QqPrivateMessage("same-qq-message", "owner", "/new 1", DateTimeOffset.UtcNow);
        await h.PostAsync(message); var count = h.Notices.Count; await h.Remote.ProcessAsync(message);
        Assert.Single(h.Calls("thread/start")); Assert.Single(h.Calls("thread/name/set"));
        Assert.Single(h.Data.Config.Load().RemoteCodex.Threads); Assert.Equal(count, h.Notices.Count);
    }

    [Fact]
    public async Task EmptyCreationPreservesAllRootsAndSupportsQuotedModelSettingsAndTheNextRequest()
    {
        await using var h = await Harness.CreateAsync(); var roots = new[] { h.Directory("main"), h.Directory("extra") };
        var project = h.AddProject("多目录项目", roots); await h.PostAsync("/p"); var created = await h.PostAsync("/new 1");
        var start = Assert.Single(h.Calls("thread/start")).GetProperty("params");
        Assert.Equal(project.Id, start.GetProperty("projectId").GetString()); Assert.Equal(roots[0], start.GetProperty("cwd").GetString());
        Assert.Equal(roots, start.GetProperty("runtimeWorkspaceRoots").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("user", start.GetProperty("threadSource").GetString());
        var named = Assert.Single(h.Calls("thread/name/set")).GetProperty("params");
        Assert.Equal(NewThread, named.GetProperty("threadId").GetString());
        Assert.Equal(Assert.Single(h.Data.Config.Load().RemoteCodex.Threads).Name, named.GetProperty("name").GetString());
        Assert.Equal(NewThread, h.References.Find("owner", created.Index)!.ThreadId); Assert.Empty(h.Calls("turn/start"));
        await h.PostAsync("/model vision", created.Index); await h.PostAsync("/reasoning high", created.Index);
        Assert.Empty(h.Calls("turn/start")); await h.PostAsync("开始检查", created.Index);
        var turn = Assert.Single(h.Calls("turn/start")).GetProperty("params");
        Assert.Equal(NewThread, turn.GetProperty("threadId").GetString()); Assert.Equal("vision", turn.GetProperty("model").GetString()); Assert.Equal("high", turn.GetProperty("effort").GetString());
        Assert.DoesNotContain(h.Server.Calls, c => CodexEvents.Text(c, "method") is "thread/queue/add" or "thread/resume");
    }

    [Fact]
    public async Task InitialTurnFailureKeepsTheCreatedIdAndCanBeRetriedByQuotingItsReceipt()
    {
        await using var h = await Harness.CreateAsync(); h.AddProject("测试项目"); await h.PostAsync("/p"); h.Server.RejectMethod = "turn/start";
        var failed = await h.PostAsync("/new 1 开始检查");
        Assert.Equal(NewThread, failed.Notice.Metadata["thread_id"]); Assert.Contains(NewThread[^12..], failed.Notice.Title + failed.Notice.Message);
        Assert.Contains("Fixture rejected request", failed.Notice.Message); Assert.Equal(NewThread, Assert.Single(h.Data.Config.Load().RemoteCodex.Threads).Id);
        h.Server.RejectMethod = null; await h.PostAsync("重新检查", failed.Index);
        Assert.Single(h.Calls("thread/start")); Assert.Equal(2, h.Calls("turn/start").Count);
        Assert.All(h.Calls("turn/start"), c => Assert.Equal(NewThread, c.GetProperty("params").GetProperty("threadId").GetString()));
    }

    private sealed record Project(string Id, string Name, string[] Roots)
    {
        public object Value => new { id = Id, name = Name, roots = Roots.Select(path => new { path }).ToArray(), metadata = new { }, position = 0, createdAt = 0, updatedAt = 0, recencyAt = (long?)null };
    }

    private sealed class Harness : IAsyncDisposable
    {
        public TestData Data { get; } = new();
        public UnifiedSessionTests.SessionServer Server { get; }
        public RemoteCodex Remote { get; }
        public QqReferenceStore References { get; }
        public List<Project> Projects { get; } = [];
        public bool RepeatCursor { get; set; }
        public ConcurrentQueue<(string Index, Notification Notice)> Notices { get; } = new();
        public ScriptedHttp ImageHttp { get; } = new((_, _, _) => throw new InvalidOperationException("Project commands must not download images."));
        private readonly HttpClient _http;
        private string _directory = "";
        private string _name = "新会话";
        private int _nextReference;
        public IReadOnlyList<JsonElement> Calls(string method) => Server.Calls.Where(c => CodexEvents.Text(c, "method") == method).ToArray();
        private Harness(UnifiedSessionTests.SessionServer server)
        {
            Server = server; References = new(Data.Paths); _http = new(ImageHttp);
            Data.Config.Update(c => { c.Qq.Targets.Add(new() { Id = "owner" }); c.RemoteCodex.Enabled = true; c.RemoteCodex.OwnerId = "owner"; });
            server.ReplyResult = (method, args) =>
            {
                if (method == "project/list")
                {
                    var cursor = CodexEvents.Text(args, "cursor"); var start = cursor is null ? 0 : int.Parse(cursor["projects:".Length..], CultureInfo.InvariantCulture);
                    return new { data = Projects.Skip(start).Take(3).Select(p => p.Value).ToArray(), nextCursor = RepeatCursor ? "projects:3" : start + 3 < Projects.Count ? "projects:" + (start + 3).ToString(CultureInfo.InvariantCulture) : null };
                }
                if (method == "thread/start") _directory = args.GetProperty("cwd").GetString()!;
                if (method == "thread/name/set") { _name = args.GetProperty("name").GetString()!; return new { }; }
                if (method is "thread/start" or "thread/read" or "thread/resume")
                    return new { thread = new { id = NewThread, name = _name, cwd = _directory, source = "appServer", threadSource = "user", model = "vision", status = new { type = "idle" } } };
                if (method == "turn/start") return new { turn = new { id = "initial-turn", status = "inProgress" } };
                return null;
            };
            Remote = new(Data.Config, new(), (owner, notice, _, _) =>
            {
                var index = "project-out-" + Interlocked.Increment(ref _nextReference);
                if (notice.Metadata.TryGetValue("thread_id", out var id)) References.Save(owner, index, id, notice.Metadata.GetValueOrDefault("question_code"));
                Notices.Enqueue((index, notice)); return Task.FromResult(new DeliveryResult(true, notice.Id, "sent"));
            }, async (_, ct) => { await server.Client.InitializeAsync(ct); return server.Client; }, new QqImageCache(Data.Paths, _http),
                wakeDesktop: (_, _) => throw new InvalidOperationException("New project threads use the managed connection."));
        }
        public static async Task<Harness> CreateAsync()
        {
            var h = new Harness(await UnifiedSessionTests.SessionServer.StartAsync()); await h.Remote.ApplySettingsAsync(); return h;
        }
        public string Directory(string name) { var path = Path.Combine(Data.Paths.Root, name); System.IO.Directory.CreateDirectory(path); return path; }
        public Project AddProject(string name, string[]? roots = null)
        {
            var project = new Project(Guid.NewGuid().ToString(), name, roots ?? [Directory("project-" + Projects.Count)]); Projects.Add(project); return project;
        }
        public Task<(string Index, Notification Notice)> PostAsync(string text, string? reference = null) =>
            PostAsync(new(Guid.NewGuid().ToString(), "owner", text, DateTimeOffset.UtcNow) { ReferenceIndex = reference });
        public async Task<(string Index, Notification Notice)> PostAsync(QqPrivateMessage message)
        {
            var before = Notices.Count; await Remote.ProcessAsync(message); await TestData.UntilAsync(() => Notices.Count > before); return Notices.Last();
        }
        public async ValueTask DisposeAsync() { await Remote.DisposeAsync(); await Server.DisposeAsync(); _http.Dispose(); Data.Dispose(); }
    }
}

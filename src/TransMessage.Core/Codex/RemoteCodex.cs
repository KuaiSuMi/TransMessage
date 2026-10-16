using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Codex;

public sealed record QqPrivateMessage(string Id, string Sender, string Text, DateTimeOffset Time);
public sealed record CodexTaskInfo(string Id, string Name, string Directory, string State)
{
    public override string ToString() => $"#{Id[..8]} · {Name}\n{Directory}";
}
public sealed record InputQuestion(string Id, string Text, string[] Options, bool Secret);
public sealed record PendingInput(string Code, string ThreadId, string TurnId, string Method, string Summary, InputQuestion[] Questions, bool CanApprove, DateTimeOffset ExpiresAt);

public sealed class RemoteCodex(ConfigStore config, Sanitizer sanitizer,
    Func<string, Notification, string?, CancellationToken, Task<DeliveryResult>> send,
    Func<RemoteCodexSettings, CancellationToken, Task<CodexRpc>>? connect = null) : IAsyncDisposable
{
    private sealed record Request(PendingInput View, JsonElement Id, TaskCompletionSource<bool> Resolved);
    private readonly ConcurrentDictionary<string, Request> _requests = new();
    private readonly ConcurrentDictionary<string, string> _lastResults = new();
    private readonly ConcurrentDictionary<string, CodexTaskInfo> _loaded = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Channel<QqPrivateMessage> _inbox = Channel.CreateBounded<QqPrivateMessage>(32);
    private readonly Channel<(string Owner, Notification Notification, string? ReplyId)> _notices = Channel.CreateBounded<(string, Notification, string?)>(64);
    private readonly DedupStore _dedup = new(config.Paths);
    private CancellationTokenSource? _stop;
    private Task? _events;
    private Task? _receiving;
    private Task? _delivering;
    private CodexRpc? _rpc;
    private string? _settings;
    private string _owner = "";
    private DateTimeOffset _rateStart;
    private int _rateCount;
    public string Status { get; private set; } = "未启用";
    public IReadOnlyList<PendingInput> Pending => _requests.Where(x => !x.Key.StartsWith("sent:", StringComparison.Ordinal)).Select(x => x.Value.View).OrderBy(x => x.ExpiresAt).ToArray();
    private Task<CodexRpc> ConnectAsync(RemoteCodexSettings settings, CancellationToken ct) => connect?.Invoke(settings, ct) ?? CodexRpc.ConnectAsync(settings.Executable, false, ct);

    public async Task ApplySettingsAsync(CancellationToken ct = default)
    {
        var settings = config.Load().RemoteCodex;
        var fingerprint = JsonSerializer.Serialize(new { settings.Enabled, settings.Mode, settings.OwnerId, settings.Executable });
        if (_settings == fingerprint && _rpc?.IsConnected == true) return;
        await StopAsync(); _settings = fingerprint;
        if (!settings.Enabled) { Status = "未启用"; return; }
        _owner = settings.OwnerId; _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            _rpc = await ConnectAsync(settings, _stop.Token);
            Status = settings.Mode == "desktop" ? "Desktop 共享队列 · 不接管实时审批" : "托管会话 · 支持远程问答";
            // These loops must finish even after the GUI dispatcher has stopped.
            var rpc = _rpc; var token = _stop.Token;
            _events = Task.Run(() => ListenAsync(rpc, token)); _receiving = Task.Run(() => ReceiveAsync(token)); _delivering = Task.Run(() => DeliverAsync(token));
        }
        catch (Exception e) { Status = AppException.Describe(e); throw; }
    }
    public static QqPrivateMessage? ParseMessage(string type, JsonElement data)
    {
        if (type != "C2C_MESSAGE_CREATE" || !data.TryGetProperty("author", out var author)) return null;
        var id = CodexEvents.Text(data, "id"); var sender = CodexEvents.Text(author, "user_openid"); var text = CodexEvents.Text(data, "content");
        if (id is not { Length: > 0 and <= 256 } || sender is not { Length: > 0 and <= 256 } || text is not { Length: > 0 and <= 6000 }
            || !DateTimeOffset.TryParse(CodexEvents.Text(data, "timestamp"), out var time)) return null;
        return new(id, sender, text.Trim(), time);
    }
    private bool Authorized(string sender)
    {
        var c = config.Load();
        return c.RemoteCodex.Enabled && sender == c.RemoteCodex.OwnerId && sender == _owner
            && c.Qq.Targets.Any(t => t.Kind == "c2c" && t.Id == sender && t.Enabled);
    }
    public bool Enqueue(string type, JsonElement data)
    {
        var message = ParseMessage(type, data);
        return message is not null && Authorized(message.Sender) && _inbox.Writer.TryWrite(message);
    }
    private async Task ReceiveAsync(CancellationToken ct)
    {
        try { await foreach (var message in _inbox.Reader.ReadAllAsync(ct)) await ProcessAsync(message, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task ProcessAsync(QqPrivateMessage message, CancellationToken ct = default)
    {
        await _commands.WaitAsync(ct);
        try
        {
            if (!Authorized(message.Sender) || message.Time < DateTimeOffset.UtcNow.AddMinutes(-5) || message.Time > DateTimeOffset.UtcNow.AddMinutes(1)) return;
            if (DateTimeOffset.UtcNow - _rateStart > TimeSpan.FromMinutes(1)) { _rateStart = DateTimeOffset.UtcNow; _rateCount = 0; }
            if (++_rateCount > 12 || !_dedup.Reserve($"qq-command:{config.Load().Qq.AppId}:{message.Sender}:{message.Id}")) return;
            string response;
            try { response = await ExecuteAsync(message.Text, ct); }
            catch (Exception e) { response = AppException.Describe(e); }
            if (Authorized(message.Sender)) await NotifyAsync(message.Sender, "Codex 私聊回复", response, message.Id, ct, message.Time);
        }
        catch (Exception e) when (e is AppException or IOException or OperationCanceledException) { Status = AppException.Describe(e); }
        finally { _commands.Release(); }
    }
    private async Task<string> ExecuteAsync(string text, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Help;
        switch (parts[0])
        {
            case "帮助": return Help;
            case "任务":
                var tasks = await TasksAsync(ct);
                return tasks.Count == 0 ? "尚未授权会话。请在电脑的“QQ 控制”中添加。" : string.Join("\n", tasks.Select(t => $"#{t.Id[..8]} · {TaskName.Short(t.Name, sanitizer)}\n{t.State}"));
            case "状态" when parts.Length == 2:
                var task = Resolve(parts[1]); var info = await TaskStateAsync(task.Id, ct);
                if (!task.Managed)
                {
                    var queue = await Rpc().CallAsync("thread/queue/list", new { threadId = task.Id, limit = 100 }, ct);
                    var count = queue.GetProperty("data").GetArrayLength();
                    return $"#{task.Id[..8]} · {TaskName.Short(task.Name, sanitizer)}\n项目：{info.Directory}\n队列中 {count} 条（最多统计 100 条）。\nDesktop 实时状态未知；队列为空也不能证明已经执行完成。";
                }
                return $"#{task.Id[..8]} · {TaskName.Short(task.Name, sanitizer)}\n{(task.Managed ? info.State : "Desktop 实时状态未知；当前只能访问共享队列")}\n{_lastResults.GetValueOrDefault(task.Id, "尚无本次连接的完成结果。")}";
            case "继续" when parts.Length == 3:
                return await ContinueAsync(parts[1], parts[2], ct);
            case "待回答":
                return Pending.Count == 0 ? "当前连接没有待回答问题。Desktop 队列模式不能接收现有窗口的实时问题。" : string.Join("\n\n", Pending.Select(Describe));
            case "回答" when parts.Length == 3:
                await AnswerAsync(parts[1], parts[2], false, ct); return "回答已由 Codex 确认处理。";
            case "批准" when parts.Length == 2:
                await AnswerAsync(parts[1], "accept", false, ct); return "本次操作已由 Codex 确认处理。";
            case "拒绝" when parts.Length == 2:
                await AnswerAsync(parts[1], "decline", false, ct); return "拒绝已由 Codex 确认处理。";
            default: return Help;
        }
    }
    public const string Help = "任务\n状态 会话编号\n继续 会话编号 你的要求\n待回答\n回答 问题码 答案（多个问题用 | 分隔）\n批准 问题码 / 拒绝 问题码\n\n会话需先在电脑授权；仅主人私聊可控制。";
    private ControlledThread Resolve(string shortId)
    {
        shortId = shortId.TrimStart('#');
        if (shortId.Length < 8) throw new AppException("CONTROL_THREAD", "使用任务列表中的 8 位编号或完整会话 ID。");
        var c = config.Load().RemoteCodex;
        var candidates = c.Threads.Where(t => t.Managed == (c.Mode == "managed") && t.Id.StartsWith(shortId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1) throw new AppException("CONTROL_THREAD", "会话未授权或编号不唯一，请在电脑检查。");
        return candidates[0];
    }
    private CodexRpc Rpc() => _rpc is { IsConnected: true } rpc ? rpc : throw new AppException("CONTROL_OFFLINE", "Codex 控制连接已断开，请在电脑重新连接。未自动重放任何指令。");
    public async Task<IReadOnlyList<CodexTaskInfo>> TasksAsync(CancellationToken ct = default)
    {
        var settings = config.Load().RemoteCodex; var tasks = new List<CodexTaskInfo>();
        foreach (var thread in settings.Threads.Where(t => t.Managed == (settings.Mode == "managed")).Take(10))
        {
            var info = await TaskStateAsync(thread.Id, ct);
            tasks.Add(info with { Name = thread.Name, State = settings.Mode == "desktop" ? "共享队列可查询；Desktop 实时运行状态未知" : info.State });
        }
        return tasks;
    }
    private static async Task<CodexTaskInfo> InspectAsync(CodexRpc rpc, string id, CancellationToken ct)
    {
        var data = await rpc.CallAsync("thread/read", new { threadId = id, includeTurns = false }, ct); return TaskInfo(data.GetProperty("thread"));
    }
    private Task<CodexTaskInfo> TaskStateAsync(string id, CancellationToken ct) => _loaded.TryGetValue(id, out var task)
        ? Task.FromResult(task with { State = _running.ContainsKey(id) ? "运行中 / 可能等待输入" : "空闲" }) : InspectAsync(Rpc(), id, ct);
    private static CodexTaskInfo TaskInfo(JsonElement thread)
    {
        var id = CodexEvents.Text(thread, "id") ?? throw new AppException("CODEX_SCHEMA", "Codex 会话缺少 ID。");
        var state = thread.TryGetProperty("status", out var status) ? CodexEvents.Text(status, "type") : null;
        return new(id, CodexEvents.Text(thread, "name") ?? id[..8], CodexEvents.Text(thread, "cwd") ?? "", state switch { "active" => "运行中 / 可能等待输入", "idle" => "空闲", "notLoaded" => "未在当前连接加载", "systemError" => "后台异常", _ => "状态未知" });
    }
    public async Task<IReadOnlyList<CodexTaskInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        await using var rpc = await ConnectAsync(config.Load().RemoteCodex, ct);
        var result = await rpc.CallAsync("thread/list", new { limit = 100, archived = false, sourceKinds = new[] { "cli", "vscode", "exec" } }, ct);
        return result.GetProperty("data").EnumerateArray().Where(t => Guid.TryParse(CodexEvents.Text(t, "id"), out _)).Select(TaskInfo).ToArray();
    }
    public async Task<string> ProbeDesktopAsync(CancellationToken ct = default)
    {
        try
        {
            await using var rpc = await CodexRpc.ConnectAsync(config.Load().RemoteCodex.Executable, true, ct);
            var loaded = await rpc.CallAsync("thread/loaded/list", new { }, ct);
            return $"官方实时控制 socket 可连接，当前加载 {loaded.GetProperty("data").GetArrayLength()} 个会话。此版本的 Desktop 模式仅使用共享队列，不代理该连接的审批。";
        }
        catch (Exception e) when (e is AppException or IOException or TimeoutException)
        { return "当前 Desktop 没有可连接的官方实时控制通道。可使用共享队列追加消息，但不能从 QQ 回答该窗口的待输入问题。"; }
    }
    public async Task<CodexTaskInfo> CreateManagedAsync(string directory, string name, CancellationToken ct = default)
    {
        if (config.Load().RemoteCodex.Mode != "managed") throw new AppException("CONTROL_MODE", "先选择托管会话模式。");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new AppException("CONTROL_DIRECTORY", "请选择存在的项目目录。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new AppException("CONTROL_NAME", "会话名称需要 1–100 个字符。");
        var result = await Rpc().CallAsync("thread/start", new { cwd = directory, sandbox = "workspace-write", approvalPolicy = "on-request", approvalsReviewer = "user", config = new Dictionary<string, object> { ["features.default_mode_request_user_input"] = true } }, ct);
        var info = TaskInfo(result.GetProperty("thread"));
        config.Update(c => c.RemoteCodex.Threads.Add(new() { Id = info.Id, Name = name.Trim(), Managed = true }));
        _loaded[info.Id] = info;
        return info with { Name = name.Trim() };
    }
    public async Task<string> ContinueAsync(string id, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 6000) throw new AppException("CONTROL_INPUT", "要求需要 1–6000 个字符。");
        var thread = Resolve(id); var rpc = Rpc();
        var input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        if (!thread.Managed)
        {
            await rpc.CallAsync("thread/queue/add", new { threadId = thread.Id, input, clientUserMessageId = Guid.NewGuid().ToString() }, ct);
            return $"#{thread.Id[..8]} 已加入共享消息队列，等待 Desktop 处理。已排队不代表已经执行；本连接不会恢复或接管该会话。";
        }
        if (!_loaded.ContainsKey(thread.Id))
        {
            var data = await rpc.CallAsync("thread/read", new { threadId = thread.Id, includeTurns = false }, ct);
            if (CodexEvents.Text(data.GetProperty("thread").GetProperty("status"), "type") == "active") throw Busy();
            var resumed = await rpc.CallAsync("thread/resume", new { threadId = thread.Id, excludeTurns = true, sandbox = "workspace-write", approvalPolicy = "on-request", approvalsReviewer = "user", config = new Dictionary<string, object> { ["features.default_mode_request_user_input"] = true } }, ct);
            _loaded[thread.Id] = TaskInfo(resumed.GetProperty("thread"));
        }
        if (!_running.TryAdd(thread.Id, 0)) throw Busy();
        try { await rpc.CallAsync("turn/start", new { threadId = thread.Id, input }, ct); }
        catch (AppException e) when (e.Code == "CODEX_RPC") { _running.TryRemove(thread.Id, out _); throw; }
        return $"#{thread.Id[..8]} 已开始执行。需要回答或本轮结束时会私聊通知。";
    }
    private static AppException Busy() => new("CONTROL_BUSY", "会话正在运行或等待回答。请先回答问题，或等本轮结束后继续。");
    private async Task ListenAsync(CodexRpc rpc, CancellationToken ct)
    {
        try
        {
            await foreach (var message in rpc.Messages.ReadAllAsync(ct))
            {
                var method = CodexEvents.Text(message, "method");
                if (!message.TryGetProperty("params", out var data)) continue;
                if (method == "serverRequest/resolved" && data.TryGetProperty("requestId", out var resolved))
                {
                    foreach (var pair in _requests.Where(x => x.Value.Id.GetRawText() == resolved.GetRawText() && x.Value.View.ThreadId == CodexEvents.Text(data, "threadId")))
                        if (_requests.TryRemove(pair.Key, out var request)) request.Resolved.TrySetResult(true);
                    continue;
                }
                var threadId = CodexEvents.Text(data, "threadId");
                var allowed = config.Load().RemoteCodex.Threads.FirstOrDefault(t => t.Id == threadId && t.Managed);
                if (allowed is null || config.Load().RemoteCodex.Mode != "managed") continue;
                if (message.TryGetProperty("id", out var requestId))
                {
                    if (method is "item/tool/requestUserInput" or "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                    {
                        var pending = ParseRequest(method, data, sanitizer);
                        if (_requests.Count >= 50) { await rpc.RejectAsync(requestId, ct); continue; }
                        _requests[pending.Code] = new(pending, requestId.Clone(), new(TaskCreationOptions.RunContinuationsAsynchronously));
                        await NotifyAsync(_owner, $"Codex 等待你 · #{threadId![..8]}", Describe(pending), null, ct);
                    }
                    else { await rpc.RejectAsync(requestId, ct); await NotifyAsync(_owner, "Codex 请求无法远程处理", "此类请求未获支持，已拒绝。请在电脑查看并调整任务。", null, ct); }
                }
                else if (method == "item/completed" && data.TryGetProperty("item", out var item) && CodexEvents.Text(item, "type") == "agentMessage")
                    _lastResults[threadId!] = Sanitizer.Truncate(sanitizer.Redact(CodexEvents.Text(item, "text")), 900);
                else if (method == "turn/completed")
                {
                    _running.TryRemove(threadId!, out _);
                    ClearRequests(threadId);
                    var state = data.TryGetProperty("turn", out var turn) ? CodexEvents.Text(turn, "status") : "unknown";
                    DateTimeOffset? completedAt = null;
                    if (turn.ValueKind == JsonValueKind.Object && turn.TryGetProperty("completedAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var seconds)
                        && seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                        completedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    await NotifyAsync(_owner, $"Codex 本轮结束 · #{threadId![..8]}", $"{TaskName.Short(allowed.Name, sanitizer)}\n状态：{state}\n{_lastResults.GetValueOrDefault(threadId!, "无结果摘要")}\n\n继续 {threadId![..8]} 下一步要求", null, ct, completedAt);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { Status = AppException.Describe(e); }
        finally { ClearRequests(null); if (!ct.IsCancellationRequested) Status = "Codex 控制已断开；待回答问题已失效，请重新连接。"; }
    }
    public static PendingInput ParseRequest(string method, JsonElement data, Sanitizer sanitizer)
    {
        var questions = new List<InputQuestion>();
        if (method == "item/tool/requestUserInput")
        {
            foreach (var q in data.GetProperty("questions").EnumerateArray())
            {
                var secret = q.TryGetProperty("isSecret", out var flag) && flag.ValueKind == JsonValueKind.True;
                var options = q.TryGetProperty("options", out var choices) && choices.ValueKind == JsonValueKind.Array
                    ? choices.EnumerateArray().Select(x => CodexEvents.Text(x, "label") ?? "").ToArray() : [];
                questions.Add(new(CodexEvents.Text(q, "id") ?? throw new JsonException(), sanitizer.Redact(CodexEvents.Text(q, "question")), options, secret));
            }
        }
        var raw = method == "item/commandExecution/requestApproval" ? $"命令：{CodexEvents.Text(data, "command")}\n目录：{CodexEvents.Text(data, "cwd")}\n原因：{CodexEvents.Text(data, "reason")}" : "文件修改请求；完整差异请在电脑核对。本版不远程批准缺少差异的请求。";
        var summary = questions.Count > 0 ? "需要补充信息" : sanitizer.Redact(raw);
        var approve = method == "item/commandExecution/requestApproval" && !string.IsNullOrEmpty(CodexEvents.Text(data, "command")) && raw.Length <= 1200 && summary == raw
            && (!data.TryGetProperty("additionalPermissions", out var permissions) || permissions.ValueKind == JsonValueKind.Null)
            && (!data.TryGetProperty("networkApprovalContext", out var network) || network.ValueKind == JsonValueKind.Null)
            && (!data.TryGetProperty("availableDecisions", out var decisions) || decisions.ValueKind == JsonValueKind.Null || decisions.ValueKind == JsonValueKind.Array && decisions.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "accept"));
        var pending = new PendingInput(Convert.ToHexString(RandomNumberGenerator.GetBytes(4)), data.GetProperty("threadId").GetString()!, data.GetProperty("turnId").GetString()!, method,
            Sanitizer.Truncate(summary, 1400), questions.ToArray(), approve, DateTimeOffset.UtcNow.AddMinutes(15));
        var preview = Sanitizer.QqText(new() { Title = $"Codex 等待你 · #{pending.ThreadId[..8]}", Message = Describe(pending), Metadata = new() { ["timestamp_source"] = "received" } });
        return preview.Contains("…内容过长，已截断", StringComparison.Ordinal) ? pending with { CanApprove = false } : pending;
    }
    public static string Describe(PendingInput pending)
    {
        var expires = pending.ExpiresAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var text = $"问题码 {pending.Code} · 有效至 {expires}（北京时间）\n{pending.Summary}";
        foreach (var q in pending.Questions.Select((q, i) => (q, i)))
            text += $"\n{q.i + 1}. {(q.q.Secret ? "敏感问题，请在电脑回答" : q.q.Text)}" + (q.q.Secret ? "" : string.Concat(q.q.Options.Select((o, i) => $"\n  {i + 1}) {o}")));
        return text + (pending.Questions.Length > 0 ? $"\n回答 {pending.Code} 答案" + (pending.Questions.Length > 1 ? "1 | 答案2（按问题顺序）" : "（可填选项编号）")
            : pending.CanApprove ? $"\n批准 {pending.Code} / 拒绝 {pending.Code}（仅本次）" : $"\n仅允许拒绝 {pending.Code}，不能远程批准。");
    }
    public async Task AnswerAsync(string code, string answer, bool local, CancellationToken ct = default)
    {
        code = code.ToUpperInvariant();
        if (!_requests.TryGetValue(code, out var request) || request.View.ExpiresAt < DateTimeOffset.UtcNow)
            throw new AppException("CONTROL_EXPIRED", "问题已结束、过期或连接已更换；未发送回答。");
        if (!Authorized(_owner)) throw new AppException("CONTROL_OWNER", "控制授权已撤销。");
        _ = Resolve(request.View.ThreadId);
        object result;
        if (request.View.Questions.Length > 0)
        {
            if (!local && request.View.Questions.Any(q => q.Secret)) throw new AppException("CONTROL_SECRET", "敏感问题只能在电脑回答，请勿通过 QQ 发送凭证。");
            var parts = answer.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != request.View.Questions.Length || parts.Any(string.IsNullOrWhiteSpace) || answer.Length > 6000)
                throw new AppException("CONTROL_ANSWER", "请按顺序回答全部问题，多个答案用 | 分隔。");
            var answers = new Dictionary<string, object>();
            for (var i = 0; i < parts.Length; i++)
            {
                var q = request.View.Questions[i];
                var value = int.TryParse(parts[i], out var choice) && choice > 0 && choice <= q.Options.Length ? q.Options[choice - 1] : parts[i];
                answers.Add(q.Id, new { answers = new[] { value } });
            }
            result = new { answers };
        }
        else
        {
            if (answer is not ("accept" or "decline") || answer == "accept" && !request.View.CanApprove) throw new AppException("CONTROL_APPROVAL", "该请求只能拒绝，或需要在电脑查看完整内容。");
            result = new { decision = answer };
        }
        if (!_requests.TryRemove(code, out _)) throw new AppException("CONTROL_EXPIRED", "此问题已被处理。");
        // Keep the request only for its acknowledgement; no second answer may reuse the code.
        var ackKey = "sent:" + code; _requests[ackKey] = request;
        try
        {
            await Rpc().ReplyAsync(request.Id, result, ct);
            if (!await request.Resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)) throw new AppException("CONTROL_EXPIRED", "问题已由其他操作结束，回答未确认。");
        }
        catch (TimeoutException) { throw new AppException("CONTROL_ANSWER_UNCERTAIN", "回答已发出，但未收到 Codex 确认；请查询任务状态，不要重复批准。"); }
        finally { _requests.TryRemove(ackKey, out _); }
    }
    private Task NotifyAsync(string owner, string title, string text, string? replyId, CancellationToken ct, DateTimeOffset? originalTimestamp = null)
    {
        if (!Authorized(owner) || ct.IsCancellationRequested) return Task.CompletedTask;
        var notification = sanitizer.Clean(new Notification
        {
            Title = title,
            Message = text,
            Source = "qq-control",
            Timestamp = originalTimestamp ?? DateTimeOffset.UtcNow,
            Metadata = new() { ["timestamp_source"] = originalTimestamp.HasValue ? "original" : "received" }
        });
        if (!_notices.Writer.TryWrite((owner, notification, replyId))) Status = "私聊回复队列已满，请在电脑查看待回答问题。";
        return Task.CompletedTask;
    }
    private async Task DeliverAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _notices.Reader.ReadAllAsync(ct))
            {
                if (!Authorized(item.Owner)) continue;
                try { var result = await send(item.Owner, item.Notification, item.ReplyId, ct); if (!result.Ok) Status = "QQ 私聊回复失败：" + result.Error; }
                catch (Exception e) { Status = AppException.Describe(e); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private void ClearRequests(string? thread)
    {
        foreach (var pair in _requests.Where(x => thread is null || x.Value.View.ThreadId == thread))
            if (_requests.TryRemove(pair.Key, out var request)) request.Resolved.TrySetResult(false);
    }
    private async Task StopAsync()
    {
        if (_stop is not null) await _stop.CancelAsync();
        ClearRequests(null);
        if (_rpc is not null) await _rpc.DisposeAsync();
        foreach (var task in new[] { _events, _receiving, _delivering }) if (task is not null) { try { await task; } catch (OperationCanceledException) { } }
        _rpc = null; _stop?.Dispose(); _stop = null;
        _loaded.Clear(); _running.Clear(); _lastResults.Clear();
        while (_inbox.Reader.TryRead(out _)) { }
        while (_notices.Reader.TryRead(out _)) { }
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}

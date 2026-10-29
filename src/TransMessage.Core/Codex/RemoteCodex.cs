using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using TransMessage.Core.Channels;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Codex;

public sealed record CodexTaskInfo(string Id, string Name, string Directory, string State)
{
    public override string ToString() => $"#{Id[^12..]} · {Name}\n{Directory}";
}
public sealed record InputQuestion(string Id, string Text, string[] Options, bool Secret);
public sealed record PendingInput(string Code, string ThreadId, string TurnId, string Method, string Summary, InputQuestion[] Questions, bool CanApprove, DateTimeOffset ExpiresAt);

public sealed partial class RemoteCodex(ConfigStore config, Sanitizer sanitizer,
    Func<string, Notification, string?, CancellationToken, Task<DeliveryResult>> send,
    Func<RemoteCodexSettings, CancellationToken, Task<CodexRpc>>? connect = null,
    QqImageCache? imageCache = null,
    Func<string, string, Notification, string, CancellationToken, Task<DeliveryResult>>? sendFile = null,
    Func<string, CancellationToken, Task>? wakeDesktop = null) : IAsyncDisposable
{
    private sealed record Request(PendingInput View, JsonElement Id, TaskCompletionSource<bool> Resolved);
    private readonly ConcurrentDictionary<string, Request> _requests = new();
    private readonly ConcurrentDictionary<string, string> _lastResults = new();
    private readonly ConcurrentDictionary<string, CodexTaskInfo> _loaded = new();
    private readonly ConcurrentDictionary<string, byte> _running = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Channel<QqPrivateMessage> _inbox = Channel.CreateBounded<QqPrivateMessage>(32);
    private readonly Channel<(string Owner, Notification Notification, string? ReplyId)> _notices = Channel.CreateBounded<(string, Notification, string?)>(64);
    private readonly Channel<(RoutedMessage Route, QqPrivateMessage Message)> _exports = Channel.CreateBounded<(RoutedMessage, QqPrivateMessage)>(8);
    private readonly DedupStore _dedup = new(config.Paths);
    private readonly QqReferenceStore _references = new(config.Paths);
    private readonly QqImageCache _images = imageCache ?? new(config.Paths);
    private CancellationTokenSource? _stop;
    private Task? _events;
    private Task? _receiving;
    private Task? _delivering;
    private Task? _exporting;
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
        var fingerprint = JsonSerializer.Serialize(new { settings.Enabled, settings.OwnerId, settings.Executable });
        if (_settings == fingerprint && _rpc?.IsConnected == true) return;
        await StopAsync(); _settings = fingerprint;
        if (!settings.Enabled) { Status = "未启用"; return; }
        _owner = settings.OwnerId; _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            _rpc = await ConnectAsync(settings, _stop.Token);
            Status = "已连接 · Desktop 共享队列 / 托管实时问答";
            // These loops must finish even after the GUI dispatcher has stopped.
            var rpc = _rpc; var token = _stop.Token;
            _events = Task.Run(() => ListenAsync(rpc, token)); _receiving = Task.Run(() => ReceiveAsync(token)); _delivering = Task.Run(() => DeliverAsync(token));
            _exporting = Task.Run(() => ExportAsync(token));
        }
        catch (Exception e) { Status = AppException.Describe(e); throw; }
    }
    public static QqPrivateMessage? ParseMessage(string type, JsonElement data) => QqMessages.Parse(type, data);
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
            string? response; RoutedMessage route = new("", message.Text, null, null);
            try
            {
                if (message.InputError is not null) throw new AppException("CONTROL_MESSAGE", message.InputError);
                route = await RouteAsync(message, ct); response = await ExecuteAsync(route, message, ct);
            }
            catch (Exception e) { response = AppException.Describe(e); }
            if (response is not null && Authorized(message.Sender)) await NotifyAsync(message.Sender, route.Thread is { } thread ? $"Codex · #{thread.Id[^12..]} · {TaskName.Short(thread.Name, sanitizer)}" : "Codex 私聊回复", response, message.Id, ct, message.Time, route.Thread?.Id, route.QuestionCode);
        }
        catch (Exception e) when (e is AppException or IOException or OperationCanceledException) { Status = AppException.Describe(e); }
        finally { _commands.Release(); }
    }
    private CodexRpc Rpc() => _rpc is { IsConnected: true } rpc ? rpc : throw new AppException("CONTROL_OFFLINE", "Codex 控制连接已断开，请在电脑重新连接。");
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
                if (allowed is null) continue;
                if (message.TryGetProperty("id", out var requestId))
                {
                    if (method is "item/tool/requestUserInput" or "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
                    {
                        var pending = ParseRequest(method, data, sanitizer);
                        if (_requests.Count >= 50) { await rpc.RejectAsync(requestId, ct); continue; }
                        _requests[pending.Code] = new(pending, requestId.Clone(), new(TaskCreationOptions.RunContinuationsAsynchronously));
                        await NotifyAsync(_owner, $"Codex 等待你 · #{threadId![^12..]}", Describe(pending), null, ct, threadId: threadId, questionCode: pending.Code);
                    }
                    else { await rpc.RejectAsync(requestId, ct); await NotifyAsync(_owner, $"Codex 请求需电脑处理 · #{threadId![^12..]}", "请求已拒绝，请在电脑查看并调整任务。", null, ct, threadId: threadId); }
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
                    await NotifyAsync(_owner, $"Codex 本轮结束 · #{threadId![^12..]}", $"{TaskName.Short(allowed.Name, sanitizer)}\n状态：{state}\n{_lastResults.GetValueOrDefault(threadId!, "无结果摘要")}\n\n引用此消息回复下一步，或用 /full 获取全文、/file 获取附件。", null, ct, completedAt, threadId, turnId: CodexEvents.Text(turn, "id"));
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
        var raw = method == "item/commandExecution/requestApproval" ? $"命令：{CodexEvents.Text(data, "command")}\n目录：{CodexEvents.Text(data, "cwd")}\n原因：{CodexEvents.Text(data, "reason")}" : "文件修改请求，请在电脑核对完整差异并批准。";
        var summary = questions.Count > 0 ? "需要补充信息" : sanitizer.Redact(raw);
        var approve = method == "item/commandExecution/requestApproval" && !string.IsNullOrEmpty(CodexEvents.Text(data, "command")) && raw.Length <= 1200 && summary == raw
            && (!data.TryGetProperty("additionalPermissions", out var permissions) || permissions.ValueKind == JsonValueKind.Null)
            && (!data.TryGetProperty("networkApprovalContext", out var network) || network.ValueKind == JsonValueKind.Null)
            && (!data.TryGetProperty("availableDecisions", out var decisions) || decisions.ValueKind == JsonValueKind.Null || decisions.ValueKind == JsonValueKind.Array && decisions.EnumerateArray().Any(d => d.ValueKind == JsonValueKind.String && d.GetString() == "accept"));
        var pending = new PendingInput(Convert.ToHexString(RandomNumberGenerator.GetBytes(4)), data.GetProperty("threadId").GetString()!, data.GetProperty("turnId").GetString()!, method,
            Sanitizer.Truncate(summary, 1400), questions.ToArray(), approve, DateTimeOffset.UtcNow.AddMinutes(15));
        var preview = Sanitizer.QqText(new() { Title = $"Codex 等待你 · #{pending.ThreadId[^12..]}", Message = Describe(pending), Metadata = new() { ["timestamp_source"] = "received" } });
        return preview.Contains("…内容过长，已截断", StringComparison.Ordinal) ? pending with { CanApprove = false } : pending;
    }
    public static string Describe(PendingInput pending)
    {
        var expires = pending.ExpiresAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var text = $"问题码 {pending.Code} · 有效至 {expires}（北京时间）\n{pending.Summary}";
        foreach (var q in pending.Questions.Select((q, i) => (q, i)))
            text += $"\n{q.i + 1}. {(q.q.Secret ? "敏感问题，请在电脑回答" : q.q.Text)}" + (q.q.Secret ? "" : string.Concat(q.q.Options.Select((o, i) => $"\n  {i + 1}) {o}")));
        return text + (pending.Questions.Length > 0 ? $"\n回答 {pending.Code} 答案" + (pending.Questions.Length > 1 ? "1 | 答案2（按问题顺序）" : "（可填选项编号）")
            : pending.CanApprove ? $"\n批准 {pending.Code} / 拒绝 {pending.Code}（仅本次）" : $"\n请在电脑批准，或回复 拒绝 {pending.Code}。");
    }
    public async Task AnswerAsync(string code, string answer, bool local, CancellationToken ct = default)
    {
        code = code.ToUpperInvariant();
        if (!_requests.TryGetValue(code, out var request) || request.View.ExpiresAt < DateTimeOffset.UtcNow)
            throw new AppException("CONTROL_EXPIRED", "问题已结束、过期或连接已更换，请查看最新问题通知。");
        if (!Authorized(_owner)) throw new AppException("CONTROL_OWNER", "控制授权已撤销。");
        _ = await ResolveAsync(request.View.ThreadId, ct);
        object result;
        if (request.View.Questions.Length > 0)
        {
            if (!local && request.View.Questions.Any(q => q.Secret)) throw new AppException("CONTROL_SECRET", "请在电脑回答此敏感问题。");
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
            if (answer is not ("accept" or "decline") || answer == "accept" && !request.View.CanApprove) throw new AppException("CONTROL_APPROVAL", "请按通知指引批准或拒绝；需要核对完整内容的请求请在电脑批准。");
            result = new { decision = answer };
        }
        if (!_requests.TryRemove(code, out _)) throw new AppException("CONTROL_EXPIRED", "此问题已被处理。");
        // Keep the request only for its acknowledgement; no second answer may reuse the code.
        var ackKey = "sent:" + code; _requests[ackKey] = request;
        try
        {
            await Rpc().ReplyAsync(request.Id, result, ct);
            if (!await request.Resolved.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)) throw new AppException("CONTROL_EXPIRED", "问题已由其他操作结束。");
        }
        catch (TimeoutException) { throw new AppException("CONTROL_ANSWER_UNCERTAIN", "回答已发出，确认结果暂不可用。请查询任务状态。"); }
        finally { _requests.TryRemove(ackKey, out _); }
    }
    private Task NotifyAsync(string owner, string title, string text, string? replyId, CancellationToken ct, DateTimeOffset? originalTimestamp = null, string? threadId = null, string? questionCode = null, string? turnId = null)
    {
        if (!Authorized(owner) || ct.IsCancellationRequested) return Task.CompletedTask;
        var metadata = new Dictionary<string, string> { ["timestamp_source"] = originalTimestamp.HasValue ? "original" : "received" };
        if (threadId is not null) metadata["thread_id"] = threadId;
        if (questionCode is not null) metadata["question_code"] = questionCode;
        if (turnId is not null) metadata["turn_id"] = turnId;
        var notification = sanitizer.Clean(new Notification
        {
            Title = title,
            Message = text,
            Source = "qq-control",
            Timestamp = originalTimestamp ?? DateTimeOffset.UtcNow,
            Metadata = metadata
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
        foreach (var task in new[] { _events, _receiving, _delivering, _exporting }) if (task is not null) { try { await task; } catch (OperationCanceledException) { } }
        _rpc = null; _stop?.Dispose(); _stop = null;
        _loaded.Clear(); _running.Clear(); _lastResults.Clear();
        _drafts.Clear();
        _projectChoices = null;
        while (_inbox.Reader.TryRead(out _)) { }
        while (_notices.Reader.TryRead(out _)) { }
        while (_exports.Reader.TryRead(out _)) { }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _images.Dispose(); }
}

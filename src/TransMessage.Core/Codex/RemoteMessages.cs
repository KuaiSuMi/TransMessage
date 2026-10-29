using System.Collections.Concurrent;
using TransMessage.Core.Channels;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Codex;

public sealed partial class RemoteCodex
{
    private sealed record RoutedMessage(string Command, string Content, ControlledThread? Thread, string? QuestionCode, QqReference? Reference = null);
    private sealed class Draft(string owner, ControlledThread thread, string? questionCode)
    {
        public string Owner { get; } = owner;
        public ControlledThread Thread { get; } = thread;
        public string? QuestionCode { get; } = questionCode;
        public List<object> Input { get; } = [];
        public List<string> Text { get; } = [];
        public int Images { get; set; }
        public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);
    }
    private readonly ConcurrentDictionary<string, Draft> _drafts = new();
    public const string Help = "任务\n状态 会话编号\n#会话编号 你的要求（也可引用通知直接回复）\n引用通知后 /d 开始图文草稿（也接受 /draft）\n/s 发送草稿 · /c 取消草稿\n引用通知后 /model 查看模型 · /model 模型名\n引用通知后 /reasoning 查看档位 · /reasoning high\n引用完成通知后 /full [页码] 阅读全文（每页最多 4 段）\n引用完成通知后 /file [页码] 获取附件（每页最多 3 个）\n省略页码从第 1 页开始；引用回复，按下一页提示发送 /full 2 或 /file 2 继续。\n待回答\n回答 问题码 答案（多个问题用 | 分隔）\n批准 问题码 / 拒绝 问题码\n\n绑定的本人私聊可直接控制会话。多个草稿同时存在时请引用或带 #编号。";

    private static string Pop(ref string text)
    {
        var parts = text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        text = parts.Length > 1 ? parts[1].Trim() : "";
        return parts.Length > 0 ? parts[0] : "";
    }
    private async Task<RoutedMessage> RouteAsync(QqPrivateMessage message, CancellationToken ct)
    {
        foreach (var pair in _drafts.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow)) _drafts.TryRemove(pair.Key, out _);
        var text = message.Text.Trim(); var command = ""; string? explicitId = null;
        var remaining = text; var first = Pop(ref remaining);
        if (first.StartsWith('#')) { explicitId = first[1..]; text = remaining; remaining = text; first = Pop(ref remaining); }
        if (first is "帮助" or "任务" or "待回答") return new(first, remaining, null, null);
        if (first == "回答" || (first is "批准" or "拒绝") && remaining.Length > 0)
        {
            var args = remaining; var code = Pop(ref args).ToUpperInvariant();
            var thread = _requests.TryGetValue(code, out var request) ? await ResolveAsync(request.View.ThreadId, ct) : null;
            if (thread is not null && explicitId is not null && (await ResolveAsync(explicitId, ct)).Id != thread.Id) throw Conflict();
            if (message.ReferenceIndex is not null)
            {
                var quoted = _references.Find(message.Sender, message.ReferenceIndex) ?? throw new AppException("CONTROL_REFERENCE", "引用记录未找到，请使用原问题通知或问题码。");
                if (thread is not null && (quoted.ThreadId != thread.Id || quoted.QuestionCode is not null && quoted.QuestionCode != code)) throw Conflict();
                thread ??= await ResolveAsync(quoted.ThreadId, ct);
            }
            return new(first, remaining, thread, code);
        }
        if (first is "状态" or "继续")
        {
            command = first; var id = Pop(ref remaining).TrimStart('#');
            if (id.Length == 0) throw new AppException("CONTROL_THREAD", "请提供会话编号。");
            if (explicitId is not null && !id.Equals(explicitId, StringComparison.OrdinalIgnoreCase)) throw Conflict();
            explicitId = id; text = remaining;
        }
        else if (first.StartsWith('/'))
        {
            command = first == "/draft" ? "/d" : first;
            if (command is not ("/d" or "/s" or "/c" or "/model" or "/reasoning" or "/full" or "/file")) throw new AppException("CONTROL_COMMAND", "发送“帮助”查看可用指令。");
            text = remaining;
            if (remaining.StartsWith('#'))
            {
                var id = Pop(ref remaining)[1..];
                if (explicitId is not null && !id.Equals(explicitId, StringComparison.OrdinalIgnoreCase)) throw Conflict();
                explicitId = id; text = remaining;
            }
        }
        QqReference? reference = null;
        if (message.ReferenceIndex is not null)
            reference = _references.Find(message.Sender, message.ReferenceIndex) ?? throw new AppException("CONTROL_REFERENCE", "这条引用未找到会话记录或已过期，请带 #会话编号重发。");
        if (command is "/full" or "/file" && string.IsNullOrWhiteSpace(reference?.TurnId))
            throw new AppException("CONTROL_RESULT_REFERENCE", $"请引用一条本轮完成通知，再发送 {command}。");
        var target = explicitId is null ? null : await ResolveAsync(explicitId, ct);
        if (reference is not null)
        {
            if (target is not null && target.Id != reference.ThreadId) throw Conflict();
            target ??= await ResolveAsync(reference.ThreadId, ct);
        }
        if (target is null)
        {
            var drafts = _drafts.Values.Where(d => d.Owner == message.Sender).Take(2).ToArray();
            if (drafts.Length == 1) return new(command, text, drafts[0].Thread, drafts[0].QuestionCode);
            throw new AppException("CONTROL_THREAD", drafts.Length > 1 ? "有多个图文草稿，请引用对应消息或带 #会话编号。" : "请引用一条 Codex 通知，或在消息开头添加 #会话编号。发送“任务”查看编号。");
        }
        var questionCode = reference?.QuestionCode;
        if (reference is null && _drafts.TryGetValue(target.Id, out var activeDraft)) questionCode = activeDraft.QuestionCode;
        return new(command, text, target, questionCode, reference);
    }
    private static AppException Conflict() => new("CONTROL_REFERENCE_CONFLICT", "引用和会话编号指向不同会话，请选择同一个目标。");

    private async Task<string?> ExecuteAsync(RoutedMessage route, QqPrivateMessage message, CancellationToken ct)
    {
        if (route.Command is "/full" or "/file")
        {
            if (!_exports.Writer.TryWrite((route, message))) throw new AppException("CONTROL_EXPORT_BUSY", "正在发送其他全文或附件，请稍后再试。");
            return null;
        }
        if (route.Command is "帮助" or "任务" or "待回答" or "状态" or "/model" or "/reasoning" or "批准" or "拒绝" or "回答")
        {
            if (message.Images.Count > 0) throw new AppException("CONTROL_IMAGE_COMMAND", "请单独发送这条指令。上传图片可引用会话发送 /d。");
            switch (route.Command)
            {
                case "帮助": return Help;
                case "任务":
                    var tasks = await TasksAsync(ct);
                    return tasks.Count == 0 ? "未发现普通会话。请在电脑新建 Codex 会话，或创建实时会话。" : string.Join("\n", tasks.Select(t => $"#{t.Id[^12..]} · {TaskName.Short(t.Name, sanitizer)}\n{t.State}"));
                case "待回答": return Pending.Count == 0 ? "当前没有待回答问题。" : string.Join("\n\n", Pending.Select(p => $"#{p.ThreadId[^12..]}\n{Describe(p)}"));
                case "状态":
                    var thread = route.Thread!; var info = await TaskStateAsync(thread.Id, ct);
                    if (thread.Managed) return $"{info.State}\n{_lastResults.GetValueOrDefault(thread.Id, "尚无本次连接的完成结果。")}";
                    var queue = await Rpc().CallAsync("thread/queue/list", new { threadId = thread.Id, limit = 100 }, ct);
                    return $"项目：{info.Directory}\n队列中 {queue.GetProperty("data").GetArrayLength()} 条（最多统计 100 条）。\n执行进度请查看 Desktop。";
                case "/model": return await ModelCommandAsync(route.Thread!.Id, string.IsNullOrWhiteSpace(route.Content) ? null : route.Content, ct);
                case "/reasoning": return await ReasoningCommandAsync(route.Thread!.Id, string.IsNullOrWhiteSpace(route.Content) ? null : route.Content, ct);
                default:
                    var args = route.Content; var code = Pop(ref args);
                    await AnswerAsync(code, route.Command == "批准" ? "accept" : route.Command == "拒绝" ? "decline" : args, false, ct);
                    return "已由 Codex 确认处理。";
            }
        }
        var target = route.Thread!;
        _drafts.TryGetValue(target.Id, out var draft);
        if (draft is not null && draft.Owner != message.Sender) throw new AppException("CONTROL_OWNER", "草稿不属于当前控制者。");
        if (draft is not null && (route.QuestionCode is not null || message.ReferenceIndex is not null) && route.QuestionCode != draft.QuestionCode)
            throw new AppException("CONTROL_DRAFT", "已有草稿属于另一条输入，请先引用原草稿 /s 提交或 /c 取消。");
        switch (route.Command)
        {
            case "/c":
                if (message.Images.Count > 0 || route.Content.Length > 0) throw new AppException("CONTROL_DRAFT", "单独发送 /c 即可取消草稿。");
                return _drafts.TryRemove(target.Id, out _) ? "已取消图文草稿。" : "该会话没有草稿。";
            case "/s":
                if (draft is null) throw new AppException("CONTROL_DRAFT", "该会话没有草稿。引用通知后发送 /d 开始。");
                await AppendAsync(draft, route.Content, message.Images, ct);
                if (draft.Input.Count == 0) throw new AppException("CONTROL_DRAFT", "草稿为空，请先发送文字或图片。");
                string result;
                if (draft.QuestionCode is not null)
                {
                    if (draft.Images > 0) throw new AppException("CONTROL_ANSWER_IMAGE", "此问题需要文字答案。请先 /c 取消图片草稿，再回复答案。");
                    await AnswerReferencedAsync(draft.QuestionCode, string.Join("\n", draft.Text), ct); result = "回答已由 Codex 确认处理。";
                }
                else result = await ContinueInputAsync(target.Id, draft.Input.ToArray(), ct);
                _drafts.TryRemove(target.Id, out _); return result;
            case "/d":
                draft ??= StartDraft(message.Sender, target, route.QuestionCode);
                await AppendAsync(draft, route.Content, message.Images, ct); return DraftStatus(draft);
            default:
                if (draft is not null)
                {
                    await AppendAsync(draft, route.Content, message.Images, ct); return DraftStatus(draft);
                }
                if (route.QuestionCode is not null)
                {
                    if (message.Images.Count > 0) throw new AppException("CONTROL_ANSWER_IMAGE", "此问题需要文字答案，请直接回复文字。");
                    await AnswerReferencedAsync(route.QuestionCode, route.Content, ct); return "回答已由 Codex 确认处理。";
                }
                if (message.Images.Count > 0)
                {
                    draft = StartDraft(message.Sender, target, null);
                    await AppendAsync(draft, route.Content, message.Images, ct); return DraftStatus(draft);
                }
                return await ContinueAsync(target.Id, route.Content, ct);
        }
    }
    private Task AnswerReferencedAsync(string code, string text, CancellationToken ct)
    {
        if (_requests.TryGetValue(code, out var request) && request.View.Questions.Length == 0)
            text = text.Trim() switch { "批准" => "accept", "拒绝" => "decline", _ => text };
        return AnswerAsync(code, text, false, ct);
    }
    private Draft StartDraft(string owner, ControlledThread thread, string? questionCode)
    {
        if (_drafts.Count >= 32) throw new AppException("CONTROL_DRAFT_LIMIT", "图文草稿过多，请先 /s 提交或 /c 取消已有草稿。");
        var draft = new Draft(owner, thread, questionCode); _drafts[thread.Id] = draft; return draft;
    }
    private async Task AppendAsync(Draft draft, string text, IReadOnlyList<QqImageAttachment> images, CancellationToken ct)
    {
        if (draft.Images + images.Count > QqImageCache.MaximumImageCount || draft.Text.Sum(x => x.Length) + text.Length > 6000)
            throw new AppException("CONTROL_DRAFT_SIZE", "每份草稿最多 8 张图片、6000 字符。原草稿已保留，请减少本条内容后重发。");
        if (draft.QuestionCode is not null && images.Count > 0) throw new AppException("CONTROL_ANSWER_IMAGE", "此问题需要文字答案，请直接回复文字。");
        var paths = await _images.DownloadAsync(images, ct);
        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(text)) { draft.Text.Add(text); draft.Input.Add(new { type = "text", text, text_elements = Array.Empty<object>() }); }
        foreach (var path in paths) draft.Input.Add(new { type = "localImage", path });
        draft.Images += paths.Count; draft.Expires = DateTimeOffset.UtcNow.AddMinutes(30);
    }
    private static string DraftStatus(Draft draft) => $"草稿已收 {draft.Text.Sum(t => t.Length)} 字、{draft.Images} 张图片。\n继续发送图文；/s 发送，/c 取消。草稿闲置 30 分钟或断开控制连接后清除。";
}

using System.Text.Json;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Codex;

public sealed partial class RemoteCodex
{
    private sealed record ModelChoice(string Id, string Model, string DefaultEffort, string[] Efforts, string[] Modalities, bool IsDefault);

    public async Task<ControlledThread> ResolveAsync(string shortId, CancellationToken ct = default)
    {
        shortId = shortId.Trim().TrimStart('#');
        var suffix = shortId.Length == 12 && shortId.All(Uri.IsHexDigit);
        if (!suffix && !Guid.TryParseExact(shortId, "D", out _))
            throw new AppException("CONTROL_THREAD", "使用消息中的 12 位会话编号或完整会话 ID。");
        var managed = config.Load().RemoteCodex.Threads.Where(t => t.Managed).ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var thread in await DiscoverAsync(ct))
            managed.TryAdd(thread.Id, new() { Id = thread.Id, Name = thread.Name, Managed = false });
        var candidates = managed.Values.Where(t => suffix ? t.Id.EndsWith(shortId, StringComparison.OrdinalIgnoreCase) : t.Id.Equals(shortId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1) throw new AppException("CONTROL_THREAD", "会话不存在、不是普通用户会话，或编号不唯一；请使用完整会话 ID。");
        return candidates[0];
    }

    public async Task<IReadOnlyList<CodexTaskInfo>> TasksAsync(CancellationToken ct = default)
    {
        var tasks = new List<CodexTaskInfo>();
        foreach (var thread in config.Load().RemoteCodex.Threads.Where(t => t.Managed))
        {
            try { tasks.Add((await TaskStateAsync(thread.Id, ct)) with { Name = thread.Name }); }
            catch (AppException e) { tasks.Add(new(thread.Id, thread.Name, "", "托管会话不可读取：" + e.Message)); }
        }
        var known = tasks.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        tasks.AddRange((await DiscoverAsync(ct)).Where(t => known.Add(t.Id)).Select(t => t with { State = "Desktop · 可追加消息，实时状态未知" }));
        return tasks;
    }

    private static async Task<CodexTaskInfo> InspectAsync(CodexRpc rpc, string id, CancellationToken ct)
    {
        var data = await rpc.CallAsync("thread/read", new { threadId = id, includeTurns = false }, ct);
        return TaskInfo(data.GetProperty("thread"));
    }

    private Task<CodexTaskInfo> TaskStateAsync(string id, CancellationToken ct) => _loaded.TryGetValue(id, out var task)
        ? Task.FromResult(task with { State = _running.ContainsKey(id) ? "托管 · 运行中 / 可能等待输入" : "托管 · 空闲" }) : InspectAsync(Rpc(), id, ct);

    private static CodexTaskInfo TaskInfo(JsonElement thread)
    {
        var id = CodexEvents.Text(thread, "id");
        if (!Guid.TryParse(id, out _)) throw new AppException("CODEX_SCHEMA", "Codex 会话缺少有效 ID。");
        var state = thread.TryGetProperty("status", out var status) ? CodexEvents.Text(status, "type") : null;
        return new(id!, CodexEvents.Text(thread, "name") ?? TaskName.Short(CodexEvents.Text(thread, "preview")), CodexEvents.Text(thread, "cwd") ?? "",
            state switch { "active" => "运行中 / 可能等待输入", "idle" => "空闲", "notLoaded" => "未在当前连接加载", "systemError" => "后台异常", _ => "状态未知" });
    }

    private static bool IsUserThread(JsonElement thread)
    {
        if (!Guid.TryParse(CodexEvents.Text(thread, "id"), out _) || CodexEvents.Text(thread, "parentThreadId") is not null
            || CodexEvents.Text(thread, "agentRole") is not null || CodexEvents.Text(thread, "originator") == "transmessage"
            || CodexEvents.Text(thread, "threadSource") is { } classification && classification != "user") return false;
        if (!thread.TryGetProperty("source", out var source)) return false;
        var user = source.ValueKind == JsonValueKind.String ? source.GetString() is "cli" or "vscode" or "exec" or "appServer"
            : CodexEvents.Text(source, "custom") is "desktop" or "codex-desktop" or "codex" or "app-server";
        return user && (CodexEvents.Text(thread, "preview") is not { Length: > 0 } preview || CodexEvents.IsRealInput(preview));
    }

    public async Task<IReadOnlyList<CodexTaskInfo>> DiscoverAsync(CancellationToken ct = default)
    {
        if (_rpc is { IsConnected: true } connected) return await DiscoverAsync(connected, ct);
        await using var rpc = await ConnectAsync(config.Load().RemoteCodex, ct);
        return await DiscoverAsync(rpc, ct);
    }

    private static async Task<IReadOnlyList<CodexTaskInfo>> DiscoverAsync(CodexRpc rpc, CancellationToken ct)
    {
        var threads = new Dictionary<string, CodexTaskInfo>(StringComparer.OrdinalIgnoreCase);
        var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        do
        {
            var result = await rpc.CallAsync("thread/list", new { limit = 100, archived = false, cursor }, ct);
            foreach (var thread in result.GetProperty("data").EnumerateArray().Where(IsUserThread))
            {
                var info = TaskInfo(thread); threads.TryAdd(info.Id, info);
            }
            cursor = CodexEvents.Text(result, "nextCursor");
            if (cursor is not null && !cursors.Add(cursor)) throw new AppException("CODEX_SCHEMA", "Codex 会话分页重复，已停止读取。");
        } while (cursor is not null);
        return threads.Values.ToArray();
    }

    public async Task<string> ProbeDesktopAsync(CancellationToken ct = default)
    {
        try
        {
            await using var rpc = await CodexRpc.ConnectAsync(config.Load().RemoteCodex.Executable, true, ct);
            var loaded = await rpc.CallAsync("thread/loaded/list", new { }, ct);
            return $"官方实时控制 socket 可连接，当前加载 {loaded.GetProperty("data").GetArrayLength()} 个会话。本版对现有 Desktop 只追加共享消息，不接管原窗口的实时问答。";
        }
        catch (Exception e) when (e is AppException or IOException or TimeoutException)
        { return "当前 Desktop 没有可连接的官方实时控制通道。可追加共享消息，但不能从 QQ 回答该窗口的待输入问题。"; }
    }

    public async Task<CodexTaskInfo> CreateManagedAsync(string directory, string name, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new AppException("CONTROL_DIRECTORY", "请选择存在的项目目录。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new AppException("CONTROL_NAME", "会话名称需要 1–100 个字符。");
        if (config.Load().RemoteCodex.Threads.Count(t => t.Managed) >= 50) throw new AppException("CONTROL_THREADS", "最多保留 50 个托管会话。");
        var result = await Rpc().CallAsync("thread/start", new { cwd = directory, sandbox = "workspace-write", approvalPolicy = "on-request", approvalsReviewer = "user", config = new Dictionary<string, object> { ["features.default_mode_request_user_input"] = true } }, ct);
        var info = TaskInfo(result.GetProperty("thread"));
        config.Update(c => c.RemoteCodex.Threads.Add(new() { Id = info.Id, Name = name.Trim(), Managed = true }));
        _loaded[info.Id] = info;
        return info with { Name = name.Trim() };
    }

    public Task<string> ContinueAsync(string id, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 6000) throw new AppException("CONTROL_INPUT", "要求需要 1–6000 个字符。");
        return ContinueInputAsync(id, [new { type = "text", text, text_elements = Array.Empty<object>() }], ct);
    }

    public async Task<string> ContinueInputAsync(string id, IReadOnlyList<object> input, CancellationToken ct = default)
    {
        if (input.Count == 0) throw new AppException("CONTROL_INPUT", "请提供文字或图片。");
        var items = JsonSerializer.SerializeToElement(input); var textLength = 0; var imageCount = 0;
        foreach (var item in items.EnumerateArray())
        {
            switch (CodexEvents.Text(item, "type"))
            {
                case "text" when CodexEvents.Text(item, "text") is { Length: > 0 } text: textLength += text.Length; break;
                case "localImage" when CodexEvents.Text(item, "path") is { } path && Path.IsPathFullyQualified(path) && File.Exists(path): imageCount++; break;
                case "image" when Uri.TryCreate(CodexEvents.Text(item, "url"), UriKind.Absolute, out var url) && url.Scheme == "https": imageCount++; break;
                default: throw new AppException("CONTROL_INPUT", "文字或图片输入无效。");
            }
        }
        if (textLength > 6000) throw new AppException("CONTROL_INPUT", "文字内容不能超过 6000 个字符。");
        if (imageCount > Channels.QqImageCache.MaximumImageCount) throw new AppException("CONTROL_INPUT", "一次最多发送 8 张图片。");
        var thread = await ResolveAsync(id, ct); var rpc = Rpc();
        if (!thread.Managed)
        {
            await rpc.CallAsync("thread/queue/add", new { threadId = thread.Id, input, clientUserMessageId = Guid.NewGuid().ToString() }, ct);
            try { await (wakeDesktop?.Invoke(thread.Id, ct) ?? DesktopWake.RequestAsync(thread.Id, ct)); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Status = "Desktop 唤醒失败：" + AppException.Describe(e);
                return $"#{thread.Id[^12..]} 消息已加入队列。Desktop 唤醒失败，请在电脑打开此会话。";
            }
            return $"#{thread.Id[^12..]} 已加入共享消息队列并发送 Desktop 后台唤醒请求，继续在原会话处理。";
        }
        if (_running.ContainsKey(thread.Id)) throw Busy();
        if (imageCount > 0)
        {
            var model = await EffectiveModelAsync(thread, await ModelsAsync(ct), ct);
            if (!model.Modalities.Contains("image", StringComparer.Ordinal)) throw new AppException("CONTROL_MODEL_IMAGE", "当前模型不支持图片，请先用 /model 选择支持图片的模型。");
        }
        if (!_loaded.ContainsKey(thread.Id))
        {
            var data = await rpc.CallAsync("thread/read", new { threadId = thread.Id, includeTurns = false }, ct);
            if (CodexEvents.Text(data.GetProperty("thread").GetProperty("status"), "type") == "active") throw Busy();
            var resumed = await rpc.CallAsync("thread/resume", new { threadId = thread.Id, excludeTurns = true, sandbox = "workspace-write", approvalPolicy = "on-request", approvalsReviewer = "user", config = new Dictionary<string, object> { ["features.default_mode_request_user_input"] = true } }, ct);
            _loaded[thread.Id] = TaskInfo(resumed.GetProperty("thread"));
        }
        if (!_running.TryAdd(thread.Id, 0)) throw Busy();
        _lastResults.TryRemove(thread.Id, out _);
        var parameters = new Dictionary<string, object> { ["threadId"] = thread.Id, ["input"] = input, ["clientUserMessageId"] = Guid.NewGuid().ToString() };
        if (thread.Model.Length > 0) parameters["model"] = thread.Model;
        if (thread.Effort.Length > 0) parameters["effort"] = thread.Effort;
        try { await rpc.CallAsync("turn/start", parameters, ct); }
        catch (AppException e) when (e.Code == "CODEX_RPC") { _running.TryRemove(thread.Id, out _); throw; }
        return $"#{thread.Id[^12..]} 已开始执行。需要回答或本轮结束时会私聊通知。";
    }

    public async Task<string> ModelCommandAsync(string id, string? model, CancellationToken ct = default)
    {
        var thread = await ResolveManagedAsync(id, ct); var models = await ModelsAsync(ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            var current = thread.Model.Length > 0 ? thread.Model : "沿用 Codex 会话设置";
            return $"#{thread.Id[^12..]} 下一轮模型：{current}\n可选：\n" + string.Join("\n", models.Select(m => $"{m.Model} · {(m.Modalities.Contains("image", StringComparer.Ordinal) ? "图文" : "文字")}"));
        }
        var selected = models.FirstOrDefault(m => m.Model == model || m.Id == model) ?? throw new AppException("CONTROL_MODEL", "模型不在当前 Codex 返回的列表中，请发送 /model 查询。");
        var effort = selected.Efforts.Contains(thread.Effort, StringComparer.Ordinal) ? thread.Effort : selected.DefaultEffort;
        SaveModel(thread.Id, selected.Model, effort);
        return $"#{thread.Id[^12..]} 下一轮使用 {selected.Model}，推理强度：{(effort.Length == 0 ? "默认" : effort)}。正在执行的轮次不变。";
    }

    public async Task<string> ReasoningCommandAsync(string id, string? effort, CancellationToken ct = default)
    {
        var thread = await ResolveManagedAsync(id, ct); var model = await EffectiveModelAsync(thread, await ModelsAsync(ct), ct);
        if (string.IsNullOrWhiteSpace(effort)) return $"#{thread.Id[^12..]} 下一轮模型：{model.Model}\n推理强度：{(thread.Effort.Length > 0 ? thread.Effort : model.DefaultEffort)}\n可选：{string.Join("、", model.Efforts)}";
        if (!model.Efforts.Contains(effort, StringComparer.Ordinal)) throw new AppException("CONTROL_REASONING", "该模型不支持此推理强度。可选：" + string.Join("、", model.Efforts));
        SaveModel(thread.Id, model.Model, effort);
        return $"#{thread.Id[^12..]} 下一轮推理强度设为 {effort}。正在执行的轮次不变。";
    }

    private async Task<ControlledThread> ResolveManagedAsync(string id, CancellationToken ct)
    {
        var thread = await ResolveAsync(id, ct);
        if (!thread.Managed) throw new AppException("CONTROL_DESKTOP_SETTINGS", "现有 Desktop 会话的实时设置连接不可用，请在 Codex 窗口调整模型；本程序不会把设置指令作为文字排队。");
        return thread;
    }

    private void SaveModel(string id, string model, string effort) => config.Update(c =>
    {
        var thread = c.RemoteCodex.Threads.Single(t => t.Managed && t.Id == id); thread.Model = model; thread.Effort = effort;
    });

    private async Task<ModelChoice> EffectiveModelAsync(ControlledThread thread, IReadOnlyList<ModelChoice> models, CancellationToken ct)
    {
        var selected = thread.Model;
        if (selected.Length == 0)
        {
            var read = await Rpc().CallAsync("thread/read", new { threadId = thread.Id, includeTurns = false }, ct);
            selected = CodexEvents.Text(read.GetProperty("thread"), "model") ?? models.FirstOrDefault(m => m.IsDefault)?.Model ?? "";
        }
        return models.FirstOrDefault(m => m.Model == selected || m.Id == selected) ?? throw new AppException("CONTROL_MODEL", "无法确认会话模型，请用 /model 明确选择当前可用模型。");
    }

    private async Task<IReadOnlyList<ModelChoice>> ModelsAsync(CancellationToken ct)
    {
        var models = new List<ModelChoice>(); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        do
        {
            var result = await Rpc().CallAsync("model/list", new { limit = 100, includeHidden = false, cursor }, ct);
            foreach (var model in result.GetProperty("data").EnumerateArray())
            {
                var name = CodexEvents.Text(model, "model");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var efforts = model.GetProperty("supportedReasoningEfforts").EnumerateArray().Select(e => CodexEvents.Text(e, "reasoningEffort")).OfType<string>().ToArray();
                var modalities = model.TryGetProperty("inputModalities", out var kinds) && kinds.ValueKind == JsonValueKind.Array
                    ? kinds.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String).Select(k => k.GetString()!).ToArray() : [];
                models.Add(new(CodexEvents.Text(model, "id") ?? name, name, CodexEvents.Text(model, "defaultReasoningEffort") ?? "", efforts, modalities,
                    model.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True));
            }
            cursor = CodexEvents.Text(result, "nextCursor");
            if (cursor is not null && !cursors.Add(cursor)) throw new AppException("CODEX_SCHEMA", "Codex 模型分页重复，已停止读取。");
        } while (cursor is not null);
        return models;
    }

    private static AppException Busy() => new("CONTROL_BUSY", "会话正在运行或等待回答。请先回答问题，或等本轮结束后继续。");
}

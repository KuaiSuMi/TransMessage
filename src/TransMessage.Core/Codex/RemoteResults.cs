using System.Globalization;
using TransMessage.Core.Channels;
using TransMessage.Core.Notifications;

namespace TransMessage.Core.Codex;

public sealed partial class RemoteCodex
{
    private async Task ExportAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _exports.Reader.ReadAllAsync(ct))
            {
                if (!Authorized(job.Message.Sender)) continue;
                var remaining = job.Message.Time.AddMinutes(5) - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) { Status = "全文或附件请求已过期，请在 QQ 重新发送指令。"; continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(remaining);
                try { await SendResultAsync(job.Route, job.Message, deadline.Token); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    if (deadline.IsCancellationRequested) { Status = "全文或附件请求已过期，请在 QQ 重新发送指令。"; continue; }
                    await NotifyAsync(job.Message.Sender, $"Codex · #{job.Route.Thread!.Id[^12..]}", AppException.Describe(e), job.Message.Id, ct,
                        job.Message.Time, job.Route.Thread.Id);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<(string Text, DateTimeOffset? Timestamp)> ReadReplyAsync(QqReference reference, CancellationToken ct)
    {
        var messages = new List<string>(); DateTimeOffset? timestamp = reference.Timestamp;
        string? cursor = null; var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var result = await Rpc().CallAsync("thread/items/list", new { threadId = reference.ThreadId, turnId = reference.TurnId, limit = 50, cursor, sortDirection = "asc" }, ct);
            foreach (var entry in result.GetProperty("data").EnumerateArray())
            {
                if (CodexEvents.Text(entry, "turnId") != reference.TurnId) throw new AppException("CODEX_REPLY", "Codex 返回了另一轮内容，请重新引用完成通知。");
                var item = entry.GetProperty("item");
                if (CodexEvents.Text(item, "type") != "agentMessage" || CodexEvents.Text(item, "phase") is "commentary" or "partial_answer") continue;
                if (CodexEvents.Text(item, "text") is { Length: > 0 } text) messages.Add(text);
                if (reference.Timestamp is null && entry.TryGetProperty("completedAtMs", out var stamp) && stamp.ValueKind == System.Text.Json.JsonValueKind.Number && stamp.TryGetInt64(out var millis)
                    && millis >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() && millis <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                    timestamp = DateTimeOffset.FromUnixTimeMilliseconds(millis);
            }
            cursor = CodexEvents.Text(result, "nextCursor");
            if (cursor is not null && !cursors.Add(cursor)) throw new AppException("CODEX_SCHEMA", "Codex 回复分页重复，请稍后再试。");
        } while (cursor is not null);
        if (messages.Count == 0) throw new AppException("CODEX_REPLY", "这一轮没有可读取的最终回复，请引用已完成的回复。");
        return (string.Join("\n\n", messages), timestamp);
    }

    private async Task SendResultAsync(RoutedMessage route, QqPrivateMessage message, CancellationToken ct)
    {
        if (message.Images.Count > 0) throw new AppException("CONTROL_RESULT", $"引用完成通知后发送 {route.Command} 即可。");
        var page = 1;
        if (route.Content.Length > 0 && (!int.TryParse(route.Content, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
            throw new AppException("CONTROL_RESULT_PAGE", $"页码从 1 开始，例如 {route.Command} 2。");
        var reference = route.Reference!;
        var reply = await ReadReplyAsync(reference, ct);
        Notification Notice(string title, string text) => new()
        {
            Title = $"Codex · #{reference.ThreadId[^12..]} · {title}",
            Message = text,
            Source = "qq-control",
            Timestamp = reply.Timestamp ?? message.Time,
            Metadata = new() { ["thread_id"] = reference.ThreadId, ["turn_id"] = reference.TurnId!, ["timestamp_source"] = reply.Timestamp.HasValue ? "original" : "received" }
        };
        async Task SendAsync(Notification notice, string? path = null)
        {
            ct.ThrowIfCancellationRequested();
            if (!Authorized(message.Sender)) throw new AppException("CONTROL_OWNER", "请先连接已绑定的本人 QQ。");
            var result = path is null ? await send(message.Sender, notice, message.Id, ct)
                : await sendFile!(message.Sender, path, notice, message.Id, ct);
            if (!result.Ok) throw new AppException("CONTROL_RESULT_SEND", result.Error ?? "发送失败，请引用原回复重新获取。");
        }
        if (route.Command == "/full")
        {
            var parts = ReplyContent.Split(sanitizer.Redact(reply.Text, trim: false)); var pages = (parts.Count + 3) / 4;
            if (page > pages) throw new AppException("CONTROL_RESULT_PAGE", $"全文共 {pages} 页，请发送 /full 1 至 /full {pages}。");
            var start = (page - 1) * 4; var end = Math.Min(start + 4, parts.Count);
            for (var i = start; i < end; i++)
            {
                var next = i == end - 1 && page < pages ? $"\n\n下一页：引用此消息发送 /full {page + 1}" : "";
                var notice = Notice($"全文 {i + 1}/{parts.Count} · 第 {page}/{pages} 页", parts[i] + next);
                notice.Metadata["reply_part"] = (i + 1).ToString(CultureInfo.InvariantCulture);
                await SendAsync(notice);
            }
        }
        else
        {
            if (sendFile is null) throw new AppException("CONTROL_FILE", "请重新连接 QQ 文件发送服务。");
            var info = await InspectAsync(Rpc(), reference.ThreadId, ct);
            var files = ReplyContent.Files(reply.Text, info.Directory);
            if (files.Count == 0) throw new AppException("CONTROL_FILE", "这条回复没有可发送的本地附件。请让 Codex 生成文件并在回复中附上文件链接。");
            var pages = (files.Count + 2) / 3;
            if (page > pages) throw new AppException("CONTROL_RESULT_PAGE", $"附件共 {pages} 页，请发送 /file 1 至 /file {pages}。");
            var selected = files.Skip((page - 1) * 3).Take(3).ToArray();
            var list = string.Join("\n", selected.Select(Path.GetFileName));
            if (page < pages) list += $"\n\n下一页：引用此消息发送 /file {page + 1}";
            await SendAsync(Notice($"附件 · 第 {page}/{pages} 页", list));
            foreach (var file in selected) await SendAsync(Notice("附件", Path.GetFileName(file)), file);
        }
    }
}

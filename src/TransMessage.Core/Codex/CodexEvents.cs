using System.Text.Json;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.Core.Codex;

public sealed record SessionIdentity(string ThreadId, string Cwd, string Client, bool IsUser);

public static class CodexEvents
{
    private static readonly string[] InternalPrompts =
    [
        "Generate a concise, single-line task title", "You are a task title generator", "Generate a short title",
        "You are generating a title", "You are creating a CONTEXT CHECKPOINT", "Perform a memory consolidation",
        "You are a helpful assistant that generates task titles"
    ];

    public static bool IsRealInput(string? text) => !string.IsNullOrWhiteSpace(text)
        && !InternalPrompts.Any(p => text.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase))
        && !text.TrimStart().StartsWith("<subagent", StringComparison.OrdinalIgnoreCase);

    public static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    public static DateTimeOffset? Timestamp(JsonElement value) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("timestamp", out var timestamp)
        && timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var time) ? time : null;

    public static SessionIdentity? ReadIdentity(JsonElement meta)
    {
        var id = Text(meta, "id");
        if (!Guid.TryParse(id, out _)) return null;
        var client = Text(meta, "originator") ?? "unknown";
        if (client == "transmessage") return new(id!, Text(meta, "cwd") ?? "", client, false);
        var user = true;
        if (Text(meta, "parent_thread_id") is not null || Text(meta, "agent_role") is not null) user = false;
        if (Text(meta, "agent_path") is { } path && path != "/root") user = false;
        if (Text(meta, "thread_source") is { } threadSource && threadSource != "user") user = false;
        if (!meta.TryGetProperty("source", out var source)) user = false;
        else if (source.ValueKind == JsonValueKind.String)
            user &= source.GetString() is "cli" or "exec" or "vscode" or "mcp";
        else if (source.ValueKind == JsonValueKind.Object && Text(source, "custom") is { } custom)
            user &= custom is "desktop" or "codex-desktop" or "codex" or "app-server";
        else user = false;
        return new(id!, Text(meta, "cwd") ?? "", client, user);
    }

    public static Notification? ParseNotify(string json, AppConfig config, SessionIdentity? identity, Sanitizer? sanitizer = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (Text(root, "type") != "agent-turn-complete" || !config.Codex.Enabled) return null;
            var thread = Text(root, "thread-id"); var turn = Text(root, "turn-id");
            if (identity is not { IsUser: true } || identity.ThreadId != thread || string.IsNullOrWhiteSpace(turn)) return null;
            var client = Text(root, "client") ?? identity.Client;
            if (client.Contains("subagent", StringComparison.OrdinalIgnoreCase) || client.Contains("internal", StringComparison.OrdinalIgnoreCase)) return null;
            if (!root.TryGetProperty("input-messages", out var inputs) || inputs.ValueKind != JsonValueKind.Array) return null;
            var input = inputs.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()).LastOrDefault();
            if (!IsRealInput(input)) return null;
            return Create(config, identity with { Client = client }, turn, input!, Text(root, "last-assistant-message"), "completed", null, sanitizer, Timestamp(root));
        }
        catch (JsonException) { return null; }
    }

    public static Notification Create(AppConfig config, SessionIdentity identity, string turn, string input, string? result, string state, long? duration, Sanitizer? sanitizer = null, DateTimeOffset? timestamp = null)
    {
        sanitizer ??= new Sanitizer(); input = sanitizer.Redact(input); result = sanitizer.Redact(result);
        var project = Path.GetFileName(identity.Cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var message = $"项目：{project}\n任务：{Sanitizer.Truncate(input, 180)}\n状态：{state switch { "failed" => "失败", "cancelled" => "已取消", _ => "本轮结束" }}\n客户端：{identity.Client}";
        if (duration is >= 0) message += $"\n耗时：{TimeSpan.FromMilliseconds(duration.Value):g}";
        if (config.Codex.IncludeResult && !string.IsNullOrWhiteSpace(result)) message += "\n\n结果：\n" + Sanitizer.Truncate(result, config.Codex.MaxResultChars);
        return new Notification
        {
            Id = $"codex:{identity.ThreadId}:{turn}",
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Source = "codex",
            Title = MessageTemplate.Expand(config.Notifications.CodexCompleted, ("project", project)),
            Message = message,
            Level = state == "failed" ? "error" : state == "cancelled" ? "warning" : "success",
            Metadata = new() { ["client"] = identity.Client, ["status"] = state, ["thread_id"] = identity.ThreadId, ["turn_id"] = turn, ["timestamp_source"] = timestamp.HasValue ? "original" : "received" }
        };
    }

    public static SessionIdentity? FindIdentity(string home, string thread)
    {
        if (!Guid.TryParse(thread, out _)) return null;
        var sessions = Path.Combine(home, "sessions");
        if (!Directory.Exists(sessions)) return null;
        foreach (var path in Directory.EnumerateFiles(sessions, $"*{thread}.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                var line = ReadBoundedLine(reader, 1024 * 1024);
                if (line is null) continue;
                using var doc = JsonDocument.Parse(line);
                if (Text(doc.RootElement, "type") == "session_meta" && doc.RootElement.TryGetProperty("payload", out var meta)) return ReadIdentity(meta);
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        return null;
    }

    public static string? ReadBoundedLine(StreamReader reader, int maximum)
    {
        var buffer = new System.Text.StringBuilder();
        int value;
        while ((value = reader.Read()) >= 0)
        {
            if (value == '\n') return buffer.ToString().TrimEnd('\r');
            if (buffer.Length >= maximum) return null;
            buffer.Append((char)value);
        }
        return buffer.Length > 0 ? buffer.ToString() : null;
    }
}

public sealed class CodexTurnReader
{
    public SessionIdentity? Identity { get; private set; }
    private string? _turn;
    private string? _input;
    private bool _valid;
    public bool RecognizedCompletion { get; private set; }

    public Notification? ReadLine(string line, AppConfig config, Sanitizer? sanitizer = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var payload)) return null;
            var type = CodexEvents.Text(root, "type");
            if (type == "session_meta") { Identity = CodexEvents.ReadIdentity(payload); return null; }
            if (Identity is not { IsUser: true } || type != "event_msg") return null;
            var eventType = CodexEvents.Text(payload, "type");
            switch (eventType)
            {
                case "task_started":
                case "turn_started":
                    _turn = CodexEvents.Text(payload, "turn_id"); _input = null; _valid = true;
                    if (CodexEvents.Text(payload, "root_turn_id") is { } rootTurn && rootTurn != _turn) _valid = false;
                    break;
                case "user_message":
                    if (_turn is not null)
                    {
                        _input = CodexEvents.Text(payload, "message");
                        _valid &= CodexEvents.IsRealInput(_input);
                    }
                    break;
                case "item_completed":
                    if (_turn is not null && CodexEvents.Text(payload, "turn_id") == _turn && CodexEvents.Text(payload, "thread_id") == Identity.ThreadId
                        && payload.TryGetProperty("item", out var item) && CodexEvents.Text(item, "type") == "UserMessage"
                        && item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        _input = string.Join("\n", content.EnumerateArray().Where(c => CodexEvents.Text(c, "type") == "text").Select(c => CodexEvents.Text(c, "text")));
                        _valid &= CodexEvents.IsRealInput(_input);
                    }
                    break;
                case "task_complete":
                case "turn_complete":
                case "turn_aborted":
                    RecognizedCompletion = true;
                    var turn = CodexEvents.Text(payload, "turn_id");
                    if (turn is null || turn != _turn || !_valid || !CodexEvents.IsRealInput(_input)) return null;
                    _turn = null;
                    var state = eventType == "turn_aborted" ? "cancelled" : payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object ? "failed" : "completed";
                    var duration = payload.TryGetProperty("duration_ms", out var millis) && millis.TryGetInt64(out var number) ? (long?)number : null;
                    return config.Codex.Enabled ? CodexEvents.Create(config, Identity, turn, _input!, CodexEvents.Text(payload, "last_agent_message"), state, duration, sanitizer, CodexEvents.Timestamp(root)) : null;
            }
        }
        catch (JsonException) { _valid = false; }
        return null;
    }
}

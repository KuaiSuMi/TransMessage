using System.Text.Json;
using TransMessage.Core.Codex;

namespace TransMessage.Core.Codex
{
    public sealed record QqPrivateMessage(string Id, string Sender, string Text, DateTimeOffset Time)
    {
        public string? ReferenceIndex { get; init; }
        public IReadOnlyList<Channels.QqImageAttachment> Images { get; init; } = [];
        public string? InputError { get; init; }
    }
}

namespace TransMessage.Core.Channels
{
    public sealed record QqImageAttachment(string Url, string ContentType, long? Size = null);

    public static class QqMessages
    {
        public static QqPrivateMessage? Parse(string type, JsonElement data)
        {
            if (type != "C2C_MESSAGE_CREATE" || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object
                || author.TryGetProperty("bot", out var bot) && bot.ValueKind == JsonValueKind.True) return null;
            var id = CodexEvents.Text(data, "id"); var sender = CodexEvents.Text(author, "user_openid");
            var text = CodexEvents.Text(data, "content") ?? "";
            if (id is not { Length: > 0 and <= 256 } || sender is not { Length: > 0 and <= 256 } || text.Length > 6000
                || !DateTimeOffset.TryParse(CodexEvents.Text(data, "timestamp"), out var time)) return null;
            List<QqImageAttachment> images = []; string? inputError = null;
            if (data.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in attachments.EnumerateArray())
                {
                    var contentType = CodexEvents.Text(item, "content_type");
                    if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    { inputError = "这条消息包含不支持的附件，整条消息未提交。请只发送文字或 PNG、JPEG、GIF 图片。"; continue; }
                    var url = CodexEvents.Text(item, "url") ?? "";
                    if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
                    var size = item.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var count) ? (long?)count : null;
                    images.Add(new(url, contentType, size));
                    if (images.Count > QqImageCache.MaximumImageCount) break;
                }
            }
            else if (attachments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                inputError = "这条消息的附件无法读取，整条消息未提交。请重新发送图片。";
            string? reference = null;
            if (data.TryGetProperty("message_scene", out var scene) && scene.ValueKind == JsonValueKind.Object
                && scene.TryGetProperty("ext", out var ext) && ext.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in ext.EnumerateArray())
                {
                    var value = field.ValueKind == JsonValueKind.String ? field.GetString() : null;
                    if (value?.StartsWith("ref_msg_idx=", StringComparison.Ordinal) == true)
                    {
                        var index = value["ref_msg_idx=".Length..];
                        if (index.Length is > 0 and <= 2048) reference = index;
                    }
                }
            }
            if (data.TryGetProperty("message_type", out var messageType) && messageType.ValueKind == JsonValueKind.Number
                && messageType.TryGetInt32(out var kind) && kind == 103 && reference is null)
                inputError = "未取得这条引用消息的会话关联，消息未提交。请重新引用机器人消息，或发送 #会话编号 你的要求。";
            if (string.IsNullOrWhiteSpace(text) && images.Count == 0 && inputError is null) return null;
            // Nested msg_elements belong to the quoted message, never to this new input.
            return new(id, sender, text.Trim(), time) { ReferenceIndex = reference, Images = images, InputError = inputError };
        }
    }
}

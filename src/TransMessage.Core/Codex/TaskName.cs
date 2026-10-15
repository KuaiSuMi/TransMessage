using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TransMessage.Core.Security;

namespace TransMessage.Core.Codex;

public static class TaskName
{
    public static string Short(string? input, Sanitizer? sanitizer = null)
    {
        try
        {
            var text = input?.Trim() ?? "";
            var request = Regex.Match(text, @"(?m)^#{1,6}\s*(?:My request|我的请求|我的需求)\s*[:：]\s*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (request.Success) text = text[(request.Index + request.Length)..].Trim();
            else if (text.StartsWith("# Files mentioned by the user:", StringComparison.Ordinal) || text.StartsWith("# Files pasted by the user:", StringComparison.Ordinal)) return "处理附件";
            const string replyTag = "<send_user_message_question_reply>";
            if (text.StartsWith(replyTag, StringComparison.Ordinal))
            {
                var end = text.IndexOf("</send_user_message_question_reply>", StringComparison.Ordinal);
                var body = end >= replyTag.Length ? text[replyTag.Length..end] : text[replyTag.Length..];
                text = "补充任务信息";
                try
                {
                    using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
                    if (json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.GetArrayLength() > 0
                        && CodexEvents.Text(json.RootElement[0], "question") is { Length: > 0 } question) text = "确认：" + question;
                }
                catch (JsonException) { }
            }
            else if (text.StartsWith('<')) return "Codex 任务";
            text = (sanitizer ?? new Sanitizer()).Redact(text);
            var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Codex 任务";
            line = Regex.Replace(line, @"^(?:#{1,6}\s*|[-*+]\s+|\d+[.、)]\s*)", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            line = Regex.Replace(line, @"\[([^\]\r\n]+)\]\([^\r\n)]*\)", "$1", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            line = line.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
            line = (sanitizer ?? new Sanitizer()).Redact(line);
            line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            line = line.Split(['。', '！', '？', '!', '?'], 2)[0].Trim();
            if (line.Length == 0) return "Codex 任务";
            var characters = StringInfo.ParseCombiningCharacters(line);
            return characters.Length <= 32 ? line : line[..characters[31]].TrimEnd() + "…";
        }
        catch (RegexMatchTimeoutException) { return "Codex 任务"; }
    }
}

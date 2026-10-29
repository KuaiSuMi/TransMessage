using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TransMessage.Core.Codex;

public static class ReplyContent
{
    private static readonly MarkdownPipeline SplitPipeline = new MarkdownPipelineBuilder().UseAutoLinks().UsePreciseSourceLocation().Build();

    public static string ExpandReferences(string text)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        var definitions = new HashSet<LinkReferenceDefinition>();
        foreach (var link in Markdown.Parse(text, SplitPipeline).Descendants<LinkInline>())
        {
            if (link.Reference is not { } reference) continue;
            // Keep the original label, including nested Markdown; LabelSpan identifies the reference name.
            var labelStart = Math.Max(link.Span.Start + (link.IsImage ? 2 : 1), (link.LastChild?.Span.End ?? -1) + 1);
            var labelEnd = text.IndexOf(']', labelStart, link.Span.End - labelStart + 1);
            var destination = text.Substring(reference.UrlSpan.Start, reference.UrlSpan.Length);
            if (reference.Title is { Length: > 0 }) destination += " " + text.Substring(reference.TitleSpan.Start, reference.TitleSpan.Length);
            edits.Add((labelEnd + 1, link.Span.End - labelEnd, "(" + destination + ")"));
            definitions.Add(reference);
        }
        if (edits.Count == 0) return text;
        foreach (var definition in definitions) edits.Add((definition.Span.Start, definition.Span.Length, ""));
        var result = new StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Start)) result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        return result.ToString();
    }

    public static IReadOnlyList<string> Split(string text, int byteLimit = 2600)
    {
        if (byteLimit < 4) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var tokens = Markdown.Parse(text, SplitPipeline).Descendants<Inline>()
            .Where(node => node is LinkInline or AutolinkInline or CodeInline).Select(node => node.Span)
            .Where(span => span.Start >= 0 && span.End >= span.Start && span.End < text.Length)
            .Select(span => (span.Start, span.End, Bytes: Encoding.UTF8.GetByteCount(text.AsSpan(span.Start, span.Length))))
            .Where(token => token.Bytes <= byteLimit).OrderBy(token => token.Start).ThenByDescending(token => token.End).ToArray();
        var parts = new List<string>(); var current = new StringBuilder(); var bytes = 0;
        var position = 0; var tokenIndex = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            while (tokenIndex < tokens.Length && tokens[tokenIndex].End < position) tokenIndex++;
            // Move a complete short token to the next part; oversized tokens retain rune-safe splitting.
            var moveToken = tokenIndex < tokens.Length && tokens[tokenIndex].Start == position && bytes + tokens[tokenIndex].Bytes > byteLimit;
            if (moveToken || bytes + rune.Utf8SequenceLength > byteLimit) { parts.Add(current.ToString()); current.Clear(); bytes = 0; }
            current.Append(rune); bytes += rune.Utf8SequenceLength;
            position += rune.Utf16SequenceLength;
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    public static IReadOnlyList<string> Files(string markdown, string directory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in Markdown.Parse(markdown).Descendants<LinkInline>())
        {
            var raw = link.Url;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                raw = Regex.Replace(raw, @"(?::\d+(?::\d+)?|#L?\d+(?:-L?\d+)?)$", "", RegexOptions.CultureInvariant);
                if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) raw = new Uri(raw).LocalPath;
                else if (Regex.IsMatch(raw, @"^[a-z][a-z\d+.-]+://|^sandbox:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                else raw = Uri.UnescapeDataString(raw);
                if (raw.Length > 3 && raw[0] == '/' && char.IsAsciiLetter(raw[1]) && raw[2] == ':') raw = raw[1..];
                var path = Path.GetFullPath(raw, directory);
                // Attachments must be ordinary local files; never dereference network, device or alternate-stream links.
                if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path.AsSpan(2).Contains(':')) continue;
                var root = Path.GetPathRoot(path)!;
                if (new DriveInfo(root).DriveType == DriveType.Network) continue;
                var current = root; var attributes = FileAttributes.Directory; var reparse = false;
                foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar))
                {
                    current = Path.Combine(current, segment); attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { reparse = true; break; }
                }
                if (!reparse && (attributes & FileAttributes.Directory) == 0) files.Add(path);
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException) { }
        }
        return files.ToArray();
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace TransMessage.Core.Codex;

public static class ReplyContent
{
    public static IReadOnlyList<string> Split(string text, int byteLimit = 2600)
    {
        if (byteLimit < 4) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var parts = new List<string>(); var current = new StringBuilder(); var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > byteLimit) { parts.Add(current.ToString()); current.Clear(); bytes = 0; }
            current.Append(rune); bytes += rune.Utf8SequenceLength;
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

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TransMessage.Core.Codex;

namespace TransMessage.Core.Channels;

internal static class QqMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAutoLinks().UsePreciseSourceLocation().Build();
    private static readonly Regex SessionNumber = new(@"#[0-9a-fA-F]{12}(?![0-9a-zA-Z_-])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    private static readonly Regex ActionTag = new(@"<\s*/?\s*qqbot-[\s\S]*?(?:>|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    private static readonly Regex Newlines = new(@"(?:\r?\n)+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(150));
    private readonly record struct Edit(int Start, int End, string Text);

    internal static (string Content, bool IsMarkdown) Format(string source, bool linkFiles)
    {
        source = ReplyContent.ExpandReferences(source);
        // Neutralize source-supplied actions even inside code: only this formatter creates executable QQ tags.
        var actions = ActionTag.Matches(source).Select(m => new SourceSpan(m.Index, m.Index + m.Length - 1)).ToArray();
        var safe = source.ToCharArray();
        foreach (var span in actions)
            for (var i = span.Start; i <= span.End; i++)
                if (safe[i] is '<' or '>') safe[i] = safe[i] == '<' ? '＜' : '＞';
        source = new string(safe);
        var document = Markdown.Parse(source, Pipeline);
        var inlines = document.Descendants<Inline>().ToArray();
        var protectedSpans = document.Descendants<Block>().Where(b => b is CodeBlock or HeadingBlock).Select(b => b.Span)
            .Concat(document.Descendants<LinkReferenceDefinition>().Select(b => b.Span))
            .Concat(inlines.Where(i => i is LinkInline or AutolinkInline or CodeInline or HtmlInline).Select(i => i.Span)).ToArray();
        var edits = new List<Edit>(); var markdown = false;
        bool InAction(SourceSpan span) => actions.Any(a => a.Start <= span.End && span.Start <= a.End);

        foreach (var inline in inlines)
        {
            var span = inline.Span;
            if (span.Start < 0 || span.End < span.Start || span.End >= source.Length || InAction(span) && inline is not LiteralInline) continue;
            switch (inline)
            {
                case LinkInline link:
                    if (WebUrl(link.Url))
                    {
                        markdown = true;
                        // Removing only '!' preserves titles, escaped URL characters and labels.
                        if (link.IsImage) edits.Add(new(span.Start, span.End, source[(span.Start + 1)..(span.End + 1)]));
                        else if (link.IsAutoLink) edits.Add(new(span.Start, span.End, "<" + link.Url + ">"));
                    }
                    else
                    {
                        var label = Label(link);
                        var local = LocalFile(link.Url);
                        edits.Add(new(span.Start, span.End, local && linkFiles ? Input("/file", label, true) : label));
                        markdown |= local && linkFiles;
                    }
                    break;
                case AutolinkInline auto:
                    if (WebUrl(auto.Url)) markdown = true;
                    else
                    {
                        var local = !auto.IsEmail && LocalFile(auto.Url);
                        var label = Label(auto.IsEmail ? auto.Url : "", auto.Url);
                        edits.Add(new(span.Start, span.End, local && linkFiles ? Input("/file", label, true) : label));
                        markdown |= local && linkFiles;
                    }
                    break;
                case CodeInline code when !InsideLink(code) && SessionNumber.Match(code.Content) is { Success: true, Index: 0 } codeMatch && codeMatch.Length == code.Content.Length:
                    edits.Add(new(span.Start, span.End, Input(code.Content + " ", code.Content, false))); markdown = true;
                    break;
                case LiteralInline literal when !InsideLink(literal):
                    foreach (Match match in SessionNumber.Matches(source[span.Start..(span.End + 1)]))
                    {
                        var start = span.Start + match.Index;
                        if (InAction(new(start, start + match.Length - 1))) continue;
                        // A Markdown escape belongs to the literal '#' and must not precede the generated tag.
                        var slashes = 0;
                        for (var i = start - 1; i >= 0 && source[i] == '\\'; i--) slashes++;
                        if (slashes % 2 != 0) start--;
                        edits.Add(new(start, span.Start + match.Index + match.Length - 1, Input(match.Value + " ", match.Value, false)));
                        markdown = true;
                    }
                    break;
            }
        }
        if (markdown)
            foreach (Match line in Newlines.Matches(source))
                if (line.Value.Count(c => c == '\n') == 1 && !protectedSpans.Any(s => s.Start <= line.Index && line.Index <= s.End))
                    edits.Add(new(line.Index, line.Index + line.Length - 1, "\n\n"));

        var result = new StringBuilder(); var position = 0;
        foreach (var edit in edits.OrderBy(e => e.Start).ThenByDescending(e => e.End))
        {
            if (edit.Start < position) continue; // A replaced local link owns its entire label, including nested formatting.
            result.Append(source, position, edit.Start - position).Append(edit.Text); position = edit.End + 1;
        }
        result.Append(source, position, source.Length - position);
        return (result.ToString(), markdown);
    }

    private static bool InsideLink(Inline inline)
    {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
            if (parent is LinkInline) return true;
        return false;
    }

    private static bool WebUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool LocalFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#') || url.StartsWith('?')) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.IsFile && !uri.IsUnc;
        return !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith(@"\\", StringComparison.Ordinal)
            && !url.Contains(':');
    }

    private static string Label(LinkInline link)
    {
        var label = string.Concat(link.Descendants<Inline>().Select(i => i switch
        {
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            LineBreakInline => " ",
            _ => ""
        }));
        return Label(label, link.Url);
    }

    private static string Label(string label, string? url)
    {
        if (string.IsNullOrWhiteSpace(label) || label == url || Regex.IsMatch(label, @"^(?:/?[a-z]:[\\/]|file:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var path = url ?? "";
            if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            else { try { path = Uri.UnescapeDataString(path); } catch (UriFormatException) { } }
            label = path.Replace('\\', '/').Split('/').LastOrDefault() ?? "附件";
        }
        var result = new StringBuilder();
        foreach (var rune in label.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (result.Length + rune.Utf16SequenceLength > 100) break;
            result.Append(rune);
        }
        return result.ToString().Trim().Replace('<', '＜').Replace('>', '＞') is { Length: > 0 } value ? value : "附件";
    }

    private static string Input(string text, string show, bool reference) => $"<qqbot-cmd-input text=\"{Uri.EscapeDataString(text)}\" show=\"{Uri.EscapeDataString(show)}\" reference=\"{(reference ? "true" : "false")}\" />";
}

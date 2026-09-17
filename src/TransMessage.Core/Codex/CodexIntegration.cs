using System.Diagnostics;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using TransMessage.Core.Configuration;

namespace TransMessage.Core.Codex;

public sealed record IntegrationState(string ConfigPath, string? OriginalValue, string[]? PreviousCommand, string[] InstalledCommand);

public sealed class CodexIntegration(AppPaths paths, string? home = null)
{
    public string ConfigPath => Path.Combine(home ?? AppPaths.CodexHome, "config.toml");
    private string StatePath => Path.Combine(paths.Root, "codex-integration.json");
    public bool IsInstalled()
    {
        if (!File.Exists(ConfigPath)) return false;
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(ConfigPath));
            return model?.TryGetValue("notify", out var value) == true && value is TomlArray array
                && array.OfType<string>().Any(s => s == "notify")
                && array.OfType<string>().Any(s => Path.GetFileName(s).Equals("TransMessage.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
    public void Install(string executable) => AtomicFile.Locked(ConfigPath, () =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        var document = Parse(text);
        var node = NotifyNode(document);
        var command = new[] { Path.GetFullPath(executable), "--data-dir", paths.Root, "codex", "notify" };
        var model = TomlSerializer.Deserialize<TomlTable>(text)!;
        var previous = model.TryGetValue("notify", out var current) && current is TomlArray array ? array.OfType<string>().ToArray() : null;
        if (previous?.SequenceEqual(command) == true) return true;
        var oldState = LoadState();
        var owned = oldState is not null && previous?.SequenceEqual(oldState.InstalledCommand) == true;
        var originalValue = node?.Value is { } originalNode ? text.Substring(originalNode.Span.Start.Offset, originalNode.Span.End.Offset - originalNode.Span.Start.Offset + 1) : null;
        var state = new IntegrationState(ConfigPath, owned ? oldState!.OriginalValue : originalValue,
            owned ? oldState!.PreviousCommand : previous, command);
        var replacement = "[" + string.Join(", ", command.Select(s => JsonSerializer.Serialize(s))) + "]";
        var updated = ReplaceValue(text, node, replacement);
        _ = Parse(updated);
        Backup();
        AtomicFile.Write(StatePath, JsonSerializer.Serialize(state));
        AtomicFile.Write(ConfigPath, updated);
        return true;
    });
    public bool Uninstall() => AtomicFile.Locked(ConfigPath, () =>
    {
        var state = LoadState();
        if (state is null || !File.Exists(ConfigPath)) return false;
        var text = File.ReadAllText(ConfigPath);
        var doc = Parse(text);
        var model = TomlSerializer.Deserialize<TomlTable>(text)!;
        if (!model.TryGetValue("notify", out var value) || value is not TomlArray array
            || !array.OfType<string>().SequenceEqual(state.InstalledCommand))
            throw new AppException("CODEX_MODIFIED", "notify 已被其他程序修改；为保留你的改动，没有覆盖配置。");
        var node = NotifyNode(doc)!;
        var updated = state.OriginalValue is not null ? ReplaceValue(text, node, state.OriginalValue)
            : text.Remove(node.Span.Start.Offset, node.Span.End.Offset - node.Span.Start.Offset + 1);
        _ = Parse(updated); Backup(); AtomicFile.Write(ConfigPath, updated); File.Delete(StatePath); return true;
    });
    public async Task ForwardPreviousAsync(string payload)
    {
        var previous = LoadState()?.PreviousCommand;
        if (previous is not { Length: > 0 } || Path.GetFileName(previous[0]).Equals("TransMessage.exe", StringComparison.OrdinalIgnoreCase)) return;
        var start = new ProcessStartInfo(previous[0]) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in previous.Skip(1)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(payload);
        using var process = Process.Start(start);
        if (process is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { /* The user's independent hook owns its process lifetime. */ }
    }
    private IntegrationState? LoadState() => File.Exists(StatePath) ? JsonSerializer.Deserialize<IntegrationState>(File.ReadAllText(StatePath)) : null;
    private static DocumentSyntax Parse(string text)
    {
        var doc = SyntaxParser.Parse(text);
        if (doc.HasErrors) throw new AppException("CODEX_PARSE", "Codex config parse failed. Original config was not changed.");
        return doc;
    }
    private static KeyValueSyntax? NotifyNode(DocumentSyntax doc) => doc.KeyValues.FirstOrDefault(k =>
        k.Key?.DotKeys.ChildrenCount == 0 && k.Key.Key?.ToString().Trim().Trim('"', '\'') == "notify");
    private static string ReplaceValue(string text, KeyValueSyntax? node, string replacement)
    {
        if (node?.Value is null) return "notify = " + replacement + Environment.NewLine + text;
        var span = node.Value.Span;
        return text[..span.Start.Offset] + replacement + text[(span.End.Offset + 1)..];
    }
    private void Backup()
    {
        if (!File.Exists(ConfigPath)) return;
        File.Copy(ConfigPath, ConfigPath + ".transmessage-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff"));
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(ConfigPath)!, "config.toml.transmessage-backup-*").OrderDescending().Skip(5)) File.Delete(file);
    }
}

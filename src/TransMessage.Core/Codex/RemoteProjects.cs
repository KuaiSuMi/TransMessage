using System.Text.Json;
using TransMessage.Core.Configuration;
using TransMessage.Core.Security;

namespace TransMessage.Core.Codex;

public sealed partial class RemoteCodex
{
    private sealed record ProjectChoice(string Id, string Name, string[] Roots);
    private IReadOnlyList<ProjectChoice>? _projectChoices;

    public async Task<string> ProjectListAsync(int page = 1, CancellationToken ct = default)
    {
        if (page < 1) throw new AppException("CONTROL_PROJECT_PAGE", "页码从 1 开始，请发送 /p 或 /p 2。");
        if (page == 1) _projectChoices = (await ReadProjectsAsync(ct)).Where(p => p.Roots.Any(AvailableDirectory)).ToArray();
        var choices = _projectChoices ?? throw new AppException("CONTROL_PROJECT_LIST", "请先发送 /p 查看项目列表。");
        if (choices.Count == 0) return "没有可用的本地项目。请先在 Codex 中添加项目，再发送 /p。";
        var pages = (choices.Count + 7) / 8;
        if (page > pages) throw new AppException("CONTROL_PROJECT_PAGE", $"项目列表共 {pages} 页，请发送 /p 1 至 /p {pages}。");
        var lines = choices.Skip((page - 1) * 8).Take(8).Select((project, index) =>
            $"{(page - 1) * 8 + index + 1}. {DisplayProjectText(project.Name, 40)}\n{DisplayProjectText(project.Roots[0], 80)}");
        return $"项目 · 第 {page}/{pages} 页\n\n" + string.Join("\n\n", lines)
            + "\n\n新建：/new 数字 [首条要求]"
            + (page < pages ? $"\n下一页：/p {page + 1}" : "");
    }

    public async Task<(ControlledThread Thread, string Message)> CreateProjectThreadAsync(int number, string prompt = "", CancellationToken ct = default)
    {
        if (prompt.Length > 6000) throw new AppException("CONTROL_INPUT", "首条要求最多 6000 个字符。");
        var choices = _projectChoices ?? throw new AppException("CONTROL_PROJECT_LIST", "请先发送 /p 查看项目列表，再用 /new 数字新建会话。");
        if (number < 1 || number > choices.Count) throw new AppException("CONTROL_PROJECT_NUMBER", "项目编号不在当前列表中，请先发送 /p 查看。");
        var selected = choices[number - 1];
        var project = (await ReadProjectsAsync(ct)).SingleOrDefault(p => p.Id == selected.Id);
        if (project is null || !project.Roots.SequenceEqual(selected.Roots, StringComparer.OrdinalIgnoreCase))
            throw new AppException("CONTROL_PROJECT_CHANGED", "项目或目录已变更，请发送 /p 刷新列表。");
        if (project.Roots.Length == 0 || project.Roots.Any(root => !AvailableDirectory(root)))
            throw new AppException("CONTROL_PROJECT_DIRECTORY", "项目有目录不可用，请在电脑修正后发送 /p 刷新。");
        prompt = prompt.Trim();
        var name = prompt.Length > 0 ? Sanitizer.Truncate(TaskName.Short(prompt, sanitizer), 100)
            : Sanitizer.Truncate(TaskName.Short(project.Name, sanitizer), 94) + " · 新对话";
        var created = await CreateManagedAsync(project.Roots[0], name, ct, project.Id, project.Roots);
        var thread = new ControlledThread { Id = created.Id, Name = created.Name, Managed = true };
        var message = $"#{created.Id[^12..]} · {created.Name}\n项目：{DisplayProjectText(project.Name, 80)}\n{created.State}";
        if (prompt.Length == 0) return (thread, message + "\n\n引用此消息回复首条要求，或发送 #会话编号 你的要求。");
        try { return (thread, message + "\n\n" + await ContinueAsync(created.Id, prompt, ct)); }
        catch (Exception e)
        {
            return (thread, message + "\n\n首条要求未确认启动：" + AppException.Describe(e)
                + "\n会话已保留，请先查看此会话状态，再引用本消息继续；无需再次 /new。");
        }
    }

    private async Task<IReadOnlyList<ProjectChoice>> ReadProjectsAsync(CancellationToken ct)
    {
        var projects = new Dictionary<string, ProjectChoice>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
        do
        {
            var result = await Rpc().CallAsync("project/list", new { limit = 100, sortKey = "position", sortDirection = "asc", cursor }, ct);
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new AppException("CODEX_SCHEMA", "Codex 项目列表格式异常，请检查更新。");
            foreach (var item in data.EnumerateArray())
            {
                var id = CodexEvents.Text(item, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var roots = item.TryGetProperty("roots", out var paths) && paths.ValueKind == JsonValueKind.Array
                    ? paths.EnumerateArray().Select(path => CodexEvents.Text(path, "path") ?? "").ToArray() : [];
                if (roots.Length == 0) continue;
                var name = CodexEvents.Text(item, "name");
                projects.TryAdd(id, new(id, string.IsNullOrWhiteSpace(name) ? "未命名项目" : name, roots));
            }
            cursor = CodexEvents.Text(result, "nextCursor");
            if (cursor is not null && !cursors.Add(cursor)) throw new AppException("CODEX_SCHEMA", "Codex 项目分页重复，已停止读取。");
        } while (cursor is not null);
        return projects.Values.ToArray();
    }

    private static bool AvailableDirectory(string path) => Path.IsPathFullyQualified(path) && Directory.Exists(path);
    private string DisplayProjectText(string text, int limit) => Sanitizer.Truncate(
        string.Join(" ", sanitizer.Redact(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), limit);
}

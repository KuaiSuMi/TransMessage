using System.Diagnostics;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Sources;

namespace TransMessage.Core.Hosting;

public sealed record CheckResult(string Name, string State, string Detail);
public static class Doctor
{
    public static async Task<List<CheckResult>> RunAsync(AppRuntime app, bool online = false, CancellationToken ct = default)
    {
        List<CheckResult> results = [];
        var c = app.Config.Load();
        results.Add(new("配置", "OK", app.Paths.Config));
        try { _ = app.Secrets.ApiToken(); results.Add(new("凭证存储", "OK", "Windows DPAPI · 当前用户")); }
        catch (Exception e) { results.Add(new("凭证存储", "FAIL", AppException.Describe(e))); }
        if (string.IsNullOrEmpty(c.Qq.AppId)) results.Add(new("QQ 认证", "WARN", "尚未配置；打开设置填写 AppID/AppSecret。"));
        else if (online)
        {
            try { await app.Qq.GetTokenAsync(ct); results.Add(new("QQ 认证", "OK", "认证成功；发送权限仍须点击测试验证。")); }
            catch (Exception e) { results.Add(new("QQ 认证", "FAIL", AppException.Describe(e))); }
        }
        else results.Add(new("QQ 认证", "WARN", "未进行网络验证；运行 doctor --online。"));
        foreach (var kind in new[] { "c2c", "group" })
            results.Add(new(kind == "c2c" ? "私聊目标" : "群聊目标", c.Qq.Targets.Any(t => t.Kind == kind && t.Enabled) ? "OK" : "WARN", "在 QQ 设置中绑定目标；主动发送受腾讯权限和配额限制。"));
        var integration = new CodexIntegration(app.Paths);
        results.Add(new("Codex 配置", integration.IsInstalled() ? "OK" : "WARN", integration.IsInstalled() ? "notify 已配置；重启已有 Codex 会话后生效。" : "点击快捷集成中的“一键连接 Codex”。"));
        var sessions = Path.Combine(AppPaths.CodexHome, "sessions");
        var compatible = false;
        if (Directory.Exists(sessions))
        {
            foreach (var path in Directory.EnumerateFiles(sessions, "*.jsonl", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(f);
                var line = CodexEvents.ReadBoundedLine(reader, 1024 * 1024);
                var parser = new CodexTurnReader();
                if (line is not null) parser.ReadLine(line, c);
                compatible |= parser.Identity is { IsUser: true };
            }
        }
        results.Add(new("Codex Desktop / VS Code", compatible ? "OK" : "WARN", compatible ? "已识别本地用户会话元数据；完成事件须通过一次真实任务验证。" : "当前 Codex 版本暂无可靠完成事件，未知格式安全跳过。"));
        var background = await app.Pipe.TrySendAsync(new("status"), ct);
        results.Add(new("后台 / HTTP / 下载", background?.Ok == true ? "OK" : "WARN", background?.Message ?? "后台未运行；双击程序或运行 serve。"));
        if (c.Downloads.AutoDownloads)
        {
            var downloads = StableFileWatcher.DownloadsPath();
            results.Add(new("下载目录", Directory.Exists(downloads) ? "OK" : "WARN", downloads));
        }
        if (online)
        {
            var update = await app.Updater.CheckAsync(BuildInfo.Version, true, ct);
            results.Add(new("更新源", update.Error is null ? "OK" : "WARN", update.Error ?? "GitHub Release 可用"));
        }
        results.Add(new("Windows Service", "INFO", "V1 使用当前用户托盘后台和登录启动，不安装系统服务。"));
        return results;
    }
}

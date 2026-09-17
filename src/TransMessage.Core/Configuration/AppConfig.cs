namespace TransMessage.Core.Configuration;

public sealed class AppConfig
{
    public QqSettings Qq { get; set; } = new();
    public CodexSettings Codex { get; set; } = new();
    public DownloadSettings Downloads { get; set; } = new();
    public ServerSettings Server { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public UpdateSettings Update { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    public void Validate()
    {
        if (Server.Port is < 1024 or > 65535) throw new AppException("CONFIG_PORT", "端口必须介于 1024–65535。");
        if (Downloads.StableSeconds is < 2 or > 3600) throw new AppException("CONFIG_STABILITY", "文件稳定时间必须介于 2–3600 秒。");
        if (Codex.MaxResultChars is < 50 or > 3000) throw new AppException("CONFIG_SUMMARY", "摘要长度必须介于 50–3000 字符。");
        if (Notifications.TailLines is < 0 or > 100) throw new AppException("CONFIG_TAIL", "末尾输出行数必须介于 0–100。");
        if (Qq.Targets.Any(t => t.Kind is not ("c2c" or "group") || string.IsNullOrWhiteSpace(t.Id)))
            throw new AppException("CONFIG_TARGET", "通知目标无效，请重新绑定。");
        if (Downloads.Directories.Any(p => !Path.IsPathFullyQualified(p))) throw new AppException("CONFIG_PATH", "监控目录必须是绝对路径。");
        if (Ui.Theme is not ("system" or "light" or "dark")) throw new AppException("CONFIG_THEME", "界面主题无效。");
        if (Ui.TextScale is < 1 or > 2) throw new AppException("CONFIG_SCALE", "文字缩放必须介于 100%–200%。");
    }
}

public sealed class QqSettings
{
    public string AppId { get; set; } = "";
    public bool Sandbox { get; set; }
    public List<QqTarget> Targets { get; set; } = [];
}
public sealed class QqTarget
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "c2c";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public override string ToString() => $"{(Kind == "c2c" ? "私聊" : "群聊")} · {Name}{(Enabled ? "" : "（停用）")}";
}
public sealed class CodexSettings
{
    public bool Enabled { get; set; } = true;
    public bool SessionWatcher { get; set; } = true;
    public bool IncludeResult { get; set; } = true;
    public int MaxResultChars { get; set; } = 900;
}
public sealed class DownloadSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoDownloads { get; set; } = true;
    public int StableSeconds { get; set; } = 10;
    public List<string> Directories { get; set; } = [];
}
public sealed class ServerSettings
{
    public bool Enabled { get; set; } = true;
    public bool AllowLan { get; set; }
    public int Port { get; set; } = 18765;
}
public sealed class NotificationSettings
{
    public bool Paused { get; set; }
    public int TailLines { get; set; } = 12;
    public string CodexCompleted { get; set; } = "🤖 Codex 完成 · {project}";
    public string CommandCompleted { get; set; } = "✅ 任务完成";
    public string CommandFailed { get; set; } = "❌ 任务失败";
    public string DownloadCompleted { get; set; } = "📥 下载完成 · {filename}";
    public string FileReady { get; set; } = "📄 文件就绪 · {filename}";
}
public sealed class UpdateSettings { public bool Check { get; set; } = true; }
public sealed class UiSettings
{
    public string Theme { get; set; } = "system";
    public double TextScale { get; set; } = 1;
    public bool ReduceTransparency { get; set; }
    public bool TrayHintShown { get; set; }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Hosting;
using TransMessage.Core.Notifications;
using TransMessage.Core.Sources;

namespace TransMessage.App;

public partial class MainWindow : Window
{
    private readonly AppRuntime _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<string, TextBlock> _status = [];
    public bool Exiting { get; set; }
    private string _page = "home";
    public MainWindow(AppRuntime app)
    {
        _app = app; InitializeComponent(); ApplyTheme();
        VersionLabel.Text = $"Windows · v{BuildInfo.Version}";
        var symbols = new[] { "\uE80F", "\uE77B", "\uE943", "\uE896", "\uE774", "\uE8D4", "\uE713", "\uE9D9" };
        var labels = new[] { "总览", "QQ 机器人", "Codex", "下载与文件", "本地 API", "快捷集成", "设置", "诊断与日志" };
        for (var i = 0; i < Navigation.Items.Count; i++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = symbols[i], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
            ((ListBoxItem)Navigation.Items[i]).Content = row;
            AutomationProperties.SetName((ListBoxItem)Navigation.Items[i], labels[i]);
        }
        Navigation.SelectedIndex = 0;
        _timer.Tick += (_, _) => { if (IsVisible) RefreshStatus(); }; _timer.Start();
        SystemParameters.StaticPropertyChanged += SystemSettingsChanged;
        Closed += (_, _) => { _timer.Stop(); SystemParameters.StaticPropertyChanged -= SystemSettingsChanged; };
    }
    private void SystemSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);
    public void Feedback(string message) => FeedbackText.Text = message;
    public async Task ExecuteAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { Feedback(AppException.Describe(e)); }
    }
    private void Navigate(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListBoxItem item || PageContent is null) return;
        _page = (string)item.Tag; PageContent.Children.Clear(); _status.Clear(); PageScroll.ScrollToTop();
        switch (_page)
        {
            case "home": Home(); break;
            case "qq": Qq(); break;
            case "codex": Codex(); break;
            case "downloads": Downloads(); break;
            case "api": Api(); break;
            case "integrations": Integrations(); break;
            case "settings": Settings(); break;
            case "doctor": Diagnostics(); break;
        }
        RefreshStatus();
    }
    private void ReloadPage() => Navigate(this, new SelectionChangedEventArgs(SelectorEvent(), Array.Empty<object>(), Array.Empty<object>()));
    private static RoutedEvent SelectorEvent() => System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
    private void Go(string tag) => Navigation.SelectedItem = Navigation.Items.Cast<ListBoxItem>().First(i => (string)i.Tag == tag);
    private void Heading(string title, string subtitle)
    {
        PageContent.Children.Add(new TextBlock { Text = title, FontSize = 30, FontWeight = FontWeights.Bold, Margin = new(0, 0, 0, 10) });
        var description = Text(subtitle); description.Margin = new(0, 0, 0, 28); PageContent.Children.Add(description);
    }
    private static TextBlock Text(string content, bool secondary = true)
    {
        var text = new TextBlock { Text = content, LineHeight = 23, Margin = new(0, 0, 0, 12) };
        text.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "Secondary" : "Label"); return text;
    }
    private StackPanel Card(string title, string? description = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
        if (description is not null) panel.Children.Add(Text(description));
        var border = new Border { CornerRadius = new(20), Padding = new(24), Margin = new(0, 0, 0, 18), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "Surface"); PageContent.Children.Add(border); return panel;
    }
    private static WrapPanel Actions(StackPanel parent) { var panel = new WrapPanel { Margin = new(0, 10, 0, -10) }; parent.Children.Add(panel); return panel; }
    private Button Button(Panel parent, string label, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetName(button, label);
        if (primary) button.SetResourceReference(StyleProperty, "PrimaryButton");
        button.Click += async (_, _) => { button.IsEnabled = false; await ExecuteAsync(action); button.IsEnabled = true; };
        parent.Children.Add(button); return button;
    }
    private Button Action(Panel parent, string label, Action action, bool primary = false) => Button(parent, label, () => { action(); return Task.CompletedTask; }, primary);
    private static TextBox Field(StackPanel parent, string label, string value, bool multiline = false)
    {
        var box = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        AutomationProperties.SetName(box, label);
        parent.Children.Add(new Label { Content = label, Target = box, Padding = new(0), Foreground = (Brush)Application.Current.FindResource("Label") });
        parent.Children.Add(box); return box;
    }
    private static CheckBox Check(StackPanel parent, string label, bool enabled)
    {
        var check = new CheckBox { Content = new TextBlock { Text = label }, IsChecked = enabled };
        AutomationProperties.SetName(check, label); parent.Children.Add(check); return check;
    }
    private void State(StackPanel parent, string key, string label)
    {
        var panel = new Grid { Margin = new(0, 7, 0, 7) };
        panel.ColumnDefinitions.Add(new() { Width = new(145) }); panel.ColumnDefinitions.Add(new());
        var labelText = Text(label, false); labelText.Margin = new(0); panel.Children.Add(labelText);
        var value = Text("正在读取"); value.Margin = new(0); Grid.SetColumn(value, 1); panel.Children.Add(value); _status[key] = value; parent.Children.Add(panel);
    }
    private async Task TestAsync()
    {
        var result = await _app.Dispatcher.SendAsync(new Notification { Title = "TransMessage 测试通知", Message = "连接已确认。下一次完成，也会在这里收到。", Level = "success" });
        Feedback(result.Ok ? "测试通知已发送，查看手机 QQ。" : result.Error!); RefreshStatus();
    }
    private void Home()
    {
        Heading("完成之后，随时知晓", "把注意力留给重要的事。Codex、命令和下载结束时，让 QQ 通知你。");
        var config = _app.Config.Load();
        var ready = config.Qq.Targets.Any(t => t.Enabled);
        var hero = Card(ready ? "通知已准备就绪" : "从连接 QQ 开始", ready ? "发一条测试通知，确认手机可以收到。" : "准备好自己的 QQ 机器人，连接后即可接收任务完成通知。");
        var actions = Actions(hero);
        if (ready) Button(actions, "发送测试通知", TestAsync, true);
        else Action(actions, "连接 QQ 机器人", () => Go("qq"), true);
        Action(actions, "查看接入方式", () => Go("integrations"));
        var status = Card("运行状态");
        State(status, "qq", "QQ 机器人"); State(status, "c2c", "手机私聊"); State(status, "group", "QQ 群聊");
        State(status, "codex", "Codex"); State(status, "background", "后台服务"); State(status, "api", "本地 API"); State(status, "downloads", "下载监控");
        var last = Card("最近通知"); State(last, "delivery", "发送结果");
        var update = Card("版本与更新", $"TransMessage {BuildInfo.Version} · Windows x64"); State(update, "update", "新版本");
        Button(Actions(update), "检查更新", CheckUpdateAsync);
    }
    private void Qq()
    {
        Heading("连接 QQ 机器人", "完成一次配置，之后由 TransMessage 自动发送通知。");
        var c = _app.Config.Load();
        var intro = Card("1. 创建你的机器人", "打开 QQ 开放平台并完成注册、机器人创建。在控制台取得 AppID 和 AppSecret。\n按当前控制台要求配置沙箱成员、测试群、群与私聊事件及 IP 白名单；主动消息权限和配额以腾讯实际授权为准。");
        var actions = Actions(intro); Action(actions, "打开 QQ 开放平台 ↗", () => WindowsIntegration.Open("https://q.qq.com"));
        Action(actions, "复制操作说明", () => { Clipboard.SetText("打开 https://q.qq.com → 创建机器人 → 获取 AppID/AppSecret → 按控制台启用群/私聊事件、沙箱成员及 IP 白名单 → 回到 TransMessage 验证配置 → 绑定目标 → 发送测试通知。主动发送需平台授权。"); Feedback("操作说明已复制。"); });
        var credentials = Card("2. 验证机器人");
        var id = Field(credentials, "AppID", c.Qq.AppId);
        credentials.Children.Add(Text("AppSecret（留空保留已保存凭证）", false));
        var secret = new PasswordBox(); AutomationProperties.SetName(secret, "AppSecret"); credentials.Children.Add(secret);
        credentials.Children.Add(Text(_app.Secrets.Get("qq") is null ? "还未保存凭证。" : "凭证已使用 Windows DPAPI 加密保存。"));
        var sandbox = Check(credentials, "使用腾讯沙箱 API（按控制台要求选择）", c.Qq.Sandbox);
        Button(Actions(credentials), "保存并验证", async () =>
        {
            if (string.IsNullOrWhiteSpace(id.Text)) throw new AppException("QQ_APPID", "填写 QQ AppID。");
            if (!string.IsNullOrWhiteSpace(secret.Password)) _app.Secrets.Set("qq", secret.Password);
            _app.Config.Update(cfg => { cfg.Qq.AppId = id.Text.Trim(); cfg.Qq.Sandbox = sandbox.IsChecked == true; }); secret.Clear(); _app.Qq.InvalidateToken();
            await _app.Qq.GetTokenAsync(); await _app.StartServicesAsync(CancellationToken.None);
            Feedback("认证成功。继续绑定目标。");
        }, true);
        var binding = Card("3. 绑定通知目标", "绑定码 5 分钟有效，每次只能使用一次。私聊直接向机器人发送；群聊需要先 @机器人。");
        var name = Field(binding, "给目标起个名字", "我的 QQ"); var code = Field(binding, "绑定指令", "点击下方按钮生成"); code.IsReadOnly = true;
        var binds = Actions(binding);
        foreach (var kind in new[] { "c2c", "group" })
            Action(binds, kind == "c2c" ? "绑定手机私聊" : "绑定 QQ 群", () =>
            {
                var challenge = _app.Binding.Create(kind, name.Text); code.Text = "bind " + challenge.Code;
                Feedback(kind == "c2c" ? "在 QQ 向机器人发送绑定指令，随后刷新目标。" : "在目标群 @机器人并发送绑定指令，随后刷新目标。");
            });
        Action(binds, "复制指令", () => { if (code.Text.StartsWith("bind ", StringComparison.Ordinal)) { Clipboard.SetText(code.Text); Feedback("绑定指令已复制。"); } });
        var targets = Card("通知目标", "勾选的目标都会收到通知。可以同时绑定多个私聊和群聊。");
        if (c.Qq.Targets.Count == 0) targets.Children.Add(Text("尚未绑定目标。完成 QQ 中的操作后，点击刷新。"));
        foreach (var target in c.Qq.Targets)
        {
            var check = Check(targets, target.ToString(), target.Enabled);
            check.Click += (_, _) => _app.Config.Update(cfg => { var item = cfg.Qq.Targets.First(t => t.Id == target.Id && t.Kind == target.Kind); item.Enabled = check.IsChecked == true; });
            Action(Actions(targets), "移除「" + target.Name + "」", () =>
            {
                if (MessageBox.Show(this, "移除后可重新绑定该目标。", "移除通知目标", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
                _app.Config.Update(cfg => cfg.Qq.Targets.RemoveAll(t => t.Id == target.Id && t.Kind == target.Kind)); ReloadPage();
            });
        }
        var targetActions = Actions(targets); Action(targetActions, "刷新目标", ReloadPage); Button(targetActions, "发送测试通知", TestAsync);
    }
    private void Codex()
    {
        Heading("Codex 完成通知", "每轮真实用户任务结束，发送一次通知。内部任务与重复事件会被过滤。");
        var c = _app.Config.Load(); var integration = new CodexIntegration(_app.Paths);
        var connect = Card("自动连接", integration.IsInstalled() ? "notify 已配置。已有 Codex 会话需要重启后加载新设置。" : "一键配置 Codex CLI / TUI；本地会话监听补充 Desktop / VS Code 完成事件。");
        connect.Children.Add(Text("修改前自动备份 config.toml。已有通知命令会被保留并转发；无法识别的会话格式不发送通知。"));
        var actions = Actions(connect); Action(actions, "一键连接 Codex", () => { integration.Install(WindowsIntegration.Executable); Feedback("Codex 已连接。重启正在运行的 Codex 会话。 "); ReloadPage(); }, true);
        Action(actions, "解除连接", () => { integration.Uninstall(); Feedback("已恢复原有 notify 设置。"); ReloadPage(); });
        var summary = Card("发送内容");
        var enabled = Check(summary, "启用 Codex 通知", c.Codex.Enabled); var watcher = Check(summary, "监听本机 Codex 用户会话", c.Codex.SessionWatcher);
        var include = Check(summary, "包含最终回答摘要（发送前本地脱敏）", c.Codex.IncludeResult);
        var maximum = Field(summary, "摘要最大字符数", c.Codex.MaxResultChars.ToString());
        Button(Actions(summary), "保存设置", async () =>
        {
            var limit = Number(maximum.Text, 50, 3000);
            _app.Config.Update(cfg => { cfg.Codex.Enabled = enabled.IsChecked == true; cfg.Codex.SessionWatcher = watcher.IsChecked == true; cfg.Codex.IncludeResult = include.IsChecked == true; cfg.Codex.MaxResultChars = limit; });
            await _app.StartServicesAsync(CancellationToken.None); Feedback("Codex 设置已保存。");
        });
        var status = Card("当前兼容状态"); State(status, "codex", "完成信号");
        status.Children.Add(Text("项目、任务摘要和最终结果来自实际事件。官方 notify 未提供可靠耗时时会省略；本地 task_complete 如包含 duration_ms 则显示。通知“本轮结束”不等于任务一定成功。"));
    }
    private void Downloads()
    {
        Heading("下载与文件", "文件出现并持续稳定后通知。自动忽略浏览器的临时下载文件。");
        var c = _app.Config.Load(); var general = Card("监控设置");
        var enabled = Check(general, "启用目录监控", c.Downloads.Enabled);
        var auto = Check(general, "自动监控 Windows 下载目录", c.Downloads.AutoDownloads);
        general.Children.Add(Text(StableFileWatcher.DownloadsPath()));
        var stable = Field(general, "文件稳定时间（秒）", c.Downloads.StableSeconds.ToString());
        general.Children.Add(Text("结合大小、修改时间、重命名和文件占用状态判断。长时间暂停写入的程序可能提前满足稳定条件，可适当延长等待时间。"));
        Button(Actions(general), "保存设置", async () =>
        {
            var seconds = Number(stable.Text, 2, 3600);
            _app.Config.Update(cfg => { cfg.Downloads.Enabled = enabled.IsChecked == true; cfg.Downloads.AutoDownloads = auto.IsChecked == true; cfg.Downloads.StableSeconds = seconds; });
            await _app.StartServicesAsync(CancellationToken.None); Feedback("文件监控设置已应用。");
        }, true);
        var directories = Card("其他监控目录", "监控所选目录的直接子文件；启动时已有的稳定文件不会补发通知。");
        foreach (var path in c.Downloads.Directories)
        {
            directories.Children.Add(Text(path, false));
            Button(Actions(directories), "移除此目录", async () => { _app.Config.Update(cfg => cfg.Downloads.Directories.Remove(path)); await _app.StartServicesAsync(CancellationToken.None); ReloadPage(); });
        }
        Button(Actions(directories), "添加目录…", async () =>
        {
            var dialog = new OpenFolderDialog { Title = "选择监控目录", Multiselect = false };
            if (dialog.ShowDialog(this) != true) return;
            _app.Config.Update(cfg => { if (!cfg.Downloads.Directories.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) cfg.Downloads.Directories.Add(dialog.FolderName); });
            await _app.StartServicesAsync(CancellationToken.None); ReloadPage();
        });
        var one = Card("等待一个文件", "高级用法：监控指定文件生成并稳定，通知后自动结束。");
        Code(one, "& '" + WindowsIntegration.Executable.Replace("'", "''") + "' watch-file 'D:\\output\\model.bin'");
    }
    private void Api()
    {
        Heading("本地 API", "让脚本、工具和局域网设备发送通知。接口仅接收通知内容。");
        var c = _app.Config.Load(); var card = Card("连接设置");
        var enabled = Check(card, "启用 HTTP API", c.Server.Enabled); var port = Field(card, "端口", c.Server.Port.ToString());
        var lan = Check(card, "允许局域网访问", c.Server.AllowLan);
        card.Children.Add(Text("默认仅本机访问；所有请求都需要 Bearer Token。开启局域网后监听所有 IPv4 网卡，Windows 防火墙可能需要你手动放行。HTTP 无传输加密，仅用于可信网络。"));
        Button(Actions(card), "保存并应用", async () =>
        {
            var number = Number(port.Text, 1024, 65535);
            _app.Config.Update(cfg => { cfg.Server.Enabled = enabled.IsChecked == true; cfg.Server.Port = number; cfg.Server.AllowLan = lan.IsChecked == true; });
            await _app.StartServicesAsync(CancellationToken.None); Feedback("API 设置已应用。连接状态见下方。 ");
        }, true);
        var token = Card("访问令牌", "令牌已加密保存在本机。复制后 30 秒自动清除未变化的剪贴板内容；重新生成后旧令牌立即失效。");
        var actions = Actions(token); Action(actions, "复制 Token", CopyToken); Action(actions, "重新生成 Token", () =>
        {
            if (MessageBox.Show(this, "旧令牌将立即失效，现有脚本需要更新。", "重新生成令牌", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _app.Secrets.RegenerateApiToken(); Feedback("新令牌已生成，点击“复制 Token”获取。");
        });
        var state = Card("连接状态"); State(state, "api", "HTTP API"); Code(state, $"POST http://127.0.0.1:{c.Server.Port}/v1/notify\nAuthorization: Bearer <复制的 Token>\nContent-Type: application/json\n\n{{\"title\":\"训练完成\",\"message\":\"loss=0.032\",\"level\":\"success\"}}");
    }
    private async void CopyToken()
    {
        var value = _app.Secrets.ApiToken(); Clipboard.SetText(value); Feedback("Token 已复制，30 秒后清除。");
        await Task.Delay(TimeSpan.FromSeconds(30));
        try { if (Clipboard.ContainsText() && Clipboard.GetText() == value) Clipboard.Clear(); } catch (System.Runtime.InteropServices.COMException) { }
    }
    private void Integrations()
    {
        Heading("快捷集成", "选一种熟悉的方式，复制代码即可接入。凭证不会写入示例。");
        var card = Card("选择接入方式");
        var choices = new ComboBox { ItemsSource = new[] { "PowerShell", "CMD", "Python", "JavaScript", "HTTP / curl", "Codex" }, SelectedIndex = 0 }; card.Children.Add(choices);
        var code = Field(card, "可复制代码", "", true); code.IsReadOnly = true; code.MinHeight = 190; code.FontFamily = new FontFamily("Cascadia Code, Consolas");
        void Change() => code.Text = Snippets.For(choices.SelectedItem?.ToString() ?? "PowerShell", WindowsIntegration.Executable, _app.Config.Load().Server.Port);
        choices.SelectionChanged += (_, _) => Change(); Change();
        var actions = Actions(card); Action(actions, "复制代码", () => { Clipboard.SetText(code.Text); Feedback("代码已复制。"); }, true); Action(actions, "一键连接 Codex", () => Go("codex"));
        var runner = Card("任何命令，结束即通知", "程序退出码保持不变，成功与失败都会通知。Ctrl+C 会传递到子进程。");
        Code(runner, "& '" + WindowsIntegration.Executable.Replace("'", "''") + "' run -- python train.py");
    }
    private void Settings()
    {
        Heading("设置", "调整后台行为、外观和通知文字。");
        var c = _app.Config.Load(); var general = Card("后台与外观");
        var startup = Check(general, "登录 Windows 后在后台运行", WindowsIntegration.StartupEnabled);
        var paused = Check(general, "暂停所有通知", c.Notifications.Paused); var update = Check(general, "自动检查新版本（每天最多一次）", c.Update.Check);
        general.Children.Add(Text("外观", false)); var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = c.Ui.Theme == "light" ? 1 : c.Ui.Theme == "dark" ? 2 : 0 }; general.Children.Add(theme);
        var opaque = Check(general, "降低透明度", c.Ui.ReduceTransparency);
        var scale = Field(general, "文字大小（100–200%）", ((int)(c.Ui.TextScale * 100)).ToString());
        var tail = Field(general, "命令失败时附带最后几行输出", c.Notifications.TailLines.ToString());
        Button(Actions(general), "保存设置", async () =>
        {
            var size = Number(scale.Text, 100, 200); var lines = Number(tail.Text, 0, 100);
            _app.Config.Update(cfg => { cfg.Notifications.Paused = paused.IsChecked == true; cfg.Update.Check = update.IsChecked == true; cfg.Ui.Theme = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "system"; cfg.Ui.ReduceTransparency = opaque.IsChecked == true; cfg.Ui.TextScale = size / 100d; cfg.Notifications.TailLines = lines; });
            WindowsIntegration.SetStartup(startup.IsChecked == true, _app.Paths); ApplyTheme(); await _app.StartServicesAsync(CancellationToken.None); Feedback("设置已保存。");
        }, true);
        var templates = Card("通知标题模板", "支持 {project}、{duration}、{command}、{filename}、{exit_code}、{device}；各来源只替换实际可用字段。");
        var codex = Field(templates, "Codex 完成", c.Notifications.CodexCompleted); var success = Field(templates, "命令成功", c.Notifications.CommandCompleted);
        var failure = Field(templates, "命令失败", c.Notifications.CommandFailed); var download = Field(templates, "下载完成", c.Notifications.DownloadCompleted); var file = Field(templates, "文件就绪", c.Notifications.FileReady);
        Action(Actions(templates), "保存模板", () =>
        {
            if (new[] { codex.Text, success.Text, failure.Text, download.Text, file.Text }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 140)) throw new AppException("TEMPLATE_LENGTH", "标题模板需要 1–140 个字符。");
            _app.Config.Update(cfg => { cfg.Notifications.CodexCompleted = codex.Text; cfg.Notifications.CommandCompleted = success.Text; cfg.Notifications.CommandFailed = failure.Text; cfg.Notifications.DownloadCompleted = download.Text; cfg.Notifications.FileReady = file.Text; }); Feedback("标题模板已保存。");
        });
        var storage = Card("本地数据", _app.Paths.Root); Action(Actions(storage), "打开数据目录", () => WindowsIntegration.Open(_app.Paths.Root));
        storage.Children.Add(Text("Portable 模式可使用 --portable 或在程序旁创建 portable.flag。凭证绑定当前 Windows 账户，换电脑或账户需要重新输入。"));
    }
    private void Diagnostics()
    {
        Heading("诊断与日志", "查看实际状态和可操作的排障建议。日志不保存消息全文。");
        var card = Card("运行检查"); var output = Field(card, "诊断结果", "点击运行诊断；网络检查会连接腾讯和 GitHub。", true); output.IsReadOnly = true; output.MinHeight = 250;
        var actions = Actions(card);
        Button(actions, "运行诊断", async () =>
        {
            output.Text = "正在检查…";
            var checks = await Doctor.RunAsync(_app, true);
            output.Text = string.Join("\n\n", checks.Select(c => $"[{c.State}] {c.Name}\n{c.Detail}")); Feedback("诊断已完成。");
        }, true);
        Action(actions, "打开日志", () => WindowsIntegration.Open(_app.Paths.Logs));
        Action(actions, "复制诊断", () => { Clipboard.SetText(output.Text); Feedback("诊断结果已复制。"); });
        var update = Card("软件更新"); State(update, "update", "当前状态");
        var updates = Actions(update); Button(updates, "检查更新", CheckUpdateAsync); Button(updates, "下载并更新", async () =>
        {
            var result = await _app.Updater.CheckAsync(BuildInfo.Version, true);
            if (result.Release is not { Newer: true } release) { Feedback(result.Error ?? "没有可安装的新版本。"); return; }
            Feedback("正在下载并验证更新…");
            var staged = await _app.Updater.DownloadAsync(release);
            await WindowsIntegration.ScheduleUpdateAsync(staged, _app.Paths); Exiting = true; Application.Current.Shutdown();
        });
    }
    private async Task CheckUpdateAsync()
    {
        var result = await _app.Updater.CheckAsync(BuildInfo.Version, true); Feedback(result.Error ?? (result.Release?.Newer == true ? "发现新版本 " + result.Release.Version : "已是最新版本。")); RefreshStatus();
    }
    private void Code(StackPanel parent, string value)
    {
        var box = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new("Cascadia Code, Consolas"), AcceptsReturn = true };
        AutomationProperties.SetName(box, "集成代码"); parent.Children.Add(box);
    }
    private static int Number(string input, int min, int max) => int.TryParse(input, out var n) && n >= min && n <= max ? n : throw new AppException("INPUT_NUMBER", $"输入 {min}–{max} 之间的整数。");
    private void RefreshStatus()
    {
        var c = _app.Config.Load();
        void Set(string key, string value) { if (_status.TryGetValue(key, out var text)) text.Text = value; }
        Set("qq", _app.Gateway?.Status ?? (string.IsNullOrEmpty(c.Qq.AppId) ? "未配置" : _app.Qq.Status));
        Set("c2c", c.Qq.Targets.Any(t => t.Kind == "c2c" && t.Enabled) ? "已绑定" : "未绑定"); Set("group", c.Qq.Targets.Any(t => t.Kind == "group" && t.Enabled) ? "已绑定" : "未绑定");
        Set("codex", (_app.Codex?.Status ?? "监听关闭") + (new CodexIntegration(_app.Paths).IsInstalled() ? " · notify 已连接" : " · notify 未集成"));
        Set("background", c.Notifications.Paused ? "运行中 · 通知已暂停" : "运行中");
        Set("api", _app.Api?.Status ?? "已关闭"); Set("downloads", _app.Files?.Status ?? "已关闭");
        Set("delivery", _app.Dispatcher.LastResult is { } last ? last.Ok ? "最近发送成功" : last.Error ?? last.Status : "还没有发送记录");
        var update = _app.Updater.Cached(); Set("update", update?.Error ?? (update?.Release?.Newer == true ? "发现新版本 " + update.Release.Version : update is null ? "尚未检查" : "已是最新版本"));
        lock (_app.Errors) foreach (var (key, error) in _app.Errors) Set(key == "Downloads" ? "downloads" : key == "API" ? "api" : key.ToLowerInvariant(), error);
        TrayStatus.Text = c.Notifications.Paused ? "Ⅱ  通知已暂停" : "●  后台运行中";
    }
    private void ApplyTheme()
    {
        var c = _app.Config.Load().Ui;
        var dark = c.Theme == "dark" || c.Theme == "system" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        var colors = dark
            ? new[] { "#1C1C1E", "#2C2C2E", "#272930", "#F5F5F7", "#B4B4BD", "#404047", "#0066CC", "#364D6A", "#737380" }
            : new[] { "#F5F5F7", "#FFFFFF", "#EDEFF5", "#1D1D1F", "#616169", "#E7E8ED", "#0066CC", "#DCE8FA", "#90939E" };
        var keys = new[] { "Page", "Surface", "Sidebar", "Label", "Secondary", "Fill", "Accent", "Selection", "Border" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "Page", "Surface", "Sidebar", "Fill" }) Application.Current.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Label", "Secondary", "Border" }) Application.Current.Resources[key] = SystemColors.WindowTextBrush;
            Application.Current.Resources["Accent"] = SystemColors.HighlightBrush; Application.Current.Resources["Selection"] = SystemColors.ControlBrush;
        }
        SidebarBorder.Opacity = c.ReduceTransparency || SystemParameters.HighContrast ? 1 : 0.97;
        RootGrid.LayoutTransform = new ScaleTransform(c.TextScale, c.TextScale);
        MinWidth = 780 * Math.Min(c.TextScale, 1.4);
    }
}

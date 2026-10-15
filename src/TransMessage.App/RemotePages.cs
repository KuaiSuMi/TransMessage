using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;
using TransMessage.Core.Security;

namespace TransMessage.App;

public partial class MainWindow
{
    private void History()
    {
        Heading("每条通知，都有记录", "保留最近 30 天、最多 500 条。内容先脱敏，再由 Windows 加密保存，仅当前账户可读。");
        var searchCard = Card("查找通知"); var query = Field(searchCard, "标题、内容或来源", "");
        var filter = new ComboBox { ItemsSource = new[] { "全部状态", "已发送", "发送失败", "送达不确定" }, SelectedIndex = 0 };
        AutomationProperties.SetName(filter, "历史状态筛选"); searchCard.Children.Add(filter);
        var list = new StackPanel(); PageContent.Children.Add(list);
        void Render()
        {
            list.Children.Clear();
            IReadOnlyList<HistoryEntry> history;
            try { history = _app.Dispatcher.History.Read(); }
            catch (Exception e) { list.Children.Add(Text(AppException.Describe(e) + " 原文件保留，请在诊断页打开数据目录后备份检查。")); return; }
            var rows = history.Where(x => (query.Text.Length == 0 || $"{x.Notification.Title}\n{x.Notification.Message}\n{x.Notification.Source}".Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (filter.SelectedIndex == 0 || NotificationHistory.Label(x.Status) == (string)filter.SelectedItem)).Take(50).ToArray();
            list.Children.Add(Text(rows.Length == 0 ? "还没有匹配的通知。发送测试通知后，这里会记录结果。" : $"显示最近 {rows.Length} 条匹配记录。"));
            foreach (var entry in rows)
            {
                var content = new StackPanel { Margin = new(0, 12, 0, 12) };
                content.Children.Add(Text($"{entry.Notification.Source} · {entry.Route}", true));
                content.Children.Add(Text(entry.Notification.Message, false));
                if (entry.Error is not null) content.Children.Add(Text(entry.Error));
                Action(Actions(content), "复制这条记录", () => { Clipboard.SetText($"[{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}] {entry.Notification.Title}\n{entry.Notification.Message}\n{NotificationHistory.Label(entry.Status)}\n{entry.Error}"); Feedback("已复制脱敏记录。"); });
                var expander = new Expander
                {
                    Header = Text($"{entry.Time.ToLocalTime():MM-dd HH:mm:ss zzz} · {NotificationHistory.Label(entry.Status)}\n{entry.Notification.Title}", false),
                    Content = content,
                    Padding = new(16),
                    Margin = new(0, 0, 0, 12),
                    MinHeight = 64
                };
                expander.SetResourceReference(Control.BackgroundProperty, "Surface"); expander.SetResourceReference(Control.ForegroundProperty, "Label");
                AutomationProperties.SetName(expander, entry.Notification.Title + "，" + NotificationHistory.Label(entry.Status)); list.Children.Add(expander);
            }
        }
        var actions = Actions(searchCard); Action(actions, "查询 / 刷新", Render, true);
        Action(actions, "清空历史", () =>
        {
            if (MessageBox.Show(this, "这会删除本机保存的通知历史，不影响 QQ 消息与通知去重。", "清空通知历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _app.Dispatcher.History.Clear(); Render();
        });
        Render();
    }
    private void RemoteControl()
    {
        Heading("在 QQ，接着做", "只允许你选定的私聊控制者与会话。群消息不会启动任务，也不能回答问题。");
        var c = _app.Config.Load(); var settings = c.RemoteCodex;
        var connection = Card("控制连接", "Desktop 共享队列用于查询和排队续写；托管会话由 TransMessage 运行，支持实时问题与回答。切换连接或停用控制会结束正在运行的托管连接。");
        var enabled = Check(connection, "启用 QQ 私聊控制", settings.Enabled);
        connection.Children.Add(Text("谁可以控制", false));
        var owners = c.Qq.Targets.Where(t => t.Kind == "c2c" && t.Enabled).ToArray();
        var owner = new ComboBox { ItemsSource = owners, SelectedItem = owners.FirstOrDefault(t => t.Id == settings.OwnerId) };
        AutomationProperties.SetName(owner, "私聊控制者"); connection.Children.Add(owner);
        if (owners.Length == 0) Action(Actions(connection), "先绑定手机私聊", () => Go("qq"));
        connection.Children.Add(Text("连接方式", false));
        var mode = new ComboBox { ItemsSource = new[] { "现有 Desktop · 共享消息队列", "TransMessage 托管 · 实时问答" }, SelectedIndex = settings.Mode == "managed" ? 1 : 0 };
        AutomationProperties.SetName(mode, "Codex 连接方式"); connection.Children.Add(mode);
        var executable = Field(connection, "Codex 程序（留空自动发现原生 exe）", settings.Executable);
        Action(Actions(connection), "选择 codex.exe…", () => { var dialog = new OpenFileDialog { Filter = "Codex 可执行程序|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) executable.Text = dialog.FileName; });
        var buttons = Actions(connection);
        Button(buttons, "保存并连接", async () =>
        {
            var nextMode = mode.SelectedIndex == 1 ? "managed" : "desktop";
            if (settings.Enabled && settings.Mode == "managed" && (nextMode != settings.Mode || enabled.IsChecked != true || executable.Text.Trim() != settings.Executable || owner.SelectedItem is QqTarget nextOwner && nextOwner.Id != settings.OwnerId)
                && MessageBox.Show(this, "更改连接会结束当前托管进程，未完成工作可能中断。", "更改控制连接", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _app.Config.Update(cfg => { cfg.RemoteCodex.Enabled = enabled.IsChecked == true; cfg.RemoteCodex.OwnerId = (owner.SelectedItem as QqTarget)?.Id ?? ""; cfg.RemoteCodex.Mode = nextMode; cfg.RemoteCodex.Executable = executable.Text.Trim(); });
            await _app.Remote.ApplySettingsAsync(); Feedback(_app.Remote.Status); ReloadPage();
        }, true);
        Button(buttons, "检测现有 Desktop", async () => Feedback(await _app.Remote.ProbeDesktopAsync()));
        State(connection, "remote", "当前连接"); State(connection, "questions", "等待回答");

        var threads = Card("允许访问的会话", "只有这里列出的会话能够从 QQ 查询或继续。编号固定来自会话 ID，避免不同项目串话。");
        foreach (var thread in settings.Threads.Where(t => t.Managed == (settings.Mode == "managed")))
        {
            threads.Children.Add(Text($"#{thread.Id[..8]} · {thread.Name}", false));
            Action(Actions(threads), "撤销此会话访问", () => { _app.Config.Update(cfg => cfg.RemoteCodex.Threads.RemoveAll(t => t.Id == thread.Id)); ReloadPage(); });
        }
        if (settings.Mode == "desktop")
        {
            threads.Children.Add(Text("追加消息会写入官方共享队列，由 Desktop 消费。无法据此确认它已开始执行，也不能把排队消息当作原窗口的审批回答。"));
            var candidates = new ComboBox(); AutomationProperties.SetName(candidates, "本机 Codex 会话"); threads.Children.Add(candidates);
            var actions = Actions(threads);
            Button(actions, "查找最近会话", async () => { candidates.ItemsSource = await _app.Remote.DiscoverAsync(); Feedback("选择会话后，授权 QQ 访问。"); });
            Action(actions, "授权选中会话", () =>
            {
                if (candidates.SelectedItem is not CodexTaskInfo selected) throw new AppException("CONTROL_THREAD", "先选择一个会话。");
                _app.Config.Update(cfg => { if (cfg.RemoteCodex.Threads.All(t => t.Id != selected.Id)) cfg.RemoteCodex.Threads.Add(new() { Id = selected.Id, Name = Sanitizer.Truncate(selected.Name, 100) }); }); ReloadPage();
            });
        }
        else
        {
            var name = Field(threads, "新会话名称", "我的远程任务"); var directory = Field(threads, "项目目录", "");
            var actions = Actions(threads);
            Action(actions, "选择项目目录…", () => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) directory.Text = dialog.FolderName; });
            Button(actions, "创建可控会话", async () => { var thread = await _app.Remote.CreateManagedAsync(directory.Text, name.Text); Feedback($"会话 #{thread.Id[..8]} 已创建。在 QQ 发送“继续 {thread.Id[..8]} 你的要求”。"); ReloadPage(); });
        }
        var pending = Card("等待你的回答", "问题码只对当前会话、当前请求有效。连接断开或问题已结束后，旧回答不会用于新的请求。有效期 15 分钟。");
        Action(Actions(pending), "刷新待回答问题", ReloadPage);
        foreach (var request in _app.Remote.Pending)
        {
            pending.Children.Add(Text(_app.Sanitizer.Redact(RemoteCodex.Describe(request)), false));
            var answers = new List<Func<string>>();
            foreach (var question in request.Questions)
            {
                if (question.Secret)
                {
                    pending.Children.Add(Text(question.Text, false)); pending.Children.Add(Text("敏感答案仅在本机输入，不记录历史。")); var password = new PasswordBox(); AutomationProperties.SetName(password, "本机敏感答案"); pending.Children.Add(password); answers.Add(() => password.Password);
                }
                else { var field = Field(pending, question.Text, ""); answers.Add(() => field.Text); }
            }
            var actions = Actions(pending);
            if (request.Questions.Length > 0) Button(actions, "发送回答", async () => { await _app.Remote.AnswerAsync(request.Code, string.Join(" | ", answers.Select(a => a())), true); Feedback("Codex 已确认回答。"); ReloadPage(); });
            else
            {
                if (request.CanApprove) Button(actions, "仅批准本次", async () => { await _app.Remote.AnswerAsync(request.Code, "accept", true); ReloadPage(); });
                Button(actions, "拒绝本次", async () => { await _app.Remote.AnswerAsync(request.Code, "decline", true); ReloadPage(); });
            }
        }
        if (_app.Remote.Pending.Count == 0) pending.Children.Add(Text(settings.Mode == "desktop" ? "此模式无法获取现有 Desktop 的待输入状态。需要手机提醒与回答时，请使用托管会话。" : "当前没有待回答问题。"));
        var commands = Card("在 QQ 中这样说"); Code(commands, RemoteCodex.Help);
    }
}

using System.Windows;
using TransMessage.Core;
using TransMessage.Core.Configuration;
using TransMessage.Core.Hosting;
using TransMessage.Core.Notifications;
using Forms = System.Windows.Forms;

namespace TransMessage.App;

internal static class GuiHost
{
    public static int Run(AppPaths paths, bool background)
    {
        using var mutex = new Mutex(false, @"Local\TransMessage-App-" + paths.InstanceId);
        bool owned;
        try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            new InstancePipe(paths).TrySendAsync(new(background ? "status" : "show")).GetAwaiter().GetResult(); return 0;
        }
        try
        {
            using var stop = new CancellationTokenSource();
            var runtime = new AppRuntime(paths);
            if (!File.Exists(paths.Config)) runtime.Config.Save(runtime.Config.Load());
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TransMessage;component/Theme.xaml", UriKind.Relative) });
            var window = new MainWindow(runtime);
            application.MainWindow = window;
            using var tray = new Forms.NotifyIcon
            {
                Text = "TransMessage · 任务完成通知",
                Visible = true,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(WindowsIntegration.Executable) ?? System.Drawing.SystemIcons.Information
            };
            var menu = new Forms.ContextMenuStrip();
            void Show() => application.Dispatcher.Invoke(() => { window.Show(); if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); });
            menu.Items.Add("打开 TransMessage", null, (_, _) => Show());
            menu.Items.Add("发送测试通知", null, async (_, _) => await window.ExecuteAsync(async () =>
            {
                var result = await runtime.Dispatcher.SendAsync(new Notification { Title = "TransMessage 测试通知", Message = "电脑与 QQ 已连接。", Level = "success" });
                window.Feedback(result.Ok ? "测试通知已发送。" : result.Error!);
            }));
            var pause = new Forms.ToolStripMenuItem("暂停通知") { CheckOnClick = true, Checked = runtime.Config.Load().Notifications.Paused };
            pause.Click += (_, _) => runtime.Config.Update(c => c.Notifications.Paused = pause.Checked); menu.Items.Add(pause);
            menu.Items.Add("打开日志", null, (_, _) => WindowsIntegration.Open(paths.Logs));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => application.Dispatcher.Invoke(application.Shutdown));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Show();
            window.Closing += (_, e) =>
            {
                if (window.Exiting) return;
                e.Cancel = true; window.Hide();
                if (!runtime.Config.Load().Ui.TrayHintShown)
                {
                    tray.ShowBalloonTip(5000, "TransMessage 仍在后台运行", "右下角托盘图标可重新打开；右键选择“退出”可停止通知。", Forms.ToolTipIcon.Info);
                    runtime.Config.Update(c => c.Ui.TrayHintShown = true);
                }
            };
            runtime.ShowRequested += Show;
            runtime.StopRequested += () => application.Dispatcher.BeginInvoke(() => { window.Exiting = true; application.Shutdown(); });
            application.SessionEnding += (_, _) => { window.Exiting = true; stop.Cancel(); };
            application.DispatcherUnhandledException += (_, e) => { window.Feedback(AppException.Describe(e.Exception)); e.Handled = true; };
            application.Startup += async (_, _) =>
            {
                if (!background) window.Show();
                await window.ExecuteAsync(() => runtime.StartServicesAsync(stop.Token));
            };
            var pipeTask = Task.Run(() => runtime.RunPipeAsync(stop.Token));
            application.Run();
            window.Exiting = true; tray.Visible = false; stop.Cancel();
            Task.Run(async () => { await pipeTask; await runtime.DisposeAsync(); }).GetAwaiter().GetResult();
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }
}

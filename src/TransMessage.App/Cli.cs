using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Hosting;
using TransMessage.Core.Notifications;
using TransMessage.Core.Sources;

namespace TransMessage.App;

internal static class Cli
{
    public static Task<int> InvokeAsync(string[] args)
    {
        var root = new RootCommand("TransMessage · 将任务完成通知发送到 QQ");
        var portable = new Option<bool>("--portable") { Description = "将数据写到程序旁的 data 目录", Recursive = true };
        var dataDir = new Option<string?>("--data-dir") { Description = "指定数据目录", Recursive = true };
        var background = new Option<bool>("--background") { Description = "启动托盘后台" };
        root.Options.Add(portable); root.Options.Add(dataDir); root.Options.Add(background);
        AppPaths Paths(System.CommandLine.ParseResult p) => new(p.GetValue(dataDir), p.GetValue(portable));
        root.SetAction(p => GuiHost.Run(Paths(p), p.GetValue(background)));
        var gui = new Command("gui", "打开图形界面"); gui.SetAction(p => GuiHost.Run(Paths(p), false)); root.Subcommands.Add(gui);
        var version = new Command("version", "显示版本与构建信息");
        version.SetAction(_ =>
        {
            var meta = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            Console.WriteLine($"TransMessage {BuildInfo.Version}\ncommit: {meta.GetValueOrDefault("Commit", "local")}\nbuild time: {meta.GetValueOrDefault("BuildTime", "local")}\nruntime: {RuntimeInformation.FrameworkDescription}\nos: {RuntimeInformation.OSDescription}\narch: {RuntimeInformation.ProcessArchitecture}"); return 0;
        }); root.Subcommands.Add(version);

        var send = new Command("send", "发送通知");
        var text = new Argument<string?>("text") { Arity = ArgumentArity.ZeroOrOne };
        var title = new Option<string>("--title") { DefaultValueFactory = _ => "通知" };
        var message = new Option<string?>("--message");
        var level = new Option<string>("--level") { DefaultValueFactory = _ => "info" };
        send.Arguments.Add(text); send.Options.Add(title); send.Options.Add(message); send.Options.Add(level);
        send.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p));
            var result = await app.SendAsync(new() { Title = p.GetValue(title)!, Message = p.GetValue(message) ?? p.GetValue(text) ?? "", Level = p.GetValue(level)! }, ct);
            Console.WriteLine(result.Ok ? result.Status == "duplicate" ? "已跳过重复通知。" : "通知已发送。" : result.Error); return result.Ok ? 0 : 1;
        }); root.Subcommands.Add(send);

        var run = new Command("run", "运行命令，结束后通知，并保留退出码（参数放在 -- 后）");
        var command = new Argument<string[]>("command") { Arity = ArgumentArity.OneOrMore }; run.Arguments.Add(command);
        run.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p)); var values = p.GetValue(command)!;
            var result = await new CommandRunner(app.Sanitizer).RunAsync(values, Console.Out, Console.Error, app.Config.Load().Notifications.TailLines, ct);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var delivery = await app.SendAsync(CommandRunner.NotificationFor(values, result, app.Config.Load()), timeout.Token);
                if (!delivery.Ok) Console.Error.WriteLine("通知警告：" + delivery.Error);
            }
            catch (Exception e) { Console.Error.WriteLine("通知警告：" + AppException.Describe(e)); }
            return result.ExitCode;
        }); root.Subcommands.Add(run);

        foreach (var name in new[] { "watch-file", "watch-dir" })
        {
            var watch = new Command(name, name == "watch-file" ? "文件出现并稳定后通知一次" : "持续监控目录中新完成的文件");
            var path = new Argument<string>("path"); watch.Arguments.Add(path);
            watch.SetAction(async (p, ct) =>
            {
                await using var app = new AppRuntime(Paths(p)); var full = Path.GetFullPath(p.GetValue(path)!);
                var directory = name == "watch-file" ? Path.GetDirectoryName(full)! : full;
                if (!Directory.Exists(directory)) throw new AppException("WATCH_DIRECTORY", "监控目录不存在，请先创建目录。");
                using var watcher = new StableFileWatcher(app.Config, app.SendAsync, app.Log);
                Console.WriteLine("正在监控；Ctrl+C 停止。");
                await watcher.RunAsync([directory], ct, name == "watch-file" ? full : null); return 0;
            }); root.Subcommands.Add(watch);
        }
        var serve = new Command("serve", "运行托盘后台服务"); serve.SetAction(p => GuiHost.Run(Paths(p), true)); root.Subcommands.Add(serve);
        var doctor = new Command("doctor", "诊断本地配置与服务"); var online = new Option<bool>("--online") { Description = "同时验证 QQ 认证及 GitHub 更新源" }; doctor.Options.Add(online);
        doctor.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p)); var checks = await Doctor.RunAsync(app, p.GetValue(online), ct);
            foreach (var check in checks) Console.WriteLine($"[{check.State}] {check.Name}：{check.Detail}");
            Console.WriteLine($"{checks.Count(c => c.State == "OK")} passed · {checks.Count(c => c.State == "WARN")} warnings · {checks.Count(c => c.State == "FAIL")} failed");
            return checks.Any(c => c.State == "FAIL") ? 1 : 0;
        }); root.Subcommands.Add(doctor);

        var codex = new Command("codex", "Codex 完成通知集成");
        var probe = new Command("probe-desktop", "只读检查现有 Desktop 控制连接和会话发现");
        probe.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p));
            Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {await app.Remote.ProbeDesktopAsync(ct)}");
            var threads = await app.Remote.DiscoverAsync(ct);
            Console.WriteLine($"已发现 {threads.Count} 个本机会话；未恢复会话或提交任何提示。"); return 0;
        }); codex.Subcommands.Add(probe);
        foreach (var action in new[] { "install", "uninstall", "status" })
        {
            var sub = new Command(action); sub.SetAction(p =>
            {
                var integration = new CodexIntegration(Paths(p));
                if (action == "install") { integration.Install(WindowsIntegration.Executable); Console.WriteLine("已连接 Codex；重启已有 Codex 会话后生效。"); }
                if (action == "uninstall") Console.WriteLine(integration.Uninstall() ? "已恢复原 notify 设置。" : "无需卸载。");
                if (action == "status") Console.WriteLine(integration.IsInstalled() ? "已集成" : "未集成");
                return 0;
            }); codex.Subcommands.Add(sub);
        }
        var notify = new Command("notify", "处理 Codex 官方 notify JSON"); var payload = new Argument<string>("payload"); notify.Arguments.Add(payload);
        notify.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p)); var json = p.GetValue(payload)!;
            var integration = new CodexIntegration(app.Paths);
            try
            {
                using var document = JsonDocument.Parse(json);
                var thread = CodexEvents.Text(document.RootElement, "thread-id") ?? "";
                var identity = CodexEvents.FindIdentity(AppPaths.CodexHome, thread);
                var notification = CodexEvents.ParseNotify(json, app.Config.Load(), identity, app.Sanitizer);
                if (notification is not null) await app.SendAsync(notification, ct);
                else app.Log.Information("Codex event skipped: unknown or non-user schema");
            }
            catch (JsonException) { app.Log.Warning("Codex malformed JSON skipped"); }
            finally { try { await integration.ForwardPreviousAsync(json); } catch (Exception e) { app.Log.Warning("Previous hook failed: {Kind}", e.GetType().Name); } }
            return 0;
        }); codex.Subcommands.Add(notify); root.Subcommands.Add(codex);

        var startup = new Command("startup", "管理当前用户登录启动");
        foreach (var action in new[] { "enable", "disable" })
        {
            var sub = new Command(action); sub.SetAction(p => { WindowsIntegration.SetStartup(action == "enable", Paths(p)); Console.WriteLine(action == "enable" ? "已启用登录启动。" : "已关闭登录启动。"); return 0; }); startup.Subcommands.Add(sub);
        }
        root.Subcommands.Add(startup);
        var setup = new Command("setup", "交互式配置 QQ 和完成通知"); setup.SetAction(async (p, ct) => await SetupAsync(Paths(p), ct)); root.Subcommands.Add(setup);

        var update = new Command("update", "检查 GitHub Release；--install 下载验证并更新"); var install = new Option<bool>("--install"); update.Options.Add(install);
        update.SetAction(async (p, ct) =>
        {
            await using var app = new AppRuntime(Paths(p)); var result = await app.Updater.CheckAsync(BuildInfo.Version, true, ct);
            Console.WriteLine(result.Error ?? (result.Release?.Newer == true ? "发现新版本 " + result.Release.Version : "已是最新版本。"));
            if (p.GetValue(install) && result.Release is { Newer: true } release)
            {
                var staged = await app.Updater.DownloadAsync(release, ct); await WindowsIntegration.ScheduleUpdateAsync(staged, app.Paths); Console.WriteLine("校验通过，程序退出后自动更新。");
            }
            return result.Error is null ? 0 : 1;
        }); root.Subcommands.Add(update);
        var apply = new Command("apply-update") { Hidden = true };
        var stagedOption = new Option<string>("--staged") { Required = true }; var target = new Option<string>("--target") { Required = true }; var pid = new Option<int>("--pid") { Required = true };
        apply.Options.Add(stagedOption); apply.Options.Add(target); apply.Options.Add(pid);
        apply.SetAction(async (p, _) => await WindowsIntegration.ApplyUpdateAsync(p.GetValue(stagedOption)!, p.GetValue(target)!, p.GetValue(pid), Paths(p))); root.Subcommands.Add(apply);
        return root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = TimeSpan.FromSeconds(90) });
    }
    private static async Task<int> SetupAsync(AppPaths paths, CancellationToken ct)
    {
        await using var app = new AppRuntime(paths);
        Console.WriteLine("在 https://q.qq.com 创建机器人，按控制台要求配置沙箱成员、群和事件权限。凭证仅保存在当前 Windows 账户。\nAppID：");
        var id = Console.ReadLine()?.Trim() ?? "";
        Console.Write("AppSecret（输入不回显）：");
        var secret = new System.Text.StringBuilder();
        if (Console.IsInputRedirected) throw new AppException("SETUP_INTERACTIVE", "setup 需要交互终端；也可双击使用 GUI。");
        while (true)
        {
            var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; }
            else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
        }
        Console.WriteLine(); app.Secrets.Set("qq", secret.ToString()); secret.Clear(); app.Config.Update(c => c.Qq.AppId = id);
        await app.Qq.GetTokenAsync(ct); Console.WriteLine("认证成功。");
        await WindowsIntegration.EnsureBackgroundAsync(paths); await app.Pipe.TrySendAsync(new("reload"), ct);
        foreach (var kind in new[] { "c2c", "group" })
        {
            Console.Write(kind == "c2c" ? "绑定私聊？[Y/n] " : "绑定 QQ 群？[Y/n] ");
            if (Console.ReadLine()?.Equals("n", StringComparison.OrdinalIgnoreCase) == true) continue;
            var reply = await app.Pipe.TrySendAsync(new("bind", Kind: kind), ct);
            Console.WriteLine($"{(kind == "c2c" ? "向机器人私聊发送" : "在目标群 @机器人并发送")}：bind {reply?.Message}\n发送后按 Enter 继续（绑定码 5 分钟有效）。"); Console.ReadLine();
        }
        Console.Write("连接 Codex？[Y/n] "); if (Console.ReadLine()?.Equals("n", StringComparison.OrdinalIgnoreCase) != true) new CodexIntegration(paths).Install(WindowsIntegration.Executable);
        Console.Write("自动监控下载目录？[Y/n] "); var downloads = Console.ReadLine()?.Equals("n", StringComparison.OrdinalIgnoreCase) != true; app.Config.Update(c => c.Downloads.AutoDownloads = downloads);
        Console.Write("登录 Windows 后启动？[y/N] "); if (Console.ReadLine()?.Equals("y", StringComparison.OrdinalIgnoreCase) == true) WindowsIntegration.SetStartup(true, paths);
        await app.Pipe.TrySendAsync(new("reload"), ct);
        var sent = await app.SendAsync(new() { Title = "TransMessage 已就绪", Message = "这是一条测试通知。", Level = "success" }, ct);
        Console.WriteLine(sent.Ok ? "测试通知已发送。" : sent.Error); return sent.Ok ? 0 : 1;
    }
}

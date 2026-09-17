using System.Diagnostics;
using Microsoft.Win32;
using TransMessage.Core;
using TransMessage.Core.Configuration;
using TransMessage.Core.Hosting;

namespace TransMessage.App;

internal static class WindowsIntegration
{
    public static string Executable => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TransMessage.exe");
    public static bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue("TransMessage") is string;
        }
    }
    public static void SetStartup(bool enabled, AppPaths paths)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("TransMessage", $"\"{Executable}\" --data-dir \"{paths.Root}\" --background");
        else key.DeleteValue("TransMessage", false);
    }
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static Process? StartBackground(AppPaths paths)
    {
        var start = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(paths.Root); start.ArgumentList.Add("--background");
        return Process.Start(start);
    }
    public static async Task EnsureBackgroundAsync(AppPaths paths)
    {
        var pipe = new InstancePipe(paths);
        if (await pipe.TrySendAsync(new("status")) is not null) return;
        StartBackground(paths)?.Dispose();
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            if (await pipe.TrySendAsync(new("status")) is not null) return;
        }
        throw new AppException("BACKGROUND_START", "后台启动失败，请打开日志或运行 doctor。");
    }
    public static async Task ScheduleUpdateAsync(string staged, AppPaths paths)
    {
        var stageDirectory = Path.GetDirectoryName(staged)!;
        var helper = Path.Combine(stageDirectory, "TransMessage.Updater.exe");
        File.Copy(Executable, helper, true);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "--data-dir", paths.Root, "apply-update", "--staged", staged, "--target", Executable, "--pid", Environment.ProcessId.ToString() }) start.ArgumentList.Add(arg);
        Process.Start(start)?.Dispose();
        await new InstancePipe(paths).TrySendAsync(new("stop"));
    }
    public static async Task<int> ApplyUpdateAsync(string staged, string target, int pid, AppPaths paths)
    {
        try
        {
            using var previous = Process.GetProcessById(pid);
            await previous.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (ArgumentException) { }
        var destination = Path.GetFullPath(target);
        var backup = destination + ".previous";
        var temporary = destination + ".updating";
        File.Copy(staged, temporary, true);
        var replaced = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(temporary, destination, backup, true); replaced = true; break; }
                catch (IOException) when (attempt < 30) { await Task.Delay(500); }
            }
            var start = new ProcessStartInfo(destination) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--data-dir", paths.Root, "--background" }) start.ArgumentList.Add(arg);
            using var launched = Process.Start(start) ?? throw new AppException("UPDATE_START", "新版启动失败。");
            var pipe = new InstancePipe(paths);
            var ready = false;
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(500);
                if (launched.HasExited) break;
                if (await pipe.TrySendAsync(new("status")) is { Ok: true }) { ready = true; break; }
            }
            if (!ready)
            {
                if (!launched.HasExited) { launched.Kill(true); await launched.WaitForExitAsync(); }
                throw new AppException("UPDATE_HEALTH", "新版未正常启动，已回滚。");
            }
            AtomicFile.Write(Path.Combine(paths.Root, "update-result.txt"), "更新成功。旧版本保存在 TransMessage.exe.previous。");
            return 0;
        }
        catch (Exception e)
        {
            if (replaced && File.Exists(backup)) File.Move(backup, destination, true);
            AtomicFile.Write(Path.Combine(paths.Root, "update-result.txt"), AppException.Describe(e));
            var restart = new ProcessStartInfo(destination) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var arg in new[] { "--data-dir", paths.Root, "--background" }) restart.ArgumentList.Add(arg);
            Process.Start(restart)?.Dispose();
            return 1;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TransMessage.Core.Codex;

public static class DesktopWake
{
    public static Task RequestAsync(string threadId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(threadId, "D", out var id)) throw new AppException("CONTROL_THREAD", "会话 ID 无效。");
        ct.ThrowIfCancellationRequested();
        var appId = FindRunningDesktopAppId();
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"))!;
        var manager = (IApplicationActivationManager)(Activator.CreateInstance(type) ?? throw new AppException("CODEX_DESKTOP_WAKE", "Windows 应用激活服务不可用。"));
        try
        {
            // MSIX apps require Windows activation. Desktop handles this route without navigation.
            // AO_NOERRORUI suppresses dialogs; AO_NOSPLASHSCREEN requires package debug mode.
            var result = manager.ActivateApplication(appId, $"--single-argument codex://threads/wake/{id:D}", 2, out _);
            if (result < 0) throw new AppException("CODEX_DESKTOP_WAKE", $"Desktop 后台唤醒失败（Windows 0x{result:X8}）。");
        }
        finally { Marshal.FinalReleaseComObject(manager); }
        return Task.CompletedTask;
    }

    private static string FindRunningDesktopAppId()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
                    var app = Directory.GetParent(path); var package = app?.Parent;
                    if (app?.Name.Equals("app", StringComparison.OrdinalIgnoreCase) == true
                        && package?.Name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) == true
                        && package.Name.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
                        && package.Parent?.Name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        using var handle = OpenProcess(0x1000, false, process.Id);
                        if (handle.IsInvalid) continue;
                        uint length = 0;
                        if (GetApplicationUserModelId(handle, ref length, null) != 122 || length is 0 or > 512) continue;
                        var appId = new StringBuilder((int)length);
                        if (GetApplicationUserModelId(handle, ref length, appId) == 0) return appId.ToString();
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        throw new AppException("CODEX_DESKTOP_NOT_RUNNING", "请先打开 Codex Desktop。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder? appId);

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, out uint processId);
    }
}

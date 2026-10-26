using Microsoft.Win32;

namespace CampusAP.App.Services;

/// <summary>
/// 开机自启动：写 HKCU Run 键（用户级，免管理员，随用户登录启动）。
/// 注册表是唯一事实源——IsEnabled 直接读键，取消勾选即删键，无 settings.json 冗余。
/// </summary>
public static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CampusAP";

    /// <summary>当前是否已启用（以注册表实际状态为准）</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>启用/停用开机自启动</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            // 路径带空格（如 "WiFi共享助手\发布"）必须加引号
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

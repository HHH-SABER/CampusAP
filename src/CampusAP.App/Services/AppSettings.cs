using System.IO;
using System.Text.Json;

namespace CampusAP.App.Services;

public enum CloseAction
{
    /// <summary>每次询问（默认）</summary>
    Ask = 0,
    /// <summary>直接退出</summary>
    Exit = 1,
    /// <summary>隐藏到系统托盘</summary>
    MinimizeToTray = 2,
}

/// <summary>应用设置，存 %APPDATA%\CampusAP\settings.json</summary>
public sealed class AppSettings
{
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>设备自定义名：键为去冒号大写的 MAC（无 MAC 时 "ip:地址"），值可为空串（=清除自定义名）</summary>
    public Dictionary<string, string> DeviceNames { get; set; } = new();

    private static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAP");

    private static string FilePath => Path.Combine(DirPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

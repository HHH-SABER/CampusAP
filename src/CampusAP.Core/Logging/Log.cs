using System.Collections.Concurrent;
using System.IO;

namespace CampusAP.Core.Logging;

/// <summary>
/// 轻量实时日志：内存环形缓冲（供 UI 实时查看）+ 按天滚动文件（%APPDATA%\CampusAP\logs\）。
/// 线程安全、绝不抛异常（日志故障不影响主流程）。
/// 纪律：包处理热路径不写日志，只记生命周期/错误/异常/统计心跳。
/// </summary>
public static class Log
{
    private static readonly ConcurrentQueue<string> _recent = new();
    private const int RecentCapacity = 600;
    private static readonly object _fileLock = new();

    /// <summary>新日志条目事件（UI 订阅，跨线程需自行调度）</summary>
    public static event Action<string>? EntryAdded;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAP", "logs");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _)) { }

            lock (_fileLock)
            {
                var path = Path.Combine(LogDirectory, $"CampusAP-{DateTime.Now:yyyyMMdd}.log");
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(path, line + Environment.NewLine);
            }

            EntryAdded?.Invoke(line);
        }
        catch
        {
            // 日志自身的故障静默
        }
    }

    /// <summary>启动时把当天日志截到最近 200 行（防文件无限膨胀）+ 清理 7 天前的旧日志</summary>
    public static void Trim()
    {
        try
        {
            var path = Path.Combine(LogDirectory, $"CampusAP-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length > 200)
                    File.WriteAllLines(path, lines[^200..]);
            }
            foreach (var f in Directory.GetFiles(LogDirectory, "CampusAP-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7))
                    File.Delete(f);
        }
        catch { }
    }
}

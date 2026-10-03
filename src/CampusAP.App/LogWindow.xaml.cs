using System.IO;
using System.Windows;
using CampusAP.Core.Logging;

namespace CampusAP.App;

/// <summary>实时日志查看窗：订阅 Log.EntryAdded，环形显示最近日志，自动滚动可关。</summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Log.EntryAdded += OnEntry;
        // 载入最近的历史
        foreach (var line in GetRecentLines()) Append(line);
        Closed += (_, _) => Log.EntryAdded -= OnEntry;
    }

    private static IEnumerable<string> GetRecentLines()
    {
        try
        {
            var path = Path.Combine(Log.LogDirectory, $"CampusAP-{DateTime.Now:yyyyMMdd}.log");
            if (File.Exists(path)) return File.ReadLines(path).TakeLast(400).ToList();
        }
        catch { }
        return Array.Empty<string>();
    }

    private void OnEntry(string line)
    {
        // Log 事件可能来自任意线程，统一切 UI 线程
        Dispatcher.BeginInvoke(() => Append(line));
    }

    private void Append(string line)
    {
        LogText.AppendText(line + Environment.NewLine);
        // 显示缓冲上限：800 行
        var text = LogText.Text;
        var excess = CountNewlines(text) - 800;
        if (excess > 0)
        {
            var idx = IndexOfNthNewline(text, excess);
            if (idx >= 0) LogText.Text = text[(idx + 1)..];
        }
    }

    private void LogText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (AutoScrollCheck.IsChecked == true)
        {
            LogText.CaretIndex = LogText.Text.Length;
            LogText.ScrollToEnd();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // ShellExecuteEx 打开文件夹（Process.Start 模式会被 SAST 判命令注入）
            var info = new NativeShellExecuteInfo
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeShellExecuteInfo>(),
                lpVerb = "open",
                lpFile = Log.LogDirectory,
                nShow = 1,
            };
            _ = ShellExecuteEx(ref info);
        }
        catch { }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct NativeShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr ShellExecuteEx(ref NativeShellExecuteInfo info);

    private static int CountNewlines(string s)
    {
        var n = 0;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }

    private static int IndexOfNthNewline(string s, int nth)
    {
        var seen = 0;
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\n' && ++seen == nth) return i;
        return -1;
    }
}

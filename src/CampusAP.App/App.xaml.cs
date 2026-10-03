using System.Threading;
using System.Windows;

namespace CampusAP.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>单实例信号：第二个实例 Set 它并退出；首实例后台等待并唤醒窗口</summary>
    public static EventWaitHandle? RestoreSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局异常钩子：任何崩溃先落日志（此前只能靠 Windows 事件日志 1026 反查）
        DispatcherUnhandledException += (_, e) =>
            Core.Logging.Log.Error("UI未处理异常: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Logging.Log.Error("致命异常: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Core.Logging.Log.Error("未观察任务异常: " + e.Exception);
            e.SetObserved();
        };

        Core.Logging.Log.Trim();
        Core.Logging.Log.Info($"应用启动 v{GetType().Assembly.GetName().Version}（管理员={new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}）");

        // 提权重启（--start-engine）时旧实例可能还在退出中：短暂等待接管，
        // 否则会撞上"信号已存在"而 Set+退出，表现为"以管理员重启后什么都没发生"。
        // 普通双开：立即唤醒已有实例并退出。
        var isAdminRestart = Environment.GetCommandLineArgs().Contains("--start-engine");
        for (var attempt = 0; ; attempt++)
        {
            RestoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CampusAP_RestoreSignal", out var createdNew);
            if (createdNew) break; // 成为首实例

            if (!isAdminRestart || attempt >= 25) // 等了 5 秒仍接管失败：按普通双开处理
            {
                RestoreSignal.Set();
                Shutdown();
                return;
            }
            RestoreSignal.Dispose();
            Thread.Sleep(200);
        }

        // 这里直接 Shutdown 且不创建主窗口——StartupUri 已移除，主窗口只在首实例路径手动创建，
        // 否则会出现"未显示就被关闭"的窗口，OnClosing 里的对话框设置 Owner 会崩。
        base.OnStartup(e);
        new MainWindow().Show();
    }
}

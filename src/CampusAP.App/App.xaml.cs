using System.Threading;
using System.Windows;

namespace CampusAP.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>单实例信号：第二个实例 Set 它并退出；首实例后台等待并唤醒窗口</summary>
    public static EventWaitHandle? RestoreSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        RestoreSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "CampusAP_RestoreSignal", out var createdNew);
        if (!createdNew)
        {
            // 已有实例（可能藏在托盘）：唤醒它而不是再开一个。
            // 这里直接 Shutdown 且不创建主窗口——StartupUri 已移除，主窗口只在首实例路径手动创建，
            // 否则会出现"未显示就被关闭"的窗口，OnClosing 里的对话框设置 Owner 会崩。
            RestoreSignal.Set();
            Shutdown();
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}

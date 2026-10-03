using System.ComponentModel;
using System.Drawing;
using System.Windows;
using CampusAP.App.Services;
using CampusAP.App.ViewModels;
using CampusAP.Core.Hotspot;

namespace CampusAP.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _forceClose;
    private bool _sessionEnding;
    private bool _hotspotExitConfirmed;
    private bool _confirmingHotspotExit;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);

        // WinForms NotifyIcon 最可靠；图标从 exe 自身提取（单文件发布后 Assets/app.ico 不在文件系统）。
        // 提取失败（异常安全兜底）不影响主流程。
        System.Drawing.Icon icon;
        try
        {
            icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                   ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = System.Drawing.SystemIcons.Application;
        }
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Visible = true,
            Text = "CampusAP · 校园热点助手",
            Icon = icon,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口", null, (_, _) => RestoreFromTray());
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => { _forceClose = true; Close(); });
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        _trayIcon.ContextMenuStrip = menu;
        _viewModel.FloatToggleRequested += () => Dispatcher.Invoke(ToggleFloatWindow);
        // Windows 注销/关机时不弹任何确认框，随系统直接结束
        System.Windows.Application.Current.SessionEnding += (_, _) => { _sessionEnding = true; _forceClose = true; };
        // 第二个实例启动时唤醒本实例（可能正藏在托盘）
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            while (App.RestoreSignal is System.Threading.EventWaitHandle signal && signal.WaitOne())
            {
                try { Dispatcher.Invoke(RestoreFromTray); }
                catch { break; /* 应用退出中：Dispatcher 已停，结束等待循环 */ }
            }
        });
        System.Windows.Application.Current.Exit += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        };
    }

    private FloatWindow? _floatWindow;

    private void ToggleFloatWindow()
    {
        if (_floatWindow is null)
        {
            _floatWindow = new FloatWindow { DataContext = _viewModel };
            _floatWindow.Closed += (_, _) =>
            {
                _floatWindow = null;
                _viewModel.FloatWindowOpen = false;
            };
        }
        if (_floatWindow.IsVisible)
        {
            _floatWindow.Hide();
            _viewModel.FloatWindowOpen = false;
        }
        else
        {
            _floatWindow.Show();
            _viewModel.FloatWindowOpen = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClose)
        {
            var action = _viewModel.Settings.CloseAction;
            if (action == CloseAction.Ask)
            {
                // Owner 只能在本窗口已显示时设置，否则（如关机路径的收尾关闭）会抛 InvalidOperationException
                var dialog = new CloseBehaviorDialog();
                if (IsLoaded) dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    action = dialog.Choice;
                    if (dialog.RememberChoice)
                    {
                        _viewModel.Settings.CloseAction = action;
                        _viewModel.Settings.Save();
                    }
                }
                else
                {
                    // 用户取消了对话框：什么都不做，窗口留在屏幕上
                    e.Cancel = true;
                    base.OnClosing(e);
                    return;
                }
            }

            if (action == CloseAction.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    _trayIcon.ShowBalloonTip(2000, "CampusAP",
                        "已隐藏到系统托盘，双击图标恢复窗口",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { }
                base.OnClosing(e);
                return;
            }
        }

        // 热点由系统托管：程序退出后 Windows 会继续共享网络（这次交接时"程序消失但手机仍连着"的根源）。
        // 真正退出前给用户一次选择，覆盖窗口退出/托盘退出/记住的直接退出三条路径；注销/关机时跳过。
        // 注意：这里必须取消本次关闭、把确认对话框推迟到关闭流程之外（Dispatcher.BeginInvoke）——
        // 在 OnClosing 里同步再调 Close() 属于关闭期重入，WPF 会抛
        // "在窗口关闭期间无法调用 Show/Close"（事件日志 1026 实录，曾致退出时闪退）。
        if (!_hotspotExitConfirmed && !_sessionEnding && _viewModel.State == HotspotState.On)
        {
            e.Cancel = true;
            if (!_confirmingHotspotExit)
            {
                _confirmingHotspotExit = true;
                Dispatcher.BeginInvoke(ConfirmExitWithHotspotOn);
            }
            return;
        }

        // 真正退出：清理托盘图标
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        System.Windows.Application.Current.Shutdown();
        base.OnClosing(e);
    }

    private async void ConfirmExitWithHotspotOn()
    {
        var result = System.Windows.MessageBox.Show(
            "校园网热点仍在运行。退出后系统会继续共享网络，已连接的设备仍可上网。\n\n" +
            "「是」：关闭热点并退出\n" +
            "「否」：仅退出程序，热点保持开启\n" +
            "「取消」：返回程序",
            "热点仍在运行",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Question);

        if (result == System.Windows.MessageBoxResult.Cancel)
        {
            _confirmingHotspotExit = false;
            _forceClose = false; // 撤销托盘退出等路径留下的标记，恢复常规关闭流程
            return;
        }

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try { await _viewModel.StopHotspotForExitAsync(); }
            catch { /* 关闭热点失败时按用户所选行为继续退出 */ }
        }

        _confirmingHotspotExit = false;
        _hotspotExitConfirmed = true;
        _forceClose = true;
        Close(); // 此刻已不在 OnClosing 调用栈内，Close 合法
    }
}

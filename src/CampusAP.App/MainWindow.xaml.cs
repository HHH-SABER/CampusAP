using System.ComponentModel;
using System.Windows;
using CampusAP.App.Services;
using CampusAP.App.ViewModels;

namespace CampusAP.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly H.NotifyIcon.TaskbarIcon _trayIcon;
    private bool _forceClose;
    private bool _balloonShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += (_, _) => _viewModel.InitializeCommand.Execute(null);
        _trayIcon = (H.NotifyIcon.TaskbarIcon)FindResource("TrayIcon");
        _viewModel.FloatToggleRequested += () => Dispatcher.Invoke(ToggleFloatWindow);
        Application.Current.Exit += (_, _) =>
        {
            _trayIcon.Visibility = Visibility.Collapsed;
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

    private void TrayIcon_LeftClick(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayShow_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        Close();
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
            var action = _settings.CloseAction;
            if (action == CloseAction.Ask)
            {
                var dialog = new CloseBehaviorDialog { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    action = dialog.Choice;
                    if (dialog.RememberChoice)
                    {
                        _settings.CloseAction = action;
                        _settings.Save();
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
                _trayIcon.Visibility = Visibility.Visible;
                if (!_balloonShown)
                {
                    _balloonShown = true;
                    try { _trayIcon.ShowNotification("CampusAP", "已隐藏到系统托盘，点击图标恢复窗口"); }
                    catch { /* 通知失败不影响功能 */ }
                }
                base.OnClosing(e);
                return;
            }
        }

        // 真正退出：清理托盘图标
        _trayIcon.Visibility = Visibility.Collapsed;
        _trayIcon.Dispose();
        Application.Current.Shutdown();
        base.OnClosing(e);
    }
}

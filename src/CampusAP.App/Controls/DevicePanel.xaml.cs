using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CampusAP.App.ViewModels;

namespace CampusAP.App.Controls;

public partial class DevicePanel : System.Windows.Controls.UserControl
{
    public DevicePanel()
    {
        InitializeComponent();
    }

    /// <summary>双击设备名称区域 → 弹出重命名对话框（主界面与悬浮窗共用本面板）</summary>
    private void DeviceName_LeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if ((sender as FrameworkElement)?.DataContext is DeviceViewModel device &&
            DataContext is MainViewModel main)
        {
            main.RenameDeviceCommand.Execute(device);
        }
    }
}

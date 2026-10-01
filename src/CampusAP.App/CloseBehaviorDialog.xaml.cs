using System.Windows;
using CampusAP.App.Services;

namespace CampusAP.App;

public partial class CloseBehaviorDialog : Window
{
    /// <summary>用户选择的关闭行为；ShowDialog()==true 时有效</summary>
    public CloseAction Choice { get; private set; }

    /// <summary>是否勾选了"记住我的选择"</summary>
    public bool RememberChoice => RememberCheckBox.IsChecked == true;

    public CloseBehaviorDialog()
    {
        InitializeComponent();
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseAction.MinimizeToTray;
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseAction.Exit;
        DialogResult = true;
    }
}

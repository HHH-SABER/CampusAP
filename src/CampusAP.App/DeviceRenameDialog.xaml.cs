using System.Windows;

namespace CampusAP.App;

public partial class DeviceRenameDialog : Window
{
    /// <summary>用户输入的名称（已去首尾空白；空串表示清除自定义名）</summary>
    public string DeviceName => NameBox.Text.Trim();

    public DeviceRenameDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

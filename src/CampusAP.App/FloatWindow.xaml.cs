using System.Windows;
using System.Windows.Input;

namespace CampusAP.App;

public partial class FloatWindow : Window
{
    public FloatWindow()
    {
        InitializeComponent();
        // 首次出现在屏幕右下角
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - Width - 24;
            Top = work.Bottom - ActualHeight - 24;
        };
    }

    private void Header_LeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}

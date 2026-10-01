using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampusAP.App.ViewModels;

/// <summary>设备列表中的一行（主界面与悬浮窗共用）</summary>
public partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty] private string ip = "";
    [ObservableProperty] private string mac = "";
    [ObservableProperty] private string downRate = "—";
    [ObservableProperty] private string upRate = "—";
    [ObservableProperty] private string totalText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockText))]
    private bool blocked;

    /// <summary>0 不限 / 1 20M / 2 5M / 3 1M / 4 256K（Mbps/Kbps）</summary>
    [ObservableProperty] private int limitIndex;

    /// <summary>由 MainViewModel 在创建本行时挂接（限速下拉变化）</summary>
    public Action<DeviceViewModel, int>? LimitRequested;

    /// <summary>拉黑/恢复按钮点击</summary>
    public Action<DeviceViewModel>? BlockRequested;

    public string BlockText => Blocked ? "解除拉黑" : "拉黑";

    partial void OnLimitIndexChanged(int value) => LimitRequested?.Invoke(this, value);

    [RelayCommand]
    private void Block() => BlockRequested?.Invoke(this);

    public void RequestBlock() => BlockRequested?.Invoke(this);
}

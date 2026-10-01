using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampusAP.App.ViewModels;

/// <summary>设备列表中的一行（主界面与悬浮窗共用）</summary>
public partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty] private string ip = "";
    [ObservableProperty] private string mac = "";

    /// <summary>用户自定义名（按 MAC 记住，留空=不使用）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(IpLineVisibility))]
    private string customName = "";

    /// <summary>反向解析得到的主机名（尽力而为，解析失败为空）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(IpLineVisibility))]
    private string hostName = "";

    [ObservableProperty] private string downRate = "—";
    [ObservableProperty] private string upRate = "—";
    [ObservableProperty] private string totalText = "—";

    /// <summary>列表首行显示名：自定义名 > 主机名 > IP</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomName) ? CustomName
        : !string.IsNullOrWhiteSpace(HostName) ? HostName
        : Ip;

    /// <summary>显示名已不是 IP 时，在下方补一行小字 IP（否则与首行重复）</summary>
    public Visibility IpLineVisibility => DisplayName == Ip ? Visibility.Collapsed : Visibility.Visible;

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

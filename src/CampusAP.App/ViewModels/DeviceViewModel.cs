using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CampusAP.Core.Devices;

namespace CampusAP.App.ViewModels;

/// <summary>设备列表中的一行（主界面与悬浮窗共用）</summary>
public partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty] private string ip = "";

    /// <summary>设置 MAC 时自动查厂商</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    [NotifyPropertyChangedFor(nameof(IpLineVisibility))]
    private string mac = "";

    /// <summary>OUI 厂商名（如"小米""华为""Apple"），查不到为空</summary>
    public string? Vendor => OuiLookup.GetVendor(Mac);

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

    [ObservableProperty] private string downRate = "";
    [ObservableProperty] private string upRate = "";
    [ObservableProperty] private string totalText = "";

    /// <summary>列表首行显示名：自定义名 > 厂商+型号 > IP</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomName)) return CustomName;
            if (!string.IsNullOrWhiteSpace(Vendor))
            {
                var model = ExtractModel(HostName);
                return string.IsNullOrEmpty(model) ? Vendor : $"{Vendor} {model}";
            }
            return string.IsNullOrWhiteSpace(HostName) ? Ip : HostName;
        }
    }

    /// <summary>从主机名提取型号（如 Mi-11-Ultra / Magic-7-Pro / iPhone15）</summary>
    private static string ExtractModel(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return "";
        // 去掉常见前缀词，保留型号段
        var h = host.ToLowerInvariant();
        // 匹配 Mi-XX / Redmi-XX / Magic-XX / Honor-XX / iPhoneXX / Galaxy-XX 等
        var patterns = new[]
        {
            @"mi[-_ ]?(\d{1,2}[a-z]?(?:[-_ ](?:ultra|pro|plus|lite|ne|t))?)",
            @"redmi[-_ ]?([a-z0-9]+)",
            @"magic[-_ ]?(\d{1,2}[a-z]?(?:[-_ ](?:pro|ultra|plus))?)",
            @"honor[-_ ]?([a-z0-9]+)",
            @"iphone[-_ ]?(\d{1,2}[a-z]?(?:[a-z]+)?)",
            @"galaxy[-_ ]?([a-z0-9]+)",
            @"mate[-_ ]?(\d{1,2}[a-z]?(?:[-_ ](?:pro|x|s|e))?)",
            @"p[-_ ]?(\d{1,2}[a-z]?(?:[-_ ](?:pro|plus|lite))?)",
            @"nova[-_ ]?([a-z0-9]+)",
            @"oppp?o?[-_ ]?([a-z0-9]+)",
            @"vivo[-_ ]?([a-z0-9]+)",
            @"iqoo[-_ ]?([a-z0-9]+)",
            @"oneplus[-_ ]?([a-z0-9]+)",
        };
        foreach (var p in patterns)
        {
            var m = System.Text.RegularExpressions.Regex.Match(h, p);
            if (m.Success)
            {
                // 还原大小写
                var model = m.Value.Replace('-', ' ').Replace('_', ' ');
                return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(model);
            }
        }
        return "";
    }

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

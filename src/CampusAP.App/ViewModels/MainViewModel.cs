using System.Windows.Media;
using CampusAP.App.Services;
using CampusAP.Core.Hotspot;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampusAP.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TetheringBackend _backend = new();

    [ObservableProperty] private bool capabilityOk;
    [ObservableProperty] private string ssid = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private int bandIndex; // 0 自动 / 1 2.4G / 2 5G，与 HotspotBand 枚举序号一致
    [ObservableProperty] private HotspotState state = HotspotState.Unknown;
    [ObservableProperty] private string statusText = "正在检测热点能力…";
    [ObservableProperty] private string diagnosisText = "";
    [ObservableProperty] private int clientCount;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private ImageSource? qrImage;

    public string ToggleText => State switch
    {
        HotspotState.On => "关闭热点",
        HotspotState.Starting or HotspotState.Stopping => "请稍候…",
        _ => "开启热点",
    };

    public bool CanToggle => CapabilityOk && !IsBusy
        && State is HotspotState.Off or HotspotState.On or HotspotState.Error or HotspotState.Unknown;

    public MainViewModel()
    {
        _backend.StatusChanged += s =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyStatus(s));
        };
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var report = _backend.CheckCapability();
            DiagnosisText = report.Diagnosis;
            if (!report.Supported)
            {
                State = HotspotState.Unsupported;
                StatusText = "本机暂不支持开热点";
                return;
            }

            CapabilityOk = true;
            var (ssid, password, band) = _backend.ReadCurrentConfig();
            Ssid = ssid;
            Password = password;
            BandIndex = (int)band;

            var status = _backend.PeekStatus();
            State = status.State;
            ClientCount = status.ClientCount;
            StatusText = status.State == HotspotState.On ? "热点运行中" : "热点未开启";
            UpdateQr();
            StartPolling();
        }
        catch (Exception ex)
        {
            StatusText = "能力检测失败：" + ex.Message;
            DiagnosisText = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        IsBusy = true;
        try
        {
            if (State == HotspotState.On)
            {
                await _backend.StopAsync();
            }
            else
            {
                // 开启前把界面上的配置写进去，避免"改了没生效"
                var error = ValidateConfig();
                if (error is not null) { StatusText = error; return; }
                await _backend.ApplyConfigAsync(Ssid.Trim(), Password, (HotspotBand)BandIndex);
                await _backend.StartAsync();
            }
            UpdateQr();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyConfigAsync()
    {
        var error = ValidateConfig();
        if (error is not null) { StatusText = error; return; }

        IsBusy = true;
        try
        {
            await _backend.ApplyConfigAsync(Ssid.Trim(), Password, (HotspotBand)BandIndex);
            StatusText = State == HotspotState.On
                ? "配置已写入并热更新广播"
                : "配置已写入，下次开启生效";
            UpdateQr();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string? ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(Ssid)) return "热点名称不能为空";
        if (Ssid.Trim().Length > 32) return "热点名称过长（32 字符以内）";
        if (string.IsNullOrEmpty(Password) || Password.Length < 8) return "密码至少 8 位";
        return null;
    }

    private void ApplyStatus(HotspotStatus s)
    {
        State = s.State;
        ClientCount = s.ClientCount;
        if (s.Detail is not null) StatusText = s.Detail;
        else if (s.State == HotspotState.On && StatusText != "热点运行中") StatusText = "热点运行中";
        else if (s.State == HotspotState.Off && StatusText != "热点未开启") StatusText = "热点未开启";
    }

    private void UpdateQr()
    {
        try { QrImage = QrCodeService.CreateWifiQr(Ssid.Trim(), Password); }
        catch { QrImage = null; }
    }

    /// <summary>
    /// 轮询兜底：WinRT 管理器没有状态变化事件，轮询还能感知用户在系统设置里手动开关热点。
    /// </summary>
    private void StartPolling()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        timer.Tick += (_, _) =>
        {
            if (!CapabilityOk || IsBusy) return;
            if (State is HotspotState.Starting or HotspotState.Stopping) return;
            var s = _backend.PeekStatus();
            if (s.State != State) { ApplyStatus(s); OnPropertyChanged(nameof(ToggleText)); }
            else ClientCount = s.ClientCount;
        };
        timer.Start();
    }

    partial void OnStateChanged(HotspotState value)
    {
        OnPropertyChanged(nameof(ToggleText));
        ToggleCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => ToggleCommand.NotifyCanExecuteChanged();

    partial void OnCapabilityOkChanged(bool value) => ToggleCommand.NotifyCanExecuteChanged();
}

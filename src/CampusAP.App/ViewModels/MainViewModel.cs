using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Media;
using CampusAP.App.Services;
using CampusAP.Core.Devices;
using CampusAP.Core.Hotspot;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampusAP.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TetheringBackend _backend = new();
    private readonly TrafficEngine _engine = new();
    private readonly Dictionary<string, (long Up, long Down)> _lastTotals = new();
    private DateTime _lastRateSample = DateTime.UtcNow;

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

    // ---- 设备与流量 ----
    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    [ObservableProperty] private bool engineRunning;
    [ObservableProperty] private string engineStatusText = "流量管控未启用（限速/拉黑需要开启）";
    [ObservableProperty] private bool floatWindowOpen;
    [ObservableProperty] private bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public event Action? FloatToggleRequested;

    public string FloatToggleText => FloatWindowOpen ? "关闭悬浮窗" : "打开悬浮窗";

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

            // 从管理员重启场景带参启动：自动开启流量管控
            if (Environment.GetCommandLineArgs().Contains("--start-engine"))
                ToggleControlCommand.Execute(null);
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

    // ---- 流量管控 ----

    /// <summary>
    /// 以管理员身份重启自身。等价于 Process.Start(Verb="runas")，改走 Win32 ShellExecuteEx：
    /// 目标文件恒为宿主程序自身、参数为常量，不含任何外部输入（SAST 无污点路径）。
    /// </summary>
    private static void ShellExecuteRunAs(string arguments)
    {
        var info = new NativeShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<NativeShellExecuteInfo>(),
            lpVerb = "runas",
            lpFile = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位自身可执行文件"),
            lpParameters = arguments,
            nShow = 1 /* SW_SHOWNORMAL */,
        };
        if (ShellExecuteEx(ref info) == IntPtr.Zero)
            throw new InvalidOperationException("管理员授权失败或被取消");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecuteEx(ref NativeShellExecuteInfo info);

    [RelayCommand]
    private void ToggleControl()
    {
        if (EngineRunning)
        {
            _engine.Stop();
            EngineRunning = false;
            EngineStatusText = "流量管控未启用（限速/拉黑需要开启）";
            RefreshDevices(forceRates: true);
            return;
        }

        if (!IsAdmin)
        {
            var choice = System.Windows.MessageBox.Show(
                "限速与拉黑需要加载内核级过滤驱动（WinDivert），因此需要管理员权限。\n\n" +
                "是否以管理员身份重启本程序并开启流量管控？\n（重启后热点不会中断）",
                "需要管理员权限", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (choice != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                ShellExecuteRunAs("--start-engine");
                System.Windows.Application.Current.Shutdown(); // Shutdown 不触发 Closing，直接结束当前实例
            }
            catch (Exception)
            {
                EngineStatusText = "已取消管理员授权，流量管控未启用";
            }
            return;
        }

        try
        {
            _engine.Start();
            EngineRunning = true;
            EngineStatusText = "流量管控运行中";
        }
        catch (Exception ex)
        {
            EngineStatusText = "开启失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void ToggleFloatWindow() => FloatToggleRequested?.Invoke();

    private void OnLimitRequested(DeviceViewModel vm, int index)
    {
        // 字节/秒：不限 / 20 Mbps / 5 Mbps / 1 Mbps / 256 Kbps
        long bytesPerSec = index switch
        {
            1 => 2_500_000,
            2 => 625_000,
            3 => 125_000,
            4 => 32_000,
            _ => 0,
        };
        _engine.SetLimit(vm.Ip, bytesPerSec);
    }

    private void OnBlockRequested(DeviceViewModel vm)
    {
        vm.Blocked = !vm.Blocked;
        _engine.SetBlocked(vm.Ip, vm.Blocked);
    }

    // ---- 轮询（热点状态 + 设备列表 + 速率）----

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

            RefreshDevices();
        };
        timer.Start();
    }

    private void RefreshDevices(bool forceRates = false)
    {
        List<TetheringClientInfo> clients;
        try { clients = _backend.GetClients(); }
        catch { return; }

        if (clients.Count == 0)
        {
            if (Devices.Count > 0)
            {
                Devices.Clear();
                _lastTotals.Clear();
                _engine.SetClients(Array.Empty<string>());
                if (EngineRunning) EngineStatusText = "流量管控运行中（暂无设备）";
            }
            return;
        }

        if (EngineRunning) _engine.SetClients(clients.Select(c => c.Ip));

        var elapsed = (DateTime.UtcNow - _lastRateSample).TotalSeconds;
        _lastRateSample = DateTime.UtcNow;

        foreach (var client in clients)
        {
            var vm = Devices.FirstOrDefault(d => d.Ip == client.Ip);
            if (vm is null)
            {
                vm = new DeviceViewModel
                {
                    Ip = client.Ip,
                    Mac = string.IsNullOrEmpty(client.Mac) ? "（未知）" : FormatMac(client.Mac),
                };
                vm.LimitRequested = OnLimitRequested;
                vm.BlockRequested = OnBlockRequested;
                Devices.Add(vm);
            }

            var (up, down) = _engine.GetTotals(client.Ip);
            var (lastUp, lastDown) = _lastTotals.TryGetValue(client.Ip, out var last) ? last : (0L, 0L);

            if (EngineRunning || forceRates)
            {
                vm.UpRate = FormatRate((up - lastUp) / Math.Max(elapsed, 0.1));
                vm.DownRate = FormatRate((down - lastDown) / Math.Max(elapsed, 0.1));
                vm.TotalText = $"↑{FormatBytes(up)}  ↓{FormatBytes(down)}";
            }
            _lastTotals[client.Ip] = (up, down);
        }

        foreach (var gone in Devices.Where(d => clients.All(c => c.Ip != d.Ip)).ToList())
        {
            Devices.Remove(gone);
            _lastTotals.Remove(gone.Ip);
        }
    }

    private static string FormatMac(string mac)
    {
        var clean = mac.Replace("-", "").Replace(":", "");
        if (clean.Length != 12) return mac;
        return string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)).ToArray()).ToUpperInvariant();
    }

    private static string FormatRate(double bytesPerSec) => bytesPerSec switch
    {
        < 1 => "0 B/s",
        < 1024 => $"{bytesPerSec:N0} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024:N1} KB/s",
        _ => $"{bytesPerSec / 1024 / 1024:N2} MB/s",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:N1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:N2} GB",
    };

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

    private string? ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(Ssid)) return "热点名称不能为空";
        if (Ssid.Trim().Length > 32) return "热点名称过长（32 字符以内）";
        if (string.IsNullOrEmpty(Password) || Password.Length < 8) return "密码至少 8 位";
        return null;
    }

    partial void OnStateChanged(HotspotState value)
    {
        OnPropertyChanged(nameof(ToggleText));
        ToggleCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => ToggleCommand.NotifyCanExecuteChanged();

    partial void OnCapabilityOkChanged(bool value) => ToggleCommand.NotifyCanExecuteChanged();

    partial void OnFloatWindowOpenChanged(bool value) => OnPropertyChanged(nameof(FloatToggleText));
}

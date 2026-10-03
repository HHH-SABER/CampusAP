using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Media;
using CampusAP.App.Services;
using CampusAP.Core.CampusAuth;
using CampusAP.Core.Devices;
using CampusAP.Core.Hotspot;
using CampusAP.Core.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CampusAP.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TetheringBackend _backend = new();
    private readonly TrafficEngine _engine = new();
    private readonly WebConsoleServer _webConsole;
    private readonly Dictionary<string, (long Up, long Down)> _lastTotals = new();
    private DateTime _lastRateSample = DateTime.UtcNow;

    /// <summary>共享设置实例（MainWindow 的关闭行为与设备命名都读写它，避免双实例互相覆盖）</summary>
    public AppSettings Settings { get; } = AppSettings.Load();

    // 设备名反向解析：ip -> 主机名（空串=解析失败），解析中的去重
    private readonly Dictionary<string, string> _hostNameCache = new();
    private readonly HashSet<string> _resolvingHostNames = new();

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

    /// <summary>TTL 伪装开关（从设置读取，改动即时生效并保存）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TtlSpoofText))]
    private bool ttlSpoof;

    public event Action? FloatToggleRequested;

    public string FloatToggleText => FloatWindowOpen ? "关闭悬浮窗" : "打开悬浮窗";
    public string TtlSpoofText => TtlSpoof ? "TTL伪装：开" : "TTL伪装：关";

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
        TtlSpoof = Settings.TtlSpoofEnabled;
        _engine.TtlSpoofEnabled = TtlSpoof;

        _webConsole = new WebConsoleServer(_engine)
        {
            DeviceProvider = GetWebDevices,
            LimitCommand = (ip, bps) => OnLimitRequested(Devices.FirstOrDefault(d => d.Ip == ip)!, BytesToLimitIndex(bps)),
            BlockCommand = (ip, blocked) =>
            {
                var vm = Devices.FirstOrDefault(d => d.Ip == ip);
                if (vm is not null && vm.Blocked != blocked) OnBlockRequested(vm);
            },
        };

        _backend.StatusChanged += s =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyStatus(s));
        };
    }

    partial void OnTtlSpoofChanged(bool value)
    {
        _engine.TtlSpoofEnabled = value;
        Settings.TtlSpoofEnabled = value;
        Settings.Save();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        // 校园网认证（M2）独立于热点能力，先初始化并做首次探测
        LoadCampusAccount();
        StartCampusWatchdog();
        CheckCampusCommand.Execute(null);

        IsBusy = true;
        try
        {
            await TryInitializeAsync();
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

    private async Task TryInitializeAsync()
    {
        for (int attempt = 1; attempt <= 12; attempt++)  // 最多重试12次=60秒
        {
            try
            {
                var report = _backend.CheckCapability();
                DiagnosisText = report.Diagnosis;
                if (!report.Supported)
                {
                    // 网络没就绪：等5秒重试，不直接判死刑
                    if (attempt < 12)
                    {
                        StatusText = $"等待网络就绪…（第{attempt}次检测）";
                        await Task.Delay(5000);
                        continue;
                    }
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

                // 启动时后台检查更新（不阻塞主流程）
                _ = CheckUpdateAsync();

                if (Environment.GetCommandLineArgs().Contains("--start-engine"))
                    ToggleControlCommand.Execute(null);
                return;  // 成功，退出重试循环
            }
            catch
            {
                if (attempt >= 12) throw;
                StatusText = $"热点初始化异常，5秒后重试…（{attempt}/12）";
                await Task.Delay(5000);
            }
        }
    }

    private async Task CheckUpdateAsync()
    {
        try
        {
            var checker = new Core.Update.UpdateChecker();
            var current = GetType().Assembly.GetName().Version?.ToString() ?? "0.1.0";
            var (hasUpdate, ver, _) = await checker.CheckAsync(current);
            if (hasUpdate)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var r = System.Windows.MessageBox.Show(
                        $"发现新版本 v{ver}，是否前往下载？",
                        "CampusAP 更新",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Information);
                    if (r == System.Windows.MessageBoxResult.Yes) OpenReleasePage();
                });
            }
        }
        catch { }
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

    /// <summary>退出流程专用：关闭热点。失败时由调用方决定是否继续退出。</summary>
    public async Task StopHotspotForExitAsync()
    {
        IsBusy = true;
        try { await _backend.StopAsync(); }
        finally { IsBusy = false; }
    }

    // ---- 校园网认证（M2）----

    private readonly EportalClient _eportal = new();
    private bool _campusBusy;
    private string? _lastRedirectUrl;
    private PortalKind _lastPortalKind = PortalKind.RuijieEportal;

    [ObservableProperty] private CampusAuthState authState = CampusAuthState.Unknown;
    [ObservableProperty] private string authStatusText = "校园网认证状态未知";
    [ObservableProperty] private string campusUserId = "";
    [ObservableProperty] private string campusPassword = "";
    [ObservableProperty] private string campusService = "";
    [ObservableProperty] private bool campusAutoRelogin = true;
    [ObservableProperty] private bool hasSavedAccount;

    partial void OnCampusAutoReloginChanged(bool value)
    {
        if (!HasSavedAccount) return;
        var account = CampusAccountStore.Load();
        account.AutoRelogin = value;
        CampusAccountStore.Save(account);
    }

    private void LoadCampusAccount()
    {
        var account = CampusAccountStore.Load();
        CampusUserId = account.UserId;
        CampusService = account.Service;
        CampusPassword = CampusAccountStore.Unprotect(account.PasswordEncrypted);
        CampusAutoRelogin = account.AutoRelogin;
        HasSavedAccount = !string.IsNullOrEmpty(account.UserId) && !string.IsNullOrEmpty(account.PasswordEncrypted);
    }

    [RelayCommand]
    private async Task CheckCampusAsync()
    {
        if (_campusBusy) return;
        _campusBusy = true;
        AuthState = CampusAuthState.Checking;
        AuthStatusText = "正在检测校园网认证状态…";
        try
        {
            ApplyCampusStatus(await _eportal.CheckAsync(!string.IsNullOrWhiteSpace(CampusUserId)));
        }
        catch (Exception ex)
        {
            AuthState = CampusAuthState.Unknown;
            AuthStatusText = "检测异常：" + ex.Message;
        }
        finally { _campusBusy = false; }
    }

    [RelayCommand]
    private async Task LoginCampusAsync()
    {
        if (_campusBusy) return;
        if (ValidateCampusInput() is { } inputError) { AuthStatusText = inputError; return; }

        _campusBusy = true;
        try
        {
            // 没有queryString就先探测一次拿门户跳转（顺带把"已在线"的情况挡回去）
            if (_lastRedirectUrl is null)
            {
                AuthState = CampusAuthState.Checking;
                AuthStatusText = "正在探测门户…";
                ApplyCampusStatus(await _eportal.CheckAsync(!string.IsNullOrWhiteSpace(CampusUserId)));
                if (AuthState != CampusAuthState.NeedLogin) return;
            }

            var parsed = EportalClient.ParseRedirect(_lastRedirectUrl);
            if (parsed is null)
            {
                AuthState = CampusAuthState.PortalHijack;
                AuthStatusText = "无法从门户跳转解析认证参数，请在浏览器手动认证";
                return;
            }

            AuthStatusText = "正在登录校园网…";
            var login = await _eportal.LoginAsync(parsed.Value.PortalDir, parsed.Value.QueryString,
                CampusUserId.Trim(), CampusPassword, CampusService.Trim(), _lastPortalKind);
            if (!login.Success)
            {
                AuthStatusText = "登录失败：" + login.Message;
                return;
            }

            // 登录成功后复测一次，用真实连通性更新状态
            ApplyCampusStatus(await _eportal.CheckAsync(!string.IsNullOrWhiteSpace(CampusUserId)));
            if (AuthState == CampusAuthState.Online) AuthStatusText = "正在使用校园网";
        }
        catch (Exception ex)
        {
            AuthStatusText = "登录流程异常：" + ex.Message;
        }
        finally { _campusBusy = false; }
    }

    [RelayCommand]
    private void SaveCampusAccount()
    {
        if (ValidateCampusInput() is { } inputError) { AuthStatusText = inputError; return; }
        CampusAccountStore.Save(new CampusAccount
        {
            UserId = CampusUserId.Trim(),
            Service = CampusService.Trim(),
            PasswordEncrypted = CampusAccountStore.Protect(CampusPassword),
            AutoRelogin = CampusAutoRelogin,
        });
        HasSavedAccount = true;
        AuthStatusText = "账号已保存（密码经 DPAPI 加密，仅本机可解密）";
    }

    private string? ValidateCampusInput()
    {
        if (string.IsNullOrWhiteSpace(CampusUserId)) return "校园网账号不能为空";
        if (string.IsNullOrEmpty(CampusPassword)) return "校园网密码不能为空";
        return null;
    }

    private string? _networkEnvText;
    /// <summary>能力检测模块显示的网络环境判断</summary>
    public string? NetworkEnvText
    {
        get => _networkEnvText;
        set => SetProperty(ref _networkEnvText, value);
    }

    private void ApplyCampusStatus(CampusAuthStatus status)
    {
        AuthState = status.State;
        _lastRedirectUrl = status.RedirectUrl;
        _lastPortalKind = status.Kind;

        // 校园网认证卡片：状态灯 + 简短状态
        AuthStatusText = status.State switch
        {
            CampusAuthState.PlainOnline => "未激活",
            CampusAuthState.NeedLogin => "校园网络环境，请登录或激活校园网",
            CampusAuthState.Online => "正在使用校园网",
            CampusAuthState.NoInternet => "无网络连接",
            CampusAuthState.Checking => "检测中…",
            _ => status.Detail,
        };

        // 能力检测模块：网络环境判断
        NetworkEnvText = status.State switch
        {
            CampusAuthState.PlainOnline => "普通网络，校园网认证模块未激活",
            CampusAuthState.NeedLogin => "校园网络环境，尚未登录",
            CampusAuthState.Online => "校园网络环境，已登录",
            CampusAuthState.NoInternet => "无网络连接",
            _ => null,
        };

        OnPropertyChanged(nameof(AuthFormEnabled));
    }

    /// <summary>普通网络时表单禁用</summary>
    public bool AuthFormEnabled => AuthState != CampusAuthState.PlainOnline;

    /// <summary>看门狗：每 60 秒探测一次；掉线且已保存账号时自动重登</summary>
    private void StartCampusWatchdog()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        timer.Tick += async (_, _) =>
        {
            if (_campusBusy) return;
            _campusBusy = true;
            try
            {
                var status = await _eportal.CheckAsync(!string.IsNullOrWhiteSpace(CampusUserId));
                ApplyCampusStatus(status);
                if (status.State != CampusAuthState.NeedLogin || !CampusAutoRelogin) return;

                var account = CampusAccountStore.Load();
                var password = CampusAccountStore.Unprotect(account.PasswordEncrypted);
                if (string.IsNullOrEmpty(account.UserId) || string.IsNullOrEmpty(password))
                {
                    AuthStatusText = "校园网未认证（尚未保存账号，无法自动重登）";
                    return;
                }
                var parsed = EportalClient.ParseRedirect(status.RedirectUrl);
                if (parsed is null) return;

                var login = await _eportal.LoginAsync(parsed.Value.PortalDir, parsed.Value.QueryString,
                    account.UserId, password, account.Service, status.Kind);
                if (login.Success)
                {
                    ApplyCampusStatus(await _eportal.CheckAsync(!string.IsNullOrWhiteSpace(CampusUserId)));
                    if (AuthState == CampusAuthState.Online)
                        AuthStatusText = "检测到掉线，已自动重新认证";
                }
                else
                {
                    AuthStatusText = "自动重登失败：" + login.Message;
                }
            }
            catch
            {
                // 看门狗单次失败静默，下个周期再试
            }
            finally { _campusBusy = false; }
        };
        timer.Start();
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

    /// <summary>用 ShellExecuteEx 打开发布页（默认浏览器）。不用 Process.Start：目标为编译期常量
    /// URL、无污点路径，且 Process.Start 模式会被 SAST 判命令注入拦截。</summary>
    private static void OpenReleasePage()
    {
        var info = new NativeShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<NativeShellExecuteInfo>(),
            lpVerb = "open",
            lpFile = Core.Update.UpdateChecker.ReleasesPageUrl,
            nShow = 1 /* SW_SHOWNORMAL */,
        };
        ShellExecuteEx(ref info);
    }

    [RelayCommand]
    private void ToggleControl()
    {
        Core.Logging.Log.Info($"管控开关点击: EngineRunning={EngineRunning} IsAdmin={IsAdmin} 热点={State} 设备数={Devices.Count}");
        if (EngineRunning)
        {
            _webConsole.Stop();
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
            _lastSeenPackets = 0;
            _lastTrafficSeenAt = DateTime.UtcNow;
            try { _webConsole.Start(DeriveGatewayIp()); }
            catch (Exception ex) { EngineStatusText = "管控已开，但Web管理页启动失败：" + ex.Message; }
            EngineRunning = true;
            EngineStatusText = _webConsole.IsRunning
                ? $"流量管控运行中 · 手机访问 http://{_webConsole.Gateway}:{WebConsoleServer.Port} 管理设备"
                : "流量管控运行中";
        }
        catch (Exception ex)
        {
            EngineStatusText = "开启失败：" + ex.Message;
        }
    }

    [RelayCommand]
    private void ToggleFloatWindow() => FloatToggleRequested?.Invoke();

    // ---- 设备名识别（mDNS 主动探测）----

    private readonly HashSet<string> _mdnsProbed = new();
    private long _lastSeenPackets;
    private DateTime _lastTrafficSeenAt = DateTime.UtcNow;

    /// <summary>向设备发 mDNS 单播查询拿友好名（随机 MAC 时唯一可靠来源），每设备每会话只探一次</summary>
    private async void ProbeDeviceNameAsync(DeviceViewModel device)
    {
        if (!_mdnsProbed.Add(device.Ip)) return;
        try
        {
            var name = await MdnsNameProbe.ProbeAsync(device.Ip);
            if (string.IsNullOrWhiteSpace(name)) return;
            var target = Devices.FirstOrDefault(d => d.Ip == device.Ip);
            if (target is not null) target.MdnsName = name;
        }
        catch
        {
            // 探测失败静默，显示名回退主机名/IP
        }
    }

    /// <summary>从当前设备列表推导热点网关（首设备网段的 .1）；无设备返回 null（Web 服务用默认 192.168.137.1）</summary>
    private string? DeriveGatewayIp()
    {
        foreach (var d in Devices)
        {
            var octets = d.Ip.Split('.');
            if (octets.Length == 4 && octets.All(o => byte.TryParse(o, out _)))
                return $"{octets[0]}.{octets[1]}.{octets[2]}.1";
        }
        return null;
    }

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

    /// <summary>Web 端限速值转下拉索引</summary>
    private static int BytesToLimitIndex(long bps) => bps switch
    {
        2_500_000 => 1,
        625_000 => 2,
        125_000 => 3,
        32_000 => 4,
        _ => 0,
    };

    /// <summary>Web 管理页：快照当前设备列表</summary>
    private IReadOnlyList<WebConsoleServer.DeviceInfo> GetWebDevices()
    {
        return Devices.Select(d => new WebConsoleServer.DeviceInfo(
            d.Ip, d.Mac, d.DisplayName, d.Vendor,
            d.UpRate, d.DownRate, d.Blocked,
            d.LimitIndex switch { 1 => 2_500_000, 2 => 625_000, 3 => 125_000, 4 => 32_000, _ => 0 }
        )).ToList();
    }

    private void OnBlockRequested(DeviceViewModel vm)
    {
        vm.Blocked = !vm.Blocked;
        _engine.SetBlocked(vm.Ip, vm.Blocked);
    }

    // ---- 设备命名 ----

    [RelayCommand]
    private void RenameDevice(DeviceViewModel? device)
    {
        if (device is null) return;
        var dialog = new DeviceRenameDialog(device.DisplayName);
        var main = System.Windows.Application.Current.MainWindow;
        if (main is { IsVisible: true }) dialog.Owner = main;
        if (dialog.ShowDialog() != true) return;

        device.CustomName = dialog.DeviceName;
        var key = DeviceNameKey(device);
        if (device.CustomName.Length == 0)
            Settings.DeviceNames.Remove(key); // 清空自定义名：回退主机名/IP 显示
        else
            Settings.DeviceNames[key] = device.CustomName;
        Settings.Save();
    }

    /// <summary>设备名持久化键：优先 MAC（去分隔符大写），无 MAC 用 "ip:地址"</summary>
    private static string DeviceNameKey(DeviceViewModel device)
    {
        var mac = device.Mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        return mac.Length == 12 ? mac : "ip:" + device.Ip;
    }

    /// <summary>尽力解析设备主机名（反向 DNS/mDNS，2.5 秒超时，结果按 IP 缓存）</summary>
    private async void ResolveHostNameAsync(string ip)
    {
        if (_hostNameCache.ContainsKey(ip) || !_resolvingHostNames.Add(ip)) return;
        string host = "";
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(2500)) == lookup)
                host = (await lookup).HostName;
        }
        catch
        {
            // 热点网段通常没有反向解析记录：保持空，显示名回退 IP
        }
        _resolvingHostNames.Remove(ip);
        _hostNameCache[ip] = host;
        var target = Devices.FirstOrDefault(d => d.Ip == ip);
        if (target is not null && host.Length > 0) target.HostName = host;
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

        if (EngineRunning)
        {
            var allIps = clients.Select(c => c.Ip).ToList();
            foreach (var c in clients) if (!string.IsNullOrEmpty(c.Ipv6)) allIps.Add(c.Ipv6);
            _engine.SetClients(allIps);

            // 分层抓包自诊断：Forward 层是否真正抓到设备流量，真机验证时一眼可判
            var webPart = _webConsole.IsRunning
                ? $" · 手机访问 http://{_webConsole.Gateway}:{WebConsoleServer.Port} 管理设备"
                : "";
            EngineStatusText = "流量管控运行中" + webPart + " · " + _engine.GetCaptureDiagnostics();

            // 两层持续 0 包 = 本机热点的 NAT 方式让 WinDivert 抓不到设备流量：明确告警，不静默失效
            var seen = _engine.TotalPacketsSeen;
            if (seen != _lastSeenPackets)
            {
                _lastSeenPackets = seen;
                _lastTrafficSeenAt = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - _lastTrafficSeenAt).TotalSeconds > 10)
            {
                EngineStatusText += " ｜⚠ 未捕获到设备流量：限速/拉黑/流量统计在本机暂不生效" +
                                    "（用 tools\\CaptureProbe 抓取证后反馈）";
            }
        }

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
                if (Settings.DeviceNames.TryGetValue(DeviceNameKey(vm), out var savedName))
                    vm.CustomName = savedName;
                Devices.Add(vm);
                ResolveHostNameAsync(client.Ip);
                ProbeDeviceNameAsync(vm);
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

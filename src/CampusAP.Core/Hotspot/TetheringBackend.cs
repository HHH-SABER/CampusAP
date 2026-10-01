using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace CampusAP.Core.Hotspot;

/// <summary>
/// 基于 Win10/11 系统移动热点（NetworkOperatorTetheringManager）的后端实现。
/// 免驱动、NAT 由系统完成；这是竞品（360/WiFi共享大师）用自研 NAT 驱动绕开系统 ICS 之后的现代替代路线。
/// </summary>
public sealed class TetheringBackend : IDisposable
{
    private NetworkOperatorTetheringManager? _manager;

    /// <summary>状态变化推送（可能来自非 UI 线程，订阅方自行调度）</summary>
    public event Action<HotspotStatus>? StatusChanged;

    /// <summary>
    /// 检测本机能否开热点，并带回当前配置。同步 API，不抛异常，结果都在报告里。
    /// </summary>
    public CapabilityReport CheckCapability()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile is null)
        {
            return new CapabilityReport(false,
                "当前没有任何已联网的连接。请先连上校园网（有线或无线），再打开本程序。",
                null, null, null, null);
        }

        var profileName = profile.ProfileName ?? "(未命名网络)";
        var connectivity = profile.GetNetworkConnectivityLevel();
        if (connectivity != NetworkConnectivityLevel.InternetAccess)
        {
            return new CapabilityReport(false,
                $"网络「{profileName}」未连上互联网（连接级别：{connectivity}）。先完成校园网认证再试。",
                profileName, null, null, null);
        }

        var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
        if (capability != TetheringCapability.Enabled)
        {
            return new CapabilityReport(false, ExplainCapability(capability),
                profileName, capability.ToString(), null, null);
        }

        try
        {
            _manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        }
        catch (Exception ex)
        {
            return new CapabilityReport(false,
                $"无法为网络「{profileName}」创建热点管理器：{ex.Message}",
                profileName, null, null, null);
        }

        var config = _manager.GetCurrentAccessPointConfiguration();
        return new CapabilityReport(true,
            $"热点可用。出口网络：「{profileName}」。网卡最大客户端数：{_manager.MaxClientCount}。",
            profileName, capability.ToString(), config.Ssid, config.Passphrase);
    }

    /// <summary>打开热点</summary>
    public async Task<HotspotStatus> StartAsync()
    {
        var manager = RequireManager();
        Notify(HotspotState.Starting, (int)manager.ClientCount);
        var result = await manager.StartTetheringAsync();
        if (result.Status != TetheringOperationStatus.Success)
        {
            var detail = $"启动失败：{result.Status}。{result.AdditionalErrorMessage}".Trim();
            Notify(HotspotState.Error, (int)manager.ClientCount, detail);
            throw new InvalidOperationException(detail);
        }
        return Notify(HotspotState.On, (int)manager.ClientCount, "热点已开启");
    }

    /// <summary>关闭热点</summary>
    public async Task<HotspotStatus> StopAsync()
    {
        var manager = RequireManager();
        Notify(HotspotState.Stopping, (int)manager.ClientCount);
        var result = await manager.StopTetheringAsync();
        if (result.Status != TetheringOperationStatus.Success)
        {
            var detail = $"关闭失败：{result.Status}。{result.AdditionalErrorMessage}".Trim();
            Notify(HotspotState.Error, (int)manager.ClientCount, detail);
            throw new InvalidOperationException(detail);
        }
        return Notify(HotspotState.Off, (int)manager.ClientCount, "热点已关闭");
    }

    /// <summary>写入热点名称/密码/频段（已开启时系统会热更新广播）</summary>
    public async Task ApplyConfigAsync(string ssid, string password, HotspotBand band)
    {
        var manager = RequireManager();
        var config = manager.GetCurrentAccessPointConfiguration();
        config.Ssid = ssid;
        config.Passphrase = password;
        config.Band = band switch
        {
            HotspotBand.Band24GHz => TetheringWiFiBand.TwoPointFourGigahertz,
            HotspotBand.Band5GHz => TetheringWiFiBand.FiveGigahertz,
            _ => TetheringWiFiBand.Auto,
        };
        await manager.ConfigureAccessPointAsync(config);
    }

    /// <summary>读取当前生效的配置（SSID/密码/频段）</summary>
    public (string Ssid, string Password, HotspotBand Band) ReadCurrentConfig()
    {
        var config = RequireManager().GetCurrentAccessPointConfiguration();
        return (config.Ssid, config.Passphrase ?? "", ToHotspotBand(config.Band));
    }

    /// <summary>无副作用地取当前状态（供 UI 轮询；也能感知用户在系统设置里手动开关）</summary>
    public HotspotStatus PeekStatus()
    {
        if (_manager is null) return new HotspotStatus(HotspotState.Unknown, 0, "未初始化");
        return new HotspotStatus(ToHotspotState(_manager.TetheringOperationalState), (int)_manager.ClientCount);
    }

    public void Dispose() => _manager = null;

    // ---- 内部 ----

    private NetworkOperatorTetheringManager RequireManager()
        => _manager ?? throw new InvalidOperationException(
            "热点管理器未初始化：请先连上网络，再重启本程序完成能力检测。");

    private HotspotStatus Notify(HotspotState state, int clients, string? detail = null)
    {
        var status = new HotspotStatus(state, clients, detail);
        StatusChanged?.Invoke(status);
        return status;
    }

    private static HotspotState ToHotspotState(TetheringOperationalState s) => s switch
    {
        TetheringOperationalState.On => HotspotState.On,
        TetheringOperationalState.Off => HotspotState.Off,
        TetheringOperationalState.InTransition => HotspotState.Starting,
        _ => HotspotState.Unknown,
    };

    private static HotspotBand ToHotspotBand(TetheringWiFiBand band) => band switch
    {
        TetheringWiFiBand.TwoPointFourGigahertz => HotspotBand.Band24GHz,
        TetheringWiFiBand.FiveGigahertz => HotspotBand.Band5GHz,
        _ => HotspotBand.Auto,
    };

    private static string ExplainCapability(TetheringCapability cap) => cap switch
    {
        TetheringCapability.DisabledByHardwareLimitation =>
            "无线网卡/硬件不支持开热点（台式机 USB 网卡和老网卡常见）。换一块支持 WiFi Direct 的网卡才行。",
        TetheringCapability.DisabledByGroupPolicy =>
            "组策略禁用了移动热点（常见于单位/机房电脑）。",
        TetheringCapability.DisabledByOperator or TetheringCapability.DisabledBySku =>
            "网络运营商或当前账户类型限制了热点功能。",
        TetheringCapability.DisabledBySystemCapability =>
            "系统能力声明缺失，热点被系统禁用。",
        TetheringCapability.DisabledByRequiredAppNotInstalled =>
            "缺少运营商要求的配套应用，热点被禁用。",
        TetheringCapability.DisabledDueToUnknownCause =>
            "热点被禁用，原因未知（可尝试更新网卡驱动）。",
        _ => $"热点能力受限：{cap}。",
    };
}

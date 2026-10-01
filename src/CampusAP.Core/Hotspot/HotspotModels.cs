namespace CampusAP.Core.Hotspot;

/// <summary>热点整体状态（后端无关，UI 只看这个）</summary>
public enum HotspotState
{
    Unknown,
    Unsupported,
    Off,
    Starting,
    On,
    Stopping,
    Error,
}

/// <summary>热点能力诊断报告：不支持时给用户明确的"为什么"</summary>
public record CapabilityReport(
    bool Supported,
    string Diagnosis,
    string? InternetProfileName,
    string? CapabilityDetail,
    string? CurrentSsid,
    string? CurrentPassword);

public record HotspotStatus(
    HotspotState State,
    int ClientCount,
    string? Detail = null);

public enum HotspotBand
{
    Auto,
    Band24GHz,
    Band5GHz,
}

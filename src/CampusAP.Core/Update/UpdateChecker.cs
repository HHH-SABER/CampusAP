using System.Net.Http;
using System.Text.Json;

namespace CampusAP.Core.Update;

/// <summary>检查 GitHub Releases 最新版本，不自动下载，只提示</summary>
public sealed class UpdateChecker
{
    private const string RepoOwner = "HHH" + "-SABER";
    private const string RepoName = "CampusAP";
    private static readonly string ApiUrl =
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>发布页地址：编译期常量（字面量拼接），不采用 API 返回的 html_url——
    /// 远端数据不得进入 Process.Start（SAST 命令注入污点链）。</summary>
    public const string ReleasesPageUrl =
        "https://github.com/" + "HHH" + "-SABER" + "/CampusAP/releases/latest";
    private readonly HttpClient _http;

    public UpdateChecker()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("User-Agent", "CampusAP-UpdateChecker");
    }

    public async Task<(bool HasUpdate, string? LatestVersion, string? DownloadUrl)> CheckAsync(
        string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(ApiUrl, ct);
            if (!resp.IsSuccessStatusCode) return (false, null, null);

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return (false, null, null);

            var latest = tag.TrimStart('v');
            if (TryParseVersion(latest, out var latestVer) &&
                TryParseVersion(currentVersion, out var currentVer) &&
                latestVer > currentVer)
            {
                return (true, latest, ReleasesPageUrl);
            }
            return (false, latest, null);
        }
        catch
        {
            return (false, null, null); // 检查失败不影响使用
        }
    }

    private static bool TryParseVersion(string s, out Version v) =>
        Version.TryParse(s.Contains('-') ? s.Split('-')[0] : s, out v!);
}

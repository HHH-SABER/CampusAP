using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CampusAP.App.Services;

/// <summary>校园网认证账号；密码经 DPAPI（CurrentUser）加密，只有本机当前用户能解密</summary>
public sealed class CampusAccount
{
    public string UserId { get; set; } = "";
    public string Service { get; set; } = "";
    public string PasswordEncrypted { get; set; } = "";
    public bool AutoRelogin { get; set; } = true;
}

/// <summary>存 %APPDATA%\CampusAP\campus.json；真实 Portal 地址不落盘（由网关重定向动态获取）</summary>
public static class CampusAccountStore
{
    private static string DirPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAP");

    private static string FilePath => Path.Combine(DirPath, "campus.json");

    public static CampusAccount Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<CampusAccount>(File.ReadAllText(FilePath)) ?? new CampusAccount();
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new CampusAccount();
    }

    public static void Save(CampusAccount account)
    {
        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(account, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string cipher)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return ""; // 密文损坏或换了系统用户：视为无密码
        }
    }
}

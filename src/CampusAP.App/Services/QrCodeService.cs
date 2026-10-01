using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace CampusAP.App.Services;

/// <summary>生成 WiFi 入网二维码（WIFI: 标准格式，手机相机扫码即可连接）</summary>
public static class QrCodeService
{
    public static BitmapImage? CreateWifiQr(string ssid, string password)
    {
        if (string.IsNullOrWhiteSpace(ssid)) return null;

        var payload = $"WIFI:T:WPA;S:{Escape(ssid)};P:{Escape(password)};;";
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);

        var image = new BitmapImage();
        using var ms = new MemoryStream(png);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze(); // 冻结后可跨线程使用
        return image;
    }

    /// <summary>WIFI: URI 转义：\ ; , : " 前加反斜杠</summary>
    private static string Escape(string s) => s
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace(":", "\\:")
        .Replace("\"", "\\\"");
}

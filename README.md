# CampusAP · 校园热点助手

一款运行在 Windows 10/11 上的校园网工具：把电脑的网络一键共享为 WiFi 热点，后续版本将加入校园网 Web 认证助手与设备管理。

> 灵感与竞品分析来自对 360 免费WiFi、WiFi共享大师等经典"WiFi 共享"软件的逆向学习（见项目说明），
> 但采用 **Win10/11 现代移动热点 API**（`NetworkOperatorTetheringManager`），免驱动、无需系统 ICS 调参。

## 功能（当前版本 M1）

- **WiFi 热点共享**：一键开启/关闭热点，免驱动，NAT 由系统完成
- **扫码入网**：自动生成 `WIFI:` 标准二维码，手机相机扫码即连
- **热点配置**：SSID / WPA2 密码 / 频段（自动 / 2.4G / 5G），开启状态下热更新广播
- **能力检测与诊断**：网卡不支持、组策略禁用、运营商限制等 8 种不可用原因均给出明确说明，不做黑盒失败
- **系统托盘**：关闭窗口可选择隐藏到托盘继续运行（可勾选"记住我的选择"），托盘左键恢复窗口、右键退出
- **实时状态**：已连接设备数轮询刷新（也能感知在系统设置里手动开关热点）

## 路线图

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 热点共享 + 托盘 + 关闭行为选项 | ✅ 完成 |
| M2 | 锐捷 ePortal Web 认证直发 POST + WebView2 通用兜底 + 掉线看门狗 | 🚧 进行中 |
| M3 | 设备列表（ARP + mDNS + OUI 厂商库）+ 流量曲线 | ⏳ |
| M4 | WinDivert 分设备限速/黑名单 + 安装包 | ⏳ |

## 环境要求

- Windows 10 2004 (build 19041) 及以上，64 位
- 无线网卡支持 WiFi Direct（程序启动时会自动检测并给出诊断）
- 无需管理员权限

## 从源码构建

```bash
dotnet build CampusAP.sln            # 需要 .NET 8 SDK
dotnet publish src/CampusAP.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o 发布
```

## 技术栈

- .NET 8 + WPF（TFM `net8.0-windows10.0.19041.0`，直接调用 WinRT 投影）
- `Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager`（热点核心）
- CommunityToolkit.Mvvm / QRCoder / H.NotifyIcon.Wpf

## 项目结构

```
src/
├── CampusAP.Core/           # 核心库（无 UI 依赖）
│   └── Hotspot/             # TetheringBackend：能力检测/启停/配置/状态轮询
└── CampusAP.App/            # WPF 界面
    ├── ViewModels/          # MVVM
    ├── Services/            # 设置持久化、二维码
    └── Assets/              # 应用图标
```

## 使用声明

本项目仅供学习与研究。请遵守所在校园网的用户协议；认证助手（M2）只自动填写用户自己的账号密码，不做验证码绕过，不包含对抗校园网共享检测的功能。请勿用于违反协议或法规的场景。

## License

MIT

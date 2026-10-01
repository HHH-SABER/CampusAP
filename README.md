# CampusAP · 校园热点助手

一款运行在 Windows 10/11 上的校园网工具：把电脑的网络一键共享为 WiFi 热点，后续版本将加入校园网 Web 认证助手与设备管理。

> 灵感与竞品分析来自对 360 免费WiFi、WiFi共享大师等经典"WiFi 共享"软件的逆向学习（见项目说明），
> 但采用 **Win10/11 现代移动热点 API**（`NetworkOperatorTetheringManager`），免驱动、无需系统 ICS 调参。

## 功能（当前版本）

- **WiFi 热点共享**：一键开启/关闭热点，免驱动，NAT 由系统完成
- **扫码入网**：自动生成 `WIFI:` 标准二维码，手机相机扫码即连
- **热点配置**：SSID / WPA2 密码 / 频段（自动 / 2.4G / 5G），开启状态下热更新广播
- **能力检测与诊断**：网卡不支持、组策略禁用、运营商限制等 8 种不可用原因均给出明确说明，不做黑盒失败
- **设备列表与实时流量**：每台设备的实时上下行速率与累计流量（数据源：系统 tethering API + WinDivert 计数）；显示名优先取自定义名，其次反向解析的主机名，双击设备名可重命名（按 MAC 记住）
- **单设备管控**：限速（令牌桶，预设 20M/5M/1M/256K）与拉黑（双向丢包），即时生效
- **悬浮窗**：可开关的置顶小窗，复用设备面板，不影响主界面
- **系统托盘**：关闭窗口可选择隐藏到托盘继续运行（可勾选"记住我的选择"），托盘左键恢复窗口、右键退出
- **校园网认证助手**：自动探测门户（由网关 302 重定向动态获取，无需配置地址）、直发 POST 登录锐捷 ePortal、每 60 秒看门狗探测掉线并自动重登；账号密码经 DPAPI 加密仅存本机
- **退出提示**：热点运行中时退出程序会提示是否一并关闭热点（系统热点在程序退出后仍会继续广播）

> 限速/拉黑基于 WinDivert（内核过滤驱动），启用时需要管理员权限——程序会引导以管理员身份重启，热点不会中断。

## 路线图

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M1 | 热点共享 + 托盘 + 关闭行为选项 | ✅ 完成 |
| M2 | 锐捷 ePortal Web 认证直发 POST + 掉线看门狗 + DPAPI 账号存储（WebView2 兜底视需求另做） | ✅ 完成 |
| M3 | 设备列表 + 每设备实时流量（系统 tethering API + WinDivert） | ✅ 完成 |
| M4 | WinDivert 分设备限速/拉黑 + 悬浮窗（安装包待做） | ✅ 完成 |

## 环境要求

- Windows 10 2004 (build 19041) 及以上，64 位
- 无线网卡支持 WiFi Direct（程序启动时会自动检测并给出诊断）
- 无需管理员权限

## 从源码构建

需要 .NET 8 SDK；发布请用根目录的 `发布.ps1`（普通构建 `dotnet build` 即可）：

```powershell
dotnet build CampusAP.sln
powershell -File 发布.ps1     # 单文件发布 + WinDivert.dll/WinDivert64.sys 就位
```

> WinDivert 的内核驱动（`WinDivert64.sys`）不能进单文件包，`发布.ps1` 会把它和
> `WinDivert.dll` 一并复制到发布目录——流量管控功能要求两者与主程序同目录。

## 技术栈

- .NET 8 + WPF（TFM `net8.0-windows10.0.19041.0`，直接调用 WinRT 投影）
- `Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager`（热点核心）
- CommunityToolkit.Mvvm / QRCoder / H.NotifyIcon.Wpf

## 项目结构

```
src/
├── CampusAP.Core/           # 核心库（无 UI 依赖）
│   ├── Hotspot/             # TetheringBackend：能力检测/启停/配置/状态轮询
│   ├── Devices/             # TrafficEngine：WinDivert 计数/限速/拉黑
│   └── CampusAuth/          # EportalClient：在线探测/门户重定向解析/登录 POST
└── CampusAP.App/            # WPF 界面
    ├── ViewModels/          # MVVM
    ├── Services/            # 设置持久化、二维码、校园网账号（DPAPI）
    └── Assets/              # 应用图标
```

## 使用声明

本项目仅供学习与研究。请遵守所在校园网的用户协议；认证助手（M2）只自动填写用户自己的账号密码，不做验证码绕过，不包含对抗校园网共享检测的功能。请勿用于违反协议或法规的场景。

## License

MIT

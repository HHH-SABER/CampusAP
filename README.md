# CampusAP · 校园热点助手

一款运行在 Windows 10/11 上的校园网工具：把电脑的网络一键共享为 WiFi 热点，
配合校园网认证助手与设备管理（实时流量 / 限速 / 拉黑 / Web 管理页）。

> 灵感与竞品分析来自对 360 免费WiFi、WiFi共享大师等经典"WiFi 共享"软件的逆向学习，
> 但采用 **Win10/11 现代移动热点 API**（`NetworkOperatorTetheringManager`）：免驱动、
> NAT 由系统完成、无需内核签名——这是经典方案（厂商 SoftAP SDK + 自研 NAT 驱动）之后的现代替代路线。

## 功能特性

**热点共享**
- 一键开启/关闭热点，免驱动；SSID / WPA2 密码 / 频段（自动 / 2.4G / 5G）配置，开启状态下热更新广播
- 扫码入网：自动生成 `WIFI:` 标准二维码，手机相机扫码即连
- 能力检测：网卡不支持、组策略禁用、运营商限制等 8 种不可用原因均给出明确诊断，不做黑盒失败

**设备管理**
- 设备列表：系统 tethering API 为准（IP + MAC），每 2 秒刷新；OUI 厂商识别、反向解析主机名、双击重命名（按 MAC 记住）
- 实时流量：WinDivert 双层抓包（Forward 层转发路径为主 + Network 层兜底），每设备上下行速率与累计流量，状态栏实时显示抓包层自诊断
- 单设备管控：令牌桶限速（预设 20M / 5M / 1M / 256K，超额丢包靠 TCP 回压）、双向拉黑，即时生效
- Web 管理页：手机浏览器访问 `http://热点网关:8899` 即可管理设备，无需装 App（一次性令牌防跨源滥用，详见使用要点）
- TTL 伪装（可开关）：客户端上行包 TTL 统一为 129，网关侧与 Windows 直发一致，抹掉多设备指纹

**校园网认证助手**
- 门户自动探测：对中性 URL 探测，由网关 302 重定向动态获取门户地址，无需配置；自动识别锐捷 ePortal / 深澜 Srun / Dr.COM
- 直发 POST 登录 + 60 秒看门狗：掉线后自动重新认证（需已保存账号）
- 账号密码经 DPAPI（CurrentUser）加密，仅存本机 `%APPDATA%\CampusAP\`

**桌面集成**
- 系统托盘：关闭窗口可选隐藏到托盘（可记住选择）；热点运行中时真正退出会三选一确认（防止"程序退出但热点仍在广播"）
- 悬浮窗：置顶可拖动小窗，复用设备面板，主窗/浮窗共享同一 ViewModel
- 单实例：重复启动唤醒已有实例（可能藏在托盘）
- 启动时检查新版本（仅提示，不自动下载；跳转地址为编译期常量）

> 限速/拉黑基于 WinDivert（内核过滤驱动），启用时需要管理员权限——程序会引导以管理员身份重启，重启不中断热点。

## 环境要求

- Windows 10 2004 (build 19041) 及以上，64 位
- 无线网卡支持 WiFi Direct（程序启动时自动检测并给出诊断）
- 普通使用无需管理员权限；开启流量管控（限速/拉黑/流量统计）时需要

## 快速开始

**方式一：安装包**（推荐普通用户）
从 GitHub Releases 下载 `CampusAP-Setup-vX.Y.Z.exe`（见 `git remote -v`），双击安装。

**方式二：绿色版**
下载发布产物三件套（`CampusAP.exe` + `WinDivert.dll` + `WinDivert64.sys`）放同一目录，运行 `CampusAP.exe`。

**方式三：从源码构建**

需要 .NET 8 SDK：

```powershell
dotnet build CampusAP.sln          # 普通构建
powershell -File 发布.ps1           # 单文件发布 + WinDivert 文件就位 + 打安装包
```

- `发布.ps1` 会：从 `Directory.Build.props` 读取版本 → `dotnet publish` 单文件 → 复制
  `WinDivert.dll`/`WinDivert64.sys` 到发布目录 → 检测到 Inno Setup 6 时自动打安装包
  （版本经 `/DMyAppVersion` 注入）。
- WinDivert 的内核驱动（`.sys`）不能进单文件包，必须与 exe 同目录，缺了会报 Win32 错误码 2。
- 发布/更新前先退出运行中的程序（单文件 exe 被锁会导致 publish 报 MSB4018）。

## 使用要点

- **托盘**：关闭主窗口选"隐藏到托盘"后，图标在系统托盘（Win11 默认收进右下角 `^` 溢出区），
  双击恢复；程序每次隐藏都会弹气泡提示这一点。
- **Web 管理页**：开启流量管控后，状态栏会显示 `http://<网关>:8899` 地址（手机连热点后访问）。
  页面自动携带每次开启随机生成的一次性令牌调用 API，防止热点内设备的恶意网页跨源调用管理接口。
- **TTL 伪装**：设备与流量卡右上角可开关；这是规避校园网共享检测的功能，请先确认你的校园网协议允许。
- **速率诊断**：状态栏"抓包层 Forward(N包)"表示转发路径正常工作；若显示"未捕获到设备流量"告警，
  说明本机热点的 NAT 方式让 WinDivert 抓不到转发流量（限速/拉黑暂不生效）——请管理员运行
  `tools/CaptureProbe/run_probe.bat` 取证，把输出贴到 issue。

## 故障排查

| 现象 | 原因与处理 |
|---|---|
| 开启管控报 Win32 错误码 2 | 发布目录缺 `WinDivert.dll` / `WinDivert64.sys`，确认三件套同目录 |
| 开启管控报错误码 5 | 未以管理员运行；程序会引导 UAC 提权重启（`--start-engine` 自动开启管控） |
| 错误码 577 / 1274 | WinDivert 驱动被安全策略/杀软阻止，将其加入白名单 |
| 热点开关灰色 | 看诊断卡：网卡不支持 / 组策略禁用 / 运营商限制等，均有说明 |
| 限速速率波动 | "丢包式"令牌桶的正常表现（靠 TCP 拥塞控制回压），非故障 |
| 手机连上但速率全 0 | 看状态栏抓包诊断；确认设备在转发流量而非仅 DHCP |

## 项目结构

```
├── src/
│   ├── CampusAP.Core/           # 核心库（无 UI 依赖）
│   │   ├── Hotspot/             #   TetheringBackend：能力检测/启停/配置/状态轮询
│   │   ├── Devices/             #   TrafficEngine（双层抓包/限速/拉黑/TTL伪装）、OuiLookup
│   │   ├── CampusAuth/          #   EportalClient：在线探测/门户识别/登录 POST
│   │   ├── Web/                 #   WebConsoleServer：Web 管理页 + API
│   │   └── Update/              #   UpdateChecker：GitHub Releases 版本检查
│   └── CampusAP.App/            # WPF 界面（MVVM）
│       ├── ViewModels/          #   主 VM（轮询/认证看门狗/管控命令/提权）、设备行 VM
│       ├── Controls/            #   设备面板（主窗与悬浮窗共用）
│       ├── Services/            #   设置持久化、二维码、校园网账号（DPAPI）
│       └── Assets/              #   应用图标
├── installer/CampusAP.iss       # Inno Setup 脚本（版本由发布脚本注入）
├── docs/UPLOAD_CHECKLIST.md     # 上传规制（提交前必读）
├── Directory.Build.props        # 版本单源
├── CHANGELOG.md                 # 变更记录（Keep a Changelog）
└── 发布.ps1                     # 发布脚本（含 BOM，勿删）
```

## 开发约定

- **版本**：只改 `Directory.Build.props` 的 `<Version>`，程序集/安装包/产物命名全链路自动取值；每次发版在 `CHANGELOG.md` 补条目
- **提交**：推送前必读 `docs/UPLOAD_CHECKLIST.md`（身份/敏感信息红线）；已启用 pre-commit 敏感扫描（`core.hooksPath` 已配置，需 python 在 PATH）
- **CI**：push/PR 自动跑 GitHub Actions 构建（`.github/workflows/ci.yml`），产物为构建工件

## 已知限制

- IPv6 流量不受管控（不计数、不限速、不改 TTL）
- 部分学校 ePortal 要求 RSA 加密密码（`passwordEncrypt=true`），当前会登录失败，请浏览器手动认证
- 深澜/Dr.COM 登录为尽力而为的通用实现，未对各校定制协议做适配
- 限速/拉黑的实机效果以真机验证为准（见 CHANGELOG 0.2.2 的抓包层重构）

## 安全与合规

本项目仅供学习与研究。请遵守所在校园网的用户协议；认证助手只自动填写用户自己的账号密码，
不做验证码绕过。TTL 伪装等共享检测对抗能力仅用于学习目的，请勿用于违反协议或法规的场景。
校园网账号密码经 DPAPI 加密仅存本机，不进入任何网络传输与仓库。

## License

[MIT](LICENSE)

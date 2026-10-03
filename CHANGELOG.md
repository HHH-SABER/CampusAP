# Changelog

本项目遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式，
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。版本单源在 `Directory.Build.props`。

## [Unreleased]

### 已知问题

- 限速/拉黑/TTL 伪装的实机效果待验证（v0.2.2 改用 WinDivert Forward 层后的首验）
- 部分学校的 ePortal 门户要求 RSA 加密密码（`passwordEncrypt=true`），当前直发明文会登录失败，此时请用浏览器手动认证
- IPv6 流量不受管控（不计数、不限速、不改 TTL）

## [0.3.0] - 2026-10-03

### 新增

- 版本号单源管理：`Directory.Build.props` → 程序集、`发布.ps1` → Inno Setup（`/DMyAppVersion`）全链路取同一版本
- Web 管理页加一次性访问令牌：API 必须携带页面 URL 中的 `?token=`，挡住跨源 fire-and-forget 请求（CSRF）
- Web 管理页渲染设备名/主机名前做 HTML 转义（防恶意主机名注入管理页）
- Web 管理服务改为绑定实际热点网关（从已连接设备网段推导；不再写死 192.168.137.1，兼容 Win11 随机网段）
- 工程化基线：`CHANGELOG.md`、`LICENSE`、`.editorconfig`、GitHub Actions 构建 CI、`.gitignore` 去重

### 修复

- 版本号不一致：程序集与安装包脚本此前都停在 0.2.0，更新检查的版本比较随之失真

### 移除

- 死依赖 `H.NotifyIcon.Wpf`（托盘已改用 WinForms NotifyIcon，XAML 里只残留了命名空间声明与三个无引用处理器）

## [0.2.2] - 2026-10-03

### 修复

- **设备速率统计恒为 0**：根因是转发（transit）流量根本不经过 WinDivert Network 层（官方定义 Network 层只处理 "to/from the local machine" 的包）。TrafficEngine 重写为双层抓包：
  - Forward 层为主：NAT 前原始地址，按设备归属计数，并承担 TTL 伪装、限速、拉黑
  - Network 层兜底：主机↔客户端本地流量始终计数；Forward 层不可用时按旧行为接管，防重复计数
- `FixIpChecksum` 把校验和写进源 IP 的致命偏移错误（校验和在 offset 10-11，不是 12-13；12-15 是源地址）
- 更新检查不再采用 API 返回的 `html_url`（远端数据不得进入进程启动参数）

### 新增

- 抓包分层自诊断：引擎状态栏实时显示两层包计数，真机验证一眼判断哪层在工作
- 动态网段：从真实客户端 IP 推导 /24，未覆盖即重建过滤器（防 Win11 随机化热点网段）

## [0.2.1] - 2026-10-01

### 修复

- 托盘可见性：隐藏到托盘时每次弹气泡提示（Win11 默认把图标收进溢出区，用户曾误以为退出）
- 单实例保护：重复启动唤醒已有实例（EventWaitHandle 信号），不再出现双实例并存
- 关机/未显示窗口路径上给对话框设 Owner 导致的崩溃（事件日志 1026）

### 新增

- 设备命名持久化：双击设备名重命名，按 MAC 记住（自定义名 > 反向解析主机名 > IP）

## [0.2.0] - 2026-10-01

### 新增

- Web 管理页：HttpListener 监听 8899 端口，手机浏览器可查看设备、限速、拉黑
- TTL 伪装：客户端上行包 TTL 统一为 129（网关侧与 Windows 直发一致），规避多设备指纹
- OUI 厂商识别：内置常见手机/PC 厂商 MAC 前缀表
- 多门户校园网认证：锐捷 ePortal / 深澜 Srun / Dr.COM，从网关 302 重定向自动识别门户类型
- IPv6 链路本地流量进入过滤器范围
- 启动时检查 GitHub Releases 更新
- Inno Setup 安装包脚本（`installer/CampusAP.iss`）
- 悬浮窗紧凑化、网络环境自动识别（普通网络/校园网未登录/已登录）

## [0.1.0] - 2026-10-01

### 新增

- M1 热点共享：能力检测（8 种不可用原因有明确诊断）、启停、SSID/密码/频段配置、扫码入网二维码
- M3/M4 设备管理：系统 tethering API 设备列表、WinDivert 逐包流量统计、令牌桶限速（20M/5M/1M/256K）、拉黑/解除、悬浮窗、管理员提权引导
- M2 校园网认证：门户自动探测（网关 302）、ePortal 直发 POST 登录、60 秒看门狗自动重登、账密 DPAPI 加密存储
- 托盘（关闭行为可记忆）、退出热点提示、关闭行为对话框
- 安全基线：pre-commit 敏感信息扫描钩子、上传规制文档

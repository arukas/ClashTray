# ClashTray

![ClashTray](.github/assets/ClashTray-README-Banner-1600x500.png)

轻量、原生、Windows 优先的 Mihomo 托盘客户端。ClashTray 把配置、节点、规则、连接、日志和系统代理控制收进一个靠近任务栏的紧凑面板，让日常切换代理不需要打开浏览器仪表盘。

当前发布目标仅为 Windows x64；不提供 x86、ARM64、macOS 或 Linux 版本。

当前正式版本为 **0.3.8**，见 [发布说明](docs/releases/0.3.8.md)。本次为设置页的核心更新加入官方下载地址预设：一键按版本号填入官方 GitHub 发布地址，钉版版本同时附带官方 SHA-256；来源白名单校验保持不变。

[![CI](https://github.com/arukas/ClashTray/actions/workflows/ci.yml/badge.svg)](https://github.com/arukas/ClashTray/actions/workflows/ci.yml) [![Release](https://github.com/arukas/ClashTray/actions/workflows/release.yml/badge.svg)](https://github.com/arukas/ClashTray/actions/workflows/release.yml)

ClashTray 是使用 C#、.NET 10 和 WinUI 3 独立实现的 Windows Mihomo 客户端。产品流程参考了 [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) 对紧凑、托盘优先代理客户端交互的探索；ClashBar 的源代码和资源不属于 ClashTray，也不随 ClashTray 分发。

## 功能

- 托盘优先：左键打开锚定在真实托盘图标旁的面板，右键提供快速操作，关闭面板回到托盘。
- 配置管理：导入本地 `.yaml` / `.yml`，或添加订阅地址；支持选择、刷新、重载和删除。
- Mihomo 控制：校验、启动、停止、重启、崩溃检测，以及 `Rule`、`Global`、`Direct` 模式切换。
- 节点与 Provider：代理组选择、节点切换、延迟测试、Proxy Provider 和 Rule Provider 手动刷新；可选数据按面板可见性和当前页面需求采集，保留已确认数据。
- 网络开关：System Proxy 和 TUN 分开管理；System Proxy 保存并按所有权恢复原始 Windows 代理状态。
- 可观测性：实时上下行速率、累计流量、连接数、内存、规则搜索、连接筛选和有界日志；外部节点、规则、连接和日志字段均有长度与数量上限，数据未变化时页面不会重建列表。
- 设置与维护：HTTP / SOCKS / Mixed 端口、`allow-lan`、IPv6、TCP concurrent、日志级别、DNS/FakeIP 缓存、Geo 数据库、开机启动和定时订阅刷新。
- 远程端点安全边界：沿用已有远程 Mihomo Controller 目录、会话和传输设置，为指定 HTTPS 端点保存受保护 secret / 自定义 CA；实际公网/局域网部署仍需单独验收。
- 安全更新：核心来源固定为官方 MetaCubeX/Mihomo 发布，下载后校验 SHA-256，再以原子方式替换并支持回滚。
- 语言与显示：简体中文 / English，系统、浅色、深色、高对比度和常见 DPI 缩放。

> **本轮 alpha 范围：** 统一本地/服务启动确认、拆分请求台账与更新事务、明确设置/配置/退出所有权、提取展示逻辑、隔离测试组装、统一控制器资源采集并整理模块目录。Wi-Fi/SSID 自动切换及相关监听/编辑入口继续关闭。

## 体积与发布版本

GitHub Actions 会在 Windows runner 上构建并发布两种 Windows x64 产物：Full 和 NoCET。两者都使用 Inno Setup 7 的 LZMA2 solid 压缩安装器；App、Service 和 Setup 都是 x64，App 与 Service 合并到同一个自包含发布目录，共用一套 .NET runtime，不需要目标机器预装任何 .NET 或 Windows App Runtime。

| 版本 | 内容 | 适合谁 |
| --- | --- | --- |
| `ClashTray-Setup-Full.exe` | 自包含 App + Service + 已验证的 Mihomo 核心 | 下载后直接使用 |
| `ClashTray-Setup-NoCET.exe` | 自包含 App + Service + 已验证的 Mihomo 核心，关闭 CET 兼容标志 | 补丁较旧、无法启动 .NET 10 的 Windows 10 22H2 |

Full 和 NoCET 使用 `packaging/mihomo-release.json` 中固定的官方版本和校验值。NoCET 只作为旧补丁 Windows 10 的兼容包，关闭 .NET 进程的 CET 兼容标志，会减少一层硬件控制流防护；普通用户优先选择 Full。

每个 EXE 旁边都会生成同名 `.sha256` 校验文件。发布页还会提供 `SHA256SUMS.txt`，不要从不明镜像下载核心或安装器。

## 安装与快速上手

系统要求：Windows 11 x64，或受支持的 Windows 10 22H2 x64；当前只有 Windows x64 版本，不提供 x86 或 ARM64 版本。Full 和 NoCET 版本自带 .NET 运行时，无需预装任何运行时。补丁较旧的 Windows 10 22H2 可优先尝试 NoCET；正常情况下请使用 Full 并安装所有可用的 Windows 更新。首次安装会请求一次 UAC 权限，用于安装受限的 `ClashTrayService`；日常使用以普通用户权限运行。

1. 从 [Releases](https://github.com/arukas/ClashTray/releases) 下载合适版本并核对 SHA-256。
2. 运行安装器，完成服务注册后从托盘打开 ClashTray。
3. 在 Proxy 页面导入配置或添加订阅，等待配置校验完成。
4. 点击 Start，选择 `Rule` / `Global` / `Direct`，再选择节点并执行延迟测试。
5. 按需独立打开 System Proxy 或 TUN；面板会显示已确认的系统和核心状态。

同一时间只应让一个 Clash / Mihomo 客户端接管系统代理。卸载入口在 Windows“设置 > 应用 > 已安装的应用”中；卸载会先停止核心、关闭 TUN、恢复 ClashTray 自己写入的代理状态，然后询问是否保留配置、订阅和日志。

## 面板页面

| 页面 | 用途 |
| --- | --- |
| Proxy | 配置、模式、System Proxy、TUN、节点、延迟和 Provider |
| Rules | 规则统计、搜索、过滤和 Provider 更新 |
| Connections | 当前连接、排序、详情、关闭单条和全部关闭 |
| Logs | App / Mihomo 日志、来源和级别过滤、搜索、复制、清除 |
| Settings | 语言、主题、端口、运行/代理选项、启动、订阅、核心维护 |

## 本地构建

开发环境：Windows 10 22H2 或 Windows 11、Visual Studio 2026 的 Windows App SDK / WinUI 工具。`global.json` 选择 .NET SDK 10.0.400，只允许同一 feature band 的稳定补丁；中央版本与八个项目的 NuGet 锁文件共同固定依赖解析。

```powershell
dotnet restore ClashTray.sln --locked-mode
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64 --no-restore
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64 --no-build --no-restore
```

构建压缩安装器：

```powershell
# 默认 Full：包含经过校验的 Mihomo 核心
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.3.8 -Variant Full

# 旧版 Windows 10 兼容包：自包含、包含核心、关闭 CET
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.3.8 -Variant NoCET
```

从 [文档索引](docs/README.md) 可直接找到 [开发环境](docs/setup.md)、[模块边界](docs/module-map.md)、[测试环境](docs/test-environment.md) 与 [发布流程](docs/release.md)。旧的 `Build-MSIX.ps1` 和 `packaging/ClashTray.Package` 保留作实验性 / 历史打包材料；当前发布路径是 Inno Setup LZMA2 solid 压缩的 EXE 安装器。

## 架构与数据

桌面 App 负责 WinUI 3 面板、托盘、每用户设置、订阅、System Proxy 和 loopback REST/WebSocket 客户端。Windows Service 负责需要提升权限的核心生命周期和 TUN 操作，并通过受限命名管道接受白名单命令。Mihomo 始终作为独立进程运行，External Controller 默认只监听 `127.0.0.1`。

用户数据位于 `%LOCALAPPDATA%\ClashTray`，服务运行数据位于 `%PROGRAMDATA%\ClashTray`。控制器密钥使用 Windows 保护存储；配置、订阅 URL、授权头和代理凭据不会写入日志。运行时的连接、流量和日志缓冲区都有上限，避免异常核心耗尽 UI 内存。

## 规划

当前重点是现有日常路径的稳定性和真实机器验收：核心/服务恢复、System Proxy 所有权、TUN 回滚、睡眠与网络变化、安装升级卸载，以及 DPI/辅助功能。Wi-Fi/SSID 自动切换、ARM64、历史流量分析和更多语言继续延期。本轮测试证据及未覆盖场景见 [结构优化实施记录](docs/structure-optimization-results-2026-10-10.md)。

## 致谢与许可

感谢 [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo) 提供核心能力，也感谢 [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) 对轻量菜单栏代理客户端交互方向的启发。

ClashTray 本身采用 [MIT License](LICENSE)。Mihomo 是独立运行的第三方核心，采用 GNU GPL v3.0。Full 和 NoCET 安装器包含官方未修改的 Mihomo Windows 二进制；其 GPLv3 许可证、精确版本、二进制校验值以及对应版本的源代码获取信息随发行版提供。ClashTray 与 Mihomo 通过 Mihomo External Controller HTTP/WebSocket 接口和进程管理边界通信。详见 [docs/third-party-notices.md](docs/third-party-notices.md)。

## 贡献

欢迎提交 Issue 和 Pull Request。请不要提交订阅地址、代理凭据、控制器密钥、用户日志、核心运行数据、证书或发布输出。提交前运行完整构建和测试，并在涉及 Windows UI、代理或服务边界时记录实际验证环境。

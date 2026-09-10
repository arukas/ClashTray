# ClashTray

轻量、原生、Windows 优先的 Mihomo 托盘客户端。ClashTray 把配置、节点、规则、连接、日志和系统代理控制收进一个靠近任务栏的紧凑面板，让日常切换代理不需要打开浏览器仪表盘。

[![CI](https://github.com/arukas/ClashTray/actions/workflows/ci.yml/badge.svg)](https://github.com/arukas/ClashTray/actions/workflows/ci.yml) [![Release](https://github.com/arukas/ClashTray/actions/workflows/release.yml/badge.svg)](https://github.com/arukas/ClashTray/actions/workflows/release.yml)

ClashTray 是使用 C#、.NET 10 和 WinUI 3 独立实现的 Windows Mihomo 客户端。产品流程参考了 [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) 对紧凑、托盘优先代理客户端交互的探索；ClashBar 的源代码和资源不属于 ClashTray，也不随 ClashTray 分发。

## 功能

- 托盘优先：左键打开锚定在真实托盘图标旁的面板，右键提供快速操作，关闭面板回到托盘。
- 配置管理：导入本地 `.yaml` / `.yml`，或添加订阅地址；支持选择、刷新、重载和删除。
- Mihomo 控制：校验、启动、停止、重启、崩溃检测，以及 `Rule`、`Global`、`Direct` 模式切换。
- 节点与 Provider：代理组选择、节点切换、延迟测试、Proxy Provider 和 Rule Provider 手动刷新。
- 网络开关：System Proxy 和 TUN 分开管理；System Proxy 保存并按所有权恢复原始 Windows 代理状态。
- 可观测性：实时上下行速率、累计流量、连接数、内存、规则搜索、连接筛选和有界日志。
- 设置与维护：HTTP / SOCKS / Mixed 端口、`allow-lan`、IPv6、TCP concurrent、日志级别、DNS/FakeIP 缓存、Geo 数据库、开机启动和定时订阅刷新。
- 安全更新：核心来源固定为官方 MetaCubeX/Mihomo 发布，下载后校验 SHA-256，再以原子方式替换并支持回滚。
- 语言与显示：简体中文 / English，系统、浅色、深色、高对比度和常见 DPI 缩放。

## 体积与发布版本

GitHub Actions 会在 Windows runner 上构建并发布四种 x64 产物。Full、NoCET 和 NoCore 使用压缩的自包含单文件发布；Framework 使用 framework-dependent 单文件宿主，payload 仍为压缩 ZIP，因此不会把 Mihomo 二进制塞进安装包。

| 版本 | 内容 | 适合谁 |
| --- | --- | --- |
| `ClashTray-Setup-Full.exe` | 自包含 App + Service + 已验证的 Mihomo 核心 | 下载后直接使用 |
| `ClashTray-Setup-NoCET.exe` | 自包含 App + Service + 已验证的 Mihomo 核心，关闭 CET 兼容标志 | 补丁较旧、无法启动 .NET 10 的 Windows 10 22H2 |
| `ClashTray-Setup-NoCore.exe` | 自包含 App + Service，不内置核心 | 已有核心，或希望首次运行后再更新核心 |
| `ClashTray-Setup-Framework.exe` | Framework-dependent App + Service，不内置核心 | 已安装 .NET 10 和 Windows App SDK，追求最小下载体积 |

Full 和 NoCET 使用 `packaging/mihomo-release.json` 中固定的官方版本和校验值。NoCET 只作为旧补丁 Windows 10 的兼容包，关闭 .NET 进程的 CET 兼容标志，会减少一层硬件控制流防护；普通用户优先选择 Full。NoCore / Framework 安装时不会删除已有的 `%PROGRAMDATA%\ClashTray\core`；新安装可以在应用内通过经过验证的核心更新流程补齐核心。Framework 版本需要目标机器已具备 .NET 10 Desktop Runtime 和可用的 Windows App SDK runtime。WinUI 3 是保留桌面 UI 所需的组件，不能从客户端本身删除；Framework 版本只是把 .NET / Windows App SDK runtime 外置，因此体积更小但安装前提更多。

每个 EXE 旁边都会生成同名 `.sha256` 校验文件。发布页还会提供 `SHA256SUMS.txt`，不要从不明镜像下载核心或安装器。

## 安装与快速上手

系统要求：Windows 11，或受支持的 Windows 10 22H2；x64；Full 和 NoCET 版本自带 .NET 运行时。补丁较旧的 Windows 10 22H2 可优先尝试 NoCET；正常情况下请使用 Full 并安装所有可用的 Windows 更新。首次安装会请求一次 UAC 权限，用于安装受限的 `ClashTrayService`；日常使用以普通用户权限运行。

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
| Settings | 语言、主题、端口、网络选项、启动、订阅、核心维护 |

## 本地构建

开发环境：Windows 10 22H2 或 Windows 11、.NET SDK 10.0.400+、Visual Studio 2026 的 Windows App SDK / WinUI 工具。仓库通过 `Directory.Packages.props` 固定依赖版本。

```powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64 --no-build
```

构建压缩安装器：

```powershell
# 默认 Full：包含经过校验的 Mihomo 核心
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant Full

# 旧版 Windows 10 兼容包：自包含、包含核心、关闭 CET
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant NoCET

# 自包含但不带核心
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant NoCore

# 更小的 framework-dependent 版本
.\packaging\Build-EXE.ps1 -Configuration Release -PackageVersion 0.1.0 -Variant Framework
```

详见 [docs/setup.md](docs/setup.md)、[docs/release.md](docs/release.md) 和 [docs/roadmap.md](docs/roadmap.md)。旧的 `Build-MSIX.ps1` 和 `packaging/ClashTray.Package` 保留作实验性 / 历史打包材料；当前发布路径是压缩的 EXE 安装器。

## 架构与数据

桌面 App 负责 WinUI 3 面板、托盘、每用户设置、订阅、System Proxy 和 loopback REST/WebSocket 客户端。Windows Service 负责需要提升权限的核心生命周期和 TUN 操作，并通过受限命名管道接受白名单命令。Mihomo 始终作为独立进程运行，External Controller 默认只监听 `127.0.0.1`。

用户数据位于 `%LOCALAPPDATA%\ClashTray`，服务运行数据位于 `%PROGRAMDATA%\ClashTray`。控制器密钥使用 Windows 保护存储；配置、订阅 URL、授权头和代理凭据不会写入日志。运行时的连接、流量和日志缓冲区都有上限，避免异常核心耗尽 UI 内存。

## 规划

当前重点是稳定完成日常使用路径：核心启动与恢复、System Proxy 所有权、TUN 回滚、订阅与 Provider 刷新、连接和日志排障、压缩发布与升级卸载。Wi-Fi/SSID 自动切换、远程端点、ARM64、历史流量分析和更多语言属于后续范围，除非单独提出。

## 致谢与许可

感谢 [MetaCubeX/mihomo](https://github.com/MetaCubeX/mihomo) 提供核心能力，也感谢 [Sitoi/ClashBar](https://github.com/Sitoi/clashbar) 对轻量菜单栏代理客户端交互方向的启发。

ClashTray 本身采用 [MIT License](LICENSE)。Mihomo 是独立运行的第三方核心，采用 GNU GPL v3.0。Full 和 NoCET 安装器包含官方未修改的 Mihomo Windows 二进制；其 GPLv3 许可证、精确版本、二进制校验值以及对应版本的源代码获取信息随发行版提供。ClashTray 与 Mihomo 通过 Mihomo External Controller HTTP/WebSocket 接口和进程管理边界通信。详见 [docs/third-party-notices.md](docs/third-party-notices.md)。

## 贡献

欢迎提交 Issue 和 Pull Request。请不要提交订阅地址、代理凭据、控制器密钥、用户日志、核心运行数据、证书或发布输出。提交前运行完整构建和测试，并在涉及 Windows UI、代理或服务边界时记录实际验证环境。

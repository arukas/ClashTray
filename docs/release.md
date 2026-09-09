# 发布与体积策略

ClashTray 的当前发布入口是 `packaging/Build-EXE.ps1`。脚本先发布 App 和 Service，再把 payload 压缩成 ZIP，最后将 ZIP 嵌入压缩的单文件安装器。这样安装器只有一个下载入口，安装过程中不会依赖临时网络下载；Full 版本仍会在构建阶段从官方 Mihomo release 下载并校验核心。

## 版本矩阵

| `-Variant` | App / Service | Mihomo | 运行时要求 | 产物 |
| --- | --- | --- | --- | --- |
| `Full` | self-contained | 内置，固定版本 + SHA-256 | Windows 10/11 x64 | `ClashTray-Setup-Full.exe` |
| `NoCore` | self-contained | 不内置；可在应用内验证更新 | Windows 10/11 x64 | `ClashTray-Setup-NoCore.exe` |
| `Framework` | framework-dependent，Windows App SDK 也不自包含 | 不内置 | .NET 10 Desktop Runtime + Windows App SDK runtime | `ClashTray-Setup-Framework.exe` |

`Full` 是默认和推荐版本。`NoCore` 用于降低下载体积或复用已安装的核心；`Framework` 只适合明确管理运行时的机器。无核心安装器不会清空已有 `%PROGRAMDATA%\ClashTray\core`，因此可以用 NoCore/Framework 做应用升级而保留核心。 WinUI 3 仍是桌面 UI 的必要依赖，Framework 只把 .NET / Windows App SDK runtime 改为外置，不会删除客户端 UI。

Full 和 NoCore 构建使用 `EnableCompressionInSingleFile=true`，Framework 构建使用 framework-dependent 单文件宿主（.NET 不允许压缩 framework-dependent native self-extract），三者都会在安装器内部使用 `Compress-Archive -CompressionLevel Optimal` 压缩 App、Service 和 Core payload。安装器旁生成 SHA-256 sidecar：

```powershell
.\packaging\Build-EXE.ps1 -Variant Full -PackageVersion 1.0.0 -OutputDirectory .\packaging\out\1.0.0\full
Get-FileHash .\packaging\out\1.0.0\full\ClashTray-Setup-Full.exe -Algorithm SHA256
```

## 发布前检查

1. 在干净 Windows x64 环境运行 `dotnet restore`、`dotnet build` 和 `dotnet test`。
2. 构建三种变体，检查文件存在、SHA-256 sidecar 和体积报告。
3. Full 包验证官方 Mihomo archive 的版本、架构、PE 头和 SHA-256；同时保留 `Mihomo-LICENSE.txt` 与 `Mihomo-Release.txt`。
4. NoCore 包在无核心的全新目录完成安装，确认应用能提示核心缺失并进入更新流程；在已有核心的安装上升级，确认核心不被覆盖或删除。
5. Framework 包在有和没有运行时的环境分别检查，缺少依赖时必须给出可理解的错误。
6. 验证首次安装、升级、卸载保留/删除数据、代理状态恢复、TUN 失败回滚和服务重启。
7. 只有通过上述检查后，才让 `release.yml` 创建 GitHub Release。

## GitHub Actions

- `ci.yml` 在 push、Pull Request 和手动触发时运行 restore、build、test。
- `release.yml` 在 `v*.*.*` tag 或手动输入版本时运行 CI 检查，随后构建 Full / NoCore / Framework，生成 `SHA256SUMS.txt`，并用仓库的 `GITHUB_TOKEN` 创建 Release。
- Actions 不读取订阅、密钥或签名证书。签名证书若以后启用，应通过 GitHub Actions secret 注入，不能放进仓库。

当前 workflow 只发布 x64；ARM64、MSIX 和带企业签名的渠道保留在后续计划中。

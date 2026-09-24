# ClashTray 第六轮定向整改结果

日期：2026-09-24
复核基线：`1c5e267de5f51af8769b05ad54f2687d4c5b50dc`
范围：仅 H1–H3。保留工作区中既有未跟踪文档；未改产品版本文件、未安装服务、未替换用户核心、未触碰真实代理或 TUN。

## 总览

| 项目 | 状态 | 主要结果 |
|---|---|---|
| H1 更新来源 | 已实现并自动化验证 | 结构化 authority/path-segment 校验；Runtime 在等锁前拒绝；服务边界再次校验 |
| H2 有界脱敏 | 已实现并自动化验证 | 扫描输出窗口内的全部 URL authority；不完整 userinfo 保守隐藏；字节和轮转限额保留 |
| H3 官方 ZIP updater | 已实现并隔离验证 | 明确接受唯一归档根条目 `mihomo-windows-amd64.exe`，安装目标仍为 `mihomo.exe` |

## H1：核心更新来源校验

**根因。** 旧校验只要求 HTTPS、`github.com` 主机并对路径调用 `Contains`。它会把其他 owner/repository 下、仅在路径后段包含官方字符串的 URL 当成官方 release。Runtime 在进入操作锁并可能停止正在运行的核心前没有预校验。

**修改文件。** `src/ClashTray.Core/CoreUpdater.cs`、`src/ClashTray.Core/ClashTrayRuntime.cs`、`src/ClashTray.Service/ServiceRuntimeController.cs`、`tests/ClashTray.Core.Tests/CoreUpdaterTests.cs`、`tests/ClashTray.Core.Tests/RuntimeStateTests.cs`、`tests/ClashTray.IntegrationTests/BoundaryTests.cs`。

**契约。** 仅接受 HTTPS `github.com` 的官方 release URL；authority 不含 userinfo 或端口，拒绝 query、fragment、百分号编码、反斜线、空/重复路径段和 URI 规范化路径。路径必须逐段精确匹配 `MetaCubeX/mihomo/releases/download/{tag}/{asset}`；tag 与 manifest version 相同，并符合 `vMAJOR.MINOR.PATCH[-prerelease]`（ASCII SemVer、无 build metadata、最多 128 字符）；资产必须精确为 `mihomo-windows-amd64-{tag}.zip`。核心更新只支持 Windows x64。空 SHA 仍允许；如提供 SHA，则必须为 64 位 ASCII 十六进制并在下载后验证。启动时不新增核心哈希重算。

Runtime 在等待 operation lock 前验证 manifest，因而无效更新不会停核心或调用服务。服务 InstallCore 边界仍独立执行同一纯校验；服务测试直接覆盖该边界。无效来源回归使用 validator 或 fake handler，没有请求合成 URL。

**正式回归与结果。** 新增固定官方 URL / prerelease 正向用例；foreign owner/repository path、raw/blob、tag/version 不一致、错误架构/资产、编码路径、userinfo、非标准及显式端口、query/fragment、错误 SemVer 负向用例；fake handler 验证请求计数为零且已安装核心、元数据及 `.previous` 恢复文件字节不变；Runtime 验证无效输入不会进入服务通道；Integration 验证服务边界拒绝。初始回归在旧实现上实际失败（foreign repository 被接受、非法 version 被接受）；修复后 CoreUpdater 和 H1 定向回归通过。完整 Core 与 Integration 数字见“最终自动化验证”。

**剩余限制。** 来源策略有意只允许固定的官方 GitHub release URL 形态，不支持镜像、重定向地址作为 manifest 输入或未来更名资产；需要扩大时须同步更新契约和测试。

## H2：多 URL 截断脱敏

**根因。** `BoundedDiagnosticWriter.SanitizeBounded` 原先仅定位第一处 HTTP(S) scheme。若安全 URL 在前，后面的 URL userinfo 在输出边界之后且 `@` 超出 lookahead，敏感前缀可能先被截断并写入日志。

**修改文件。** `src/ClashTray.Core/BoundedDiagnosticWriter.cs`、`tests/ClashTray.Core.Tests/BoundedDiagnosticWriterTests.cs`。

**实现与边界。** 扫描上限仍为单字段最大输出字符数加 256 字符 lookahead（message 为 512+256）；从上一个 authority 结束点继续查找每个可见 HTTP(S) scheme，不重复从头拼接整串或扫描前缀。若某 authority 可能越过输出/扫描边界且看不到 `@`，从该 URL 起保守隐藏剩余窗口，再交给现有 `ErrorSanitizer` 处理完整 URL、Authorization 和 query secret。总扫描窗口固定有界，算法随窗口长度线性增长；记录仍不超过 16 KiB，文件仍最多 3 个且每个不超过 1 MiB，异常树、stack-frame 和 rotation 限额不变。

**正式回归与结果。** 新增安全 URL 在前、多个后续 URL、Authorization 与 query token 混排，并将 userinfo `@` 放在扫描下标 767/768/769 的合成输入。新用例在旧逻辑上失败并指出合成密码前缀落盘；修复后新旧单 URL、混排、Unicode、文件轮转和失败保留测试均通过。断言检查敏感片段不在输出内，同时逐文件检查 byte/record 限额。

**性能证据与限制。** 证明来自固定 768 字符 message 扫描窗口、authority 单向推进和正式回归，不是独立微基准；本轮没有修改或运行列表性能 harness，也未声称测得真实 UI 帧耗时。异常内容长度以 UTF-16 字符窗口限制，输出仍以 UTF-8 byte budget 截断。

## H3：接受固定官方 Mihomo ZIP

**根因。** updater 只寻找 `mihomo.exe`，而固定 `v1.19.31` 官方 Windows x64 ZIP 的源条目名为 `mihomo-windows-amd64.exe`，导致经过来源/SHA 校验的官方包仍无法应用内安装。

**修改文件。** `src/ClashTray.Core/CoreUpdater.cs`、`tests/ClashTray.Core.Tests/CoreUpdaterTests.cs`、`tests/ClashTray.IntegrationTests/OfficialMihomoUpdaterTests.cs`、`tests/ClashTray.IntegrationTests/OfficialMihomoTestSupport.cs`、`tests/ClashTray.IntegrationTests/OfficialMihomoServiceInteropTests.cs`、`packaging/Test-OfficialMihomo.ps1`、`src/ClashTray.Service/ServiceRuntimeController.cs`、`tests/ClashTray.IntegrationTests/BoundaryTests.cs`。

**实现与隔离。** updater 只接受唯一的归档根条目 `mihomo-windows-amd64.exe`，同名重复或嵌套条目拒绝；不枚举任意 `*.exe`。解压后仍执行 x64 PE 检查，写入目标仍是受管路径 `mihomo.exe`，保留 SHA、归档/文件大小、元数据、原子替换、`.previous` 和回滚检查。新的 `RequiresOfficialMihomo` Integration 用例从门禁显式传入归档路径，测试自身再次校验固定 SHA；fake HTTP handler 只从该归档流提供字节，安装目的地为临时 `AppPaths`，不会启动刚安装的 updater 核心或覆盖真实安装。

为使整个 Integration 回归不触碰主机代理恢复记录，`ServiceRuntimeController` 的内部测试构造函数增加可替换恢复回调；生产公开构造仍默认调用 `SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers`。Integration service 夹具明确注入 no-op。官方核心交互仅使用隔离配置、loopback controller 和 `tun.enable=false`。

**正式回归与结果。** 官方原始 ZIP 的 SHA-256 为 `38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67`（22,430,598 bytes）。新增的强制官方 updater 用例在旧实现上因缺少 `mihomo.exe` 而失败；候选规则修复后通过，并核对安装目标、元数据、归档摘要、PE/x64 与安装文件摘要。Core 合成 ZIP 另测重复候选拒绝。`Test-OfficialMihomo.ps1` 现验证并显式提供 archive path/SHA，支持参数传入已验证归档或下载官方固定归档；官方门禁逐项比较发现数、声明数及非通过结果。

**剩余限制。** 该候选契约绑定当前官方 Windows amd64 ZIP 命名。没有执行真实服务安装、用户核心替换或代理/TUN 操作；隔离 updater 测试证明文件安装事务，不替代用户机器上的签名/权限、升级恢复和卸载验收。

## 最终自动化验证

- Release x64 完整解决方案构建：成功，0 warnings、0 errors。
- Release x64 Core：428/428 passed，0 skipped。
- Release x64 Integration：32/32 passed，0 skipped；设置 `CLASHTRAY_MIHOMO_REQUIRED=true`，并显式提供固定 Mihomo 可执行文件和官方 ZIP 路径。
- `packaging/Test-OfficialMihomo.ps1 -Configuration Release -ExistingArchivePath <已校验官方 ZIP>`：官方类别 5/5 passed，0 skipped；固定版本 `v1.19.31`，脚本重新校验 ZIP SHA。该门禁执行官方 config/controller/service 与 updater 集成用例。
- H1/H2 旧实现失败用例、修复后定向用例，以及 H3 官方归档用例均已实际执行；合成 secret 只用于测试。
- 未修改 CI/Release workflow 文件。上述是本地 Release 测试和本地官方脚本证据；GitHub Actions / Release 的远端运行状态在本记录写入时尚未确认。

## 验收层级与剩余工作

- **实现完成：** H1–H3 修改及交付记录完成。
- **自动化验证：** Release x64 build、Core、Integration 和本地固定官方 Mihomo 强制门禁通过。
- **隔离 UI 验证：** 本轮没有启动 WinUI UI smoke；行为通过 Runtime/服务边界自动化覆盖。
- **真实系统验收：** 未执行。没有安装服务、触碰真实 Mihomo 安装、System Proxy、TUN、代理流量或真实用户配置。
- **远端验证：** 当前尚未推送，远端 CI 和 alpha Release 尚未运行；在按追加请求推送后，应以 Actions 结果及 GitHub Release 状态更新本记录。

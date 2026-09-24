# ClashTray 第五轮定向整改结果

日期：2026-09-24
Git 基线：`9919a5a`（本轮开始时 `HEAD` 与报告基线一致；未发现后续已修复版本）
范围：仅处理 G1–G3，并记录首次远端 CI 暴露的官方核心集成测试临时目录共享锁问题；保留既有无关工作树文件，不更改应用版本。用户随后授权推送及 alpha 发布。

## 结果摘要

| 项目 | 状态 | 主要证据 |
|---|---|---|
| G1 异步命令端点漂移 | 已修复并自动化验证 | 5 个 fake HTTP handler 回归全通过；旧 A 命令在排队期间切至 B 后，A/B 均没有收到该命令；携带 B generation 的后续命令成功 |
| G2 诊断截断后凭据片段落盘 | 已修复并自动化验证 | 合成 userinfo、Authorization、query secret 回归通过；截断的 URL userinfo 超出 256 字符 lookahead 时也没有前缀写入日志 |
| G3 输出行读取丢行 | 已修复并自动化验证 | 不同读取分块得到完全一致的日志序列；超长行后正常行保留；EOF、CRLF、取消均通过 |

## G1：固定异步命令的 endpoint/session generation

### 根因

Connections 页原来只把连接 ID 交给 Runtime。Runtime 在取得共享操作锁后才查找当前远程 session；等待期间从 A 切到 B 时，旧请求会把同一个 ID 的 DELETE 发给 B。模式、节点、Provider 和缓存/Geo 操作也共用该执行入口，存在同一类晚解析风险。

### 修改

- `src/ClashTray.Contracts/EndpointContracts.cs`：新增 `EndpointCommandTarget`，携带 `EndpointId` 和 session `Generation`。
- `src/ClashTray.App/MainWindow.xaml.cs`：从显示快照捕获结构化目标，并传给 Proxy、Connections、Settings 以及托盘模式命令。Logs 与 Rules 的只读路径不传变更目标。
- `src/ClashTray.App/ConnectionsPage.xaml.cs`、`ProxyPage.xaml.cs`、`SettingsPage.xaml.cs`、`App.xaml.cs`：关闭连接、模式、节点、Provider、DNS/FakeIP cache 和 Geo 命令携带生成该 UI 命令的目标。
- `src/ClashTray.Core/ClashTrayRuntime.cs`、`ProxyOperationCoordinator.cs`：在首次等待前保存预期目标；共享操作锁准入后、发送前核对 endpoint ID、generation 和当前 session。对过期目标抛出明确的 session 已切换错误，不回退到当前端点。远端 capability 检查、操作锁、发送后的 session 检查和确认刷新均保留。
- 复核 `RefreshDataAsync`：它在第一次 await 前捕获当前 session，属于只读刷新；没有将其改成变更命令。

### 正式回归与结果

`tests/ClashTray.Core.Tests/RuntimeEndpointCommandTargetTests.cs` 使用隔离目录、fake connector 和 recording `HttpMessageHandler`；A/B 返回相同连接 ID `c1`。测试先持有实际 Runtime 操作锁，再发起并排队旧命令，切换端点或 generation 后放行锁。

- 修改前，回归复现了关闭全部、关闭单条以及同一端点 generation 变化未拒绝这 3 个问题。
- 修改后 5/5 通过，0 跳过。
- A→B 排队切换后，旧命令被拒绝，A 与 B 都没有收到 DELETE；随后携带 B 目标的正常关闭成功，B 收到且只收到一次 DELETE（单条为 `/connections/c1`，全部为 `/connections`）。
- 无切换时当前 A 的目标可正常关闭；同 ID 不能绕过 generation 校验；排队取消不会发出请求。

## G2：保持脱敏安全性的有界诊断截断

### 根因

异常消息先被截到 512 字符，再交给 `ErrorSanitizer`。userinfo 脱敏需要识别 URL authority 内的 `@`；截断点落在 userinfo 中且 `@` 尚未出现时，脱敏器把用户名/密码前缀当成普通 URL 文本，写入诊断文件。

### 修改

- `src/ClashTray.Core/BoundedDiagnosticWriter.cs`：对待脱敏文本先取“字段输出上限 + 256 字符 lookahead”的有限前缀，再脱敏并裁到原字段上限。若 HTTP(S) URL authority 越过输出边界且在扫描范围内仍没有 `@`，将该 authority 起的剩余片段整体隐藏，避免把未闭合 userinfo 前缀留在日志。Authorization 与 query secret 在有限扫描前缀上先脱敏。
- 扫描输入有固定上限：消息最多 768 字符、type 最多 416 字符、stack 方法位置最多 512 字符；版本仍限制为 64 字符。诊断输出约束不变：每条记录最多 16 KiB、最多 3 个各 1 MiB 文件、每异常最多 8 个异常详情及 6 个有限栈帧。

### 正式回归与结果

`tests/ClashTray.Core.Tests/BoundedDiagnosticWriterTests.cs` 全部使用合成标记。先行测试在旧实现上失败，确认截断 userinfo 的敏感前缀确实落盘。修复后通过，覆盖：

- userinfo 中的合成凭据在输出截断点开始，并延伸 2,048 个字符、超过 lookahead 后才出现 `@`；
- 截断点位于 Authorization Bearer 值和 `token=` 值内部；
- 输出文件总量和记录字节上限仍满足原配置。

日志中不含 `SYNTHETIC_` 前缀。完整 Core 回归中的诊断树限额、并发轮转、Unicode、异常失败保留等既有测试也全部通过。

## G3：保留读取块内后续行的输出行读取器

### 根因

旧读取器每次调用都创建 1,024 字符局部缓冲区，遇到第一个 LF 就返回；缓冲区中同块其余字符因此被丢弃。超长行在读取直到 LF 时也会一并吞掉同块后续行。

### 修改

- `src/ClashTray.Core/MihomoProcessManager.cs`：为每个 stdout/stderr drain 生命周期创建一个 `BoundedOutputLineReader`，保留 1,024 字符缓冲区的 offset/count。返回当前行时保留未消费部分。超过既有 64 KiB 单行限制后继续跳过当前行至 LF，但保留 LF 后的字符。CRLF 规范化、尾行、取消和 drain 异常处理行为保持一致。
- `DrainOutputAsync` 是生产 drain 使用的同一路径，供可控 `TextReader` 自动化测试调用；stdout/stderr 仍通过现有事件顺序进入日志流水线。

### 正式回归与结果

`tests/ClashTray.Core.Tests/MihomoOutputLineReaderTests.cs` 在修复前两项失败：单次读取块中的行序列少行，且超长行后的正常行消失。修复后 3/3 通过，0 跳过：

- 同一输入使用单块、逐字符和多组变长块读取时，输出均为 `first`, `second`, `third`, `fourth-at-eof`；覆盖 LF、CRLF 和无换行 EOF 尾行。
- 64 KiB 限长的长行仅输出保留前缀和原截断标记，后续 `normal-after-long-line` 完整到达。
- 取消时，尚未结束的部分行不发布，drain 按取消令牌退出。

### 远端 CI 暴露的临时目录清理竞态

- GitHub master CI run 80（提交 `493543e`）构建及官方 Mihomo 门禁通过，但 Integration 为 28/29；唯一失败发生在官方核心 smoke 退出后删除隔离目录，Windows 报 `cache.db` 仍被共享锁占用。Core 为 420/420。该次未发布 alpha。详见 [GitHub Actions run 80](https://github.com/arukas/ClashTray/actions/runs/35987533629)。
- 使用临时目录和 `FileShare.None` 合成锁在本机复现原始 `Directory.Delete` 失败，底层 HRESULT 为 `0x80070020`（sharing violation）。
- `tests/ClashTray.IntegrationTests/OfficialMihomoInteropTests.cs` 现对 Mihomo 官方测试临时目录清理，仅对 Windows sharing/lock violation（错误码 32/33）做最多 8 次重试，间隔 100–450 ms；其他 I/O 错误立即传播。新增合成锁回归通过（1/1）。此更改只影响测试夹具清理，不改变产品进程管理行为。
- 修正后完整 Release x64 build 0 警告/错误；固定官方 Mihomo 门禁 4/4、0 跳过；Core 420/420、Integration 30/30、0 跳过。
- GitHub master CI run 81（提交 `9199fc4`）成功；TRX 确认 Core 420/420、Integration 30/30、官方 Mihomo 4/4，失败与跳过均为 0。详见 [GitHub Actions run 81](https://github.com/arukas/ClashTray/actions/runs/35989136415)。
- `v0.3.3-alpha.6` 指向提交 `9199fc4`。Release workflow run 成功，官方核心、解决方案测试、压缩变体、资产上传及 Release 创建均通过；GitHub 上已发布为 prerelease，包含 7 项资产。详见 [Release run](https://github.com/arukas/ClashTray/actions/runs/35989808806) 和 [alpha.6 下载页](https://github.com/arukas/ClashTray/releases/tag/v0.3.3-alpha.6)。既有 `alpha.5` 保持不变。

## 验证记录

### 自动化构建和测试

- 完整 Release x64 解决方案构建：`dotnet build .\ClashTray.sln --configuration Release -p:Platform=x64 --no-restore`；通过，0 警告、0 错误。
- Core Release 回归：420 通过、0 失败、0 跳过。
- Integration Release 回归：新增共享锁清理回归后 30 通过、0 失败、0 跳过。
- 固定官方核心门禁：`packaging/Test-OfficialMihomo.ps1 -Configuration Release`；固定版本 `v1.19.31`，官方 Windows x64 归档 SHA-256 为 `38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67`，与 `packaging/mihomo-release.json` 一致。核心版本和 PE x64 检查通过；官方类别 4/4 通过、0 跳过。完整 Integration 随后在同一受控核心强制环境中通过。
- `git diff --check`：通过。
- 上述完整 Core/Integration 套件保留并运行了既有 F1–F4、N1–N5 测试保证；没有为其重复实施整改。A1–A3 仍只作为后续取证/测试设计建议，本轮未展开。

### 隔离验证

本轮 G1–G3 的正式回归分别使用 fake HTTP handler 与隔离临时路径、合成凭据与临时诊断目录、确定性分块 `TextReader`；没有对真实远端连接发送关闭命令。官方 Mihomo 用例使用 SHA-256 已验证的临时核心和测试配置运行。

### UI 与真实系统验收

- 本轮没有另行启动 WinUI Debug smoke，也没有手动点击真实应用界面；UI 目标传递经 Release 构建验证，Core 命令路由经 fake handler 验证。
- 未执行真实代理/TUN、服务安装、真实远端 endpoint 或真实连接清理，也未执行 Explorer、睡眠/网卡切换等系统验收。以上是遵守本轮限制后的未完成验收项。
- 首次远端 master CI 曾失败于上述 Windows 测试清理锁；修复后的 master CI run 81 和 alpha.6 Release workflow 均成功。自动化验证状态已完成，详见上方运行记录。

## 交付状态

已完成 G1–G3 代码修改、CI 清理竞态修正、Release x64 构建、官方核心强制门禁与完整回归；master CI 及 `v0.3.3-alpha.6` Release 已通过并公开发布。未安装服务或操作真实代理/TUN，未更改项目版本源文件。工作树中的既有无关文档继续保留。

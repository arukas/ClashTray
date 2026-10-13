# 0.3.6 发布后代码复审

2026-10-11（Asia/Singapore）。本轮已对结构重构的实际代码差异补做自审，确认并修复 1 项 P2 状态文案问题：核心验证配置时，面板误显示“已停止”。未发现可以确认的 P0/P1 缺陷。针对重点边界复跑的 Core 173 项与 Service 13 项用例全部通过、无跳过。此结论限于下面的审阅范围和证据；实际安装服务、真实网络切换及整机验收仍待完成。

这次审阅由本轮代码作者重新进行，未安排第二位独立审阅者。重构前的[结构审查](code-structure-review-2026-10-10.md)评估的是旧代码组织，实施过程的构建、测试和 UI smoke 也不能单独视为最终代码审核。本报告记录的是发布代码的另一次差异审阅、调用链检查及针对性验证。

## 审阅对象

- 基线：`f015e164190bf6eb682c2aa3952204deb1f66b49`。
- 发布提交：`caf2f7727a6e28cc887694d76cc85d1ee2c63e4a`，tag `v0.3.6-alpha.3`。
- [27 项逐项提交和完整差异](https://github.com/arukas/ClashTray/compare/f015e164190bf6eb682c2aa3952204deb1f66b49...v0.3.6-alpha.3)。
- 重点审阅新增模块、原调用方接线、取消/失败路径、持久化与回滚、发布入口及测试夹具组装。纯文件搬迁另外核对内容一致性。
- 审阅时跟踪工作区与发布提交一致，原有未跟踪文件保留；诊断和测试结果写入 ignored `artifacts`。

## 代码审阅结果

**R1 / P2：配置验证阶段误显示“已停止”，已修复。** `StartCoreCoreAsync` 会发布 `CoreState.Validating`，但 `PanelDisplayFormatting.CoreStateKey` 缺少对应分支，面板沿用默认的 `CoreStateStopped` 文案。此问题在基线的 MainWindow 格式化方法中已经存在，拆分后被保留，属于现有缺陷。补充 `CoreStateValidating` 映射及 zh-CN “验证中”、en-US “Validating” 资源，使文字与忙碌状态和状态点一致。改动只涉及展示，未改变启动和网络操作。

| 范围 | 检查内容及结论 |
| --- | --- |
| [共用启动就绪策略](../src/ClashTray.Core/Runtime/CoreStartupReadiness.cs)、[本地启动事务](../src/ClashTray.Core/Runtime/LocalCoreStartupTransaction.cs) | 检查控制器请求前后的进程/监听所有权、有效端口、TUN 关闭事实、重试上限、总体期限与失败清理。服务仍使用自己的观察源与 payload 校验。服务启动结果未知时，Runtime 进入核对流程，没有回退到本地重复启动。 |
| [服务请求台账](../src/ClashTray.Service/Dispatch/ServiceRequestLedger.cs)及[调度器](../src/ClashTray.Service/Dispatch/ServiceRuntimeController.cs) | 检查 RequestId/fingerprint、RecoveryOnly、服务实例约束、重复请求加入原任务、等待方取消、独立配额与 TTL。执行中的记录不会过期；完成时间与结果在同一锁内发布；状态查询、普通变更和已确认清理保留各自配额。 |
| [服务核心更新事务](../src/ClashTray.Service/Updates/ServiceCoreUpdateHandler.cs) | 对照原实现检查下载、最小配置校验、取消后的回滚及错误转换。调用仍处于调度器统一的生命周期/TUN 操作锁内，IPC 可执行路径和命令边界未扩展。 |
| [设置提交](../src/ClashTray.Core/Settings/SettingsCommitCoordinator.cs)、[配置切换上下文](../src/ClashTray.Core/Configurations/ConfigurationSwitchOperations.cs) | 检查恢复记录、字段合并、关闭优先意图、失败可见性和调用方操作准入。每次配置切换使用本次调用的固定 lease，应用与补偿阶段沿用同一个上下文；未发现旧的可变 ActiveLease 被保留或借用。 |
| [退出清理工作组](../src/ClashTray.Core/Runtime/ShutdownCleanupCohort.cs)及 Runtime 退出调用链 | 对照原流程检查等待期限与操作取消的区别、迟到任务观察、资源释放顺序和所有权转移。未完成的清理任务保留 operation lease 和 deadline token source，待任务结束后释放；前置任务仍使用资源时不会继续销毁运行时资源。 |
| [设置展示逻辑](../src/ClashTray.Presentation/Settings)、[命令边界](../src/ClashTray.Presentation/Commands/SettingsPageCommands.cs)及 WinUI 页面 | 检查提交草稿快照、保存期间的新编辑、页面代际、端点测试状态以及开关对已确认事实的投影。WinUI 控件、焦点、对话框仍在 App；Presentation 没有 WinUI 程序集依赖。 |
| [面板上下文](../src/ClashTray.Presentation/Panel/PanelSnapshotContext.cs)、App 托盘调用方 | 检查 endpoint identity/generation 与命令目标来自同一上下文。托盘使用本地设备事实，选中远程控制器的失败状态不会覆盖本机核心/系统代理/TUN 状态。 |
| [资源采样](../src/ClashTray.Core/Controller/ControllerResourceSampler.cs)、快照读取与发布 | 检查本地/远程原有取消语义、失败后的最后确认数据、JSON 文档释放、Provider 直接解析与诊断脱敏。Snapshot getter 不再排空待发布日志；发布边界显式排空日志，订阅回调在发布锁外执行。 |
| [环境组装](../src/ClashTray.Core/Runtime/RuntimeEnvironmentDependencies.cs)、[测试组装](../tests/Shared/RuntimeFixture.cs) | 检查公开生产构造入口、显式 isolated 工厂和所有旧测试调用方。测试默认的启动注册、服务访问及代理控制均隔离；伪所有权观察器只在测试程序集，生产组装不再依据 paths 是否为空隐式选择测试行为。 |
| 项目引用、目录与 [EXE 打包](../packaging/Build-EXE.ps1) | 检查依赖方向、Presentation 引用、SmokeTest 的 Release 排除路径及 XBF/PRI 相对目录复制。当前 Release 输出中的 App、Shell 和五个 Pages XBF 及 PRI 均存在。 |
| SDK、NuGet 锁文件与 [Release 测试入口](../packaging/Test-ReleaseSuite.ps1) | 检查锁定 restore/publish、Integration → Core 的顺序、TRX 新鲜目录与结果判定。缺少 TRX、零测试、跳过、失败和非零退出码均拒绝继续；错误摘要有长度限制并转义 workflow command 数据。 |

除 R1 外，本轮没有发现其他可以确认的缺陷。Runtime 的跨事实源编排、WinUI 页面的动态控件及有限内部测试 hook 仍存在，属于[实施记录](structure-optimization-results-2026-10-10.md)和[模块边界](module-map.md)已列明的维护空间。

## 本轮新增验证

| 项目 | 实际结果 |
| --- | --- |
| Core 针对性回归 | **173/173 通过，0 跳过**。覆盖启动就绪、服务未知结果核对、设置恢复、关闭优先、配置切换、退出超时、并发准入、资源采样、快照读取、展示上下文和架构边界。 |
| Service 请求/清理边界回归 | **13/13 通过，0 跳过**。覆盖重复请求、响应丢失、过期和服务实例变化、等待方取消、查询压力及清理保留配额。 |
| 基线到发布提交的纯搬迁 | **131 个 Git blob 完全相同**。其余搬迁伴随行为调整或文件拆分，按上述调用链及一致性检查审阅。 |
| OperationAdmission 拆分 | 6 个文件按原声明顺序重组后，除空白和重复 namespace 外与旧文件一致。 |
| StateContracts 拆分 | 7 个文件的声明行一致；11 个 enum 的成员顺序及 19 个 record 的参数顺序、默认值一致，协议版本仍为 3。 |
| Release XAML 输出 | 7 个 XBF 和 `ClashTray.App.pri` 存在；核对递归复制保留 `Shell/`、`Pages/` 相对路径。 |
| 发布入口故障注入 | MissingTrx、EmptyTrx、Skipped、Failed、Nonzero 五种输入全部拒绝继续；Passed 输入依次执行两个项目。失败摘要 `%`、CR/LF 转义检查通过。合成输入不计入实际测试通过数量。 |
| PowerShell 与 diff | 4 个相关包装/门禁脚本语法解析无错误；`git diff --check` 通过。 |

上表中的复跑使用 alpha.3 的 Release/x64 构建输出，`--no-build --no-restore`，在 R1 修复前完成。

R1 修复后的验证：

| 项目 | 实际结果 |
| --- | --- |
| 完整解决方案构建 | Release/x64 和 Debug/x64 均通过，0 警告、0 错误。 |
| Core 完整单元测试 | **779/779 通过，0 跳过**，包含中英文资源一致性和展示模块检查。 |
| 隔离 Integration 测试 | **37/37 通过，0 跳过**；本轮本地未重跑需要官方核心、TLS、受限令牌和 Windows ACL 的用例。 |
| 隔离 WinUI smoke | 退出 0，20 张 PNG、11 份 JSON。实际截图中的核心状态为“验证中”，观测 JSON 的 `CoreActionEnabled=false`；其余既有页面和交互检查通过。 |

为了观察 WinUI 控件，临时向既有 Debug smoke 加入一帧 `CoreState.Validating` 截图和状态 JSON；没有添加永久测试。编译诊断输出后按原 SHA-256 恢复 smoke 源文件，提交不包含临时探针。中文实际渲染已检查，英文资源随完整测试验证，但本轮没有英文 UI 截图。本轮没有执行真实安装、代理或 TUN 操作。

修复源码为 [`0ce364c`](https://github.com/arukas/ClashTray/commit/0ce364caab331430b00f9e3241fad4f64a4ca1a7)，与版本及发布说明的更新分别提交，交付版本递增为 [0.3.6-alpha.4](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.4)。发布提交为 `a166d8ef5bfd917acb959238d387df5b7055b144`。

本地证据位于 `artifacts/structure-optimization-20261010/final-review-20261011/`：`focused-test-summary.json`、`focused-core/review-core.trx`、`focused-service/review-service.trx`、`static-checks.json`、`script-parse-checks.json` 和 `release-suite-faults/checks.json`。R1 修复后的构建日志、TRX、截图和 JSON 在其中的 `validating-fix/`。这些诊断产物未提交到源码仓库。

## alpha.4 最终发布验证

[alpha.4](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.4) 于 2026-10-11 09:04:38（Asia/Singapore）发布，`prerelease=true`、`draft=false`。远端 master 和 annotated tag 的目标提交均已核对；之后的报告更新是单独的文档提交。

| 项目 | 远端实际结果 |
| --- | --- |
| [CI 38100201079](https://github.com/arukas/ClashTray/actions/runs/38100201079) | 成功。完整构建 0 警告/错误，Core 779/779、Integration 62/62、官方核心 19/19、安全能力 6/6，均无跳过。Core 行覆盖率 **80.47%**、分支覆盖率 **73.93%**，通过 70%/65% 门槛。 |
| [Release 38100203430](https://github.com/arukas/ClashTray/actions/runs/38100203430) | 成功。完整构建 0 警告/错误，官方核心 19/19、安全能力 6/6、Integration 62/62、Core 779/779 无跳过；Full 和 NoCET 打包、来源归档及发布步骤均通过。 |
| 公开资产 | 7 项，名称、非零体积和 GitHub SHA-256 摘要已检查。下载 2 个安装器 sidecar、发布清单和 SHA256SUMS，文件自身摘要与 GitHub 一致；sidecar、SUMS 中的安装器摘要与 GitHub 安装器摘要一致。未下载安装器或源归档，也未执行安装器。 |
| 来源清单 | `sourceCommit=a166d8ef5bfd917acb959238d387df5b7055b144`、`clashTrayVersion=0.3.6-alpha.4`；Mihomo v1.19.31、MetaCubeXD v1.273.1 的来源与固定摘要符合仓库清单。 |

Full 为 83,701,291 字节，SHA-256 `35be178ce4a8169ab2d3273cfd47028bc0e39dcdd29252069ad75daabb4c2e07`；NoCET 为 83,680,277 字节，SHA-256 `5944a4bb6b92fc1114053b32e7fc3669c6ac9423492582ceb8f11f2ff6f98424`。本节测试数字来自本次完整 Actions 日志；各门禁的独立运行有重叠，不能相加当作不同测试数量。

本地新增证据：`alpha4-delivery/{workflow-summary,github-release,release-verification}.json`、`alpha4-actions-38100201079/logs/` 和 `alpha4-actions-38100203430/logs/`，均位于上面的 ignored 诊断目录。

## alpha.3 已有发布证据和整机验收限制

[alpha.3](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.3) 已发布，`prerelease=true`、`draft=false`。

- [CI 38065956218](https://github.com/arukas/ClashTray/actions/runs/38065956218)与 [Release 38065960608](https://github.com/arukas/ClashTray/actions/runs/38065960608)成功。
- 发布前本地完整 Release/Debug 均为 0 警告、0 错误；Core 779/779、Integration 62/62、官方 Mihomo 19/19、安全能力 6/6 无跳过；Core 行/分支覆盖率 80.50%/74.02%。这些是实施和发布阶段的结果，独立于本轮的 186 项复跑。
- 7 项公开资产已核验，两个安装器 sidecar、SHA256SUMS 与 GitHub 资产摘要匹配；发布清单的源码提交为 `caf2f77`。公开安装器未在本机执行。
- 隔离 WinUI smoke 的既有证据为退出 0、19 张 PNG 和 10 份 JSON 检查；本轮未再次把它作为真实机器验收。

仍未完成的整机验收：实际安装服务的身份与生命周期、真实 System Proxy/TUN 切换和失败恢复、安装/升级/卸载、登录启动、睡眠恢复、适配器变化、Explorer 重启、多显示器/任务栏位置、物理输入、高 DPI、高对比度、辅助技术以及 Windows 10。这些限制保留在 alpha 发布说明中。

## 历史 Actions 失败诊断

用户更新令牌后，已读取首两次 Release 的完整日志，并下载 alpha.1 的官方门禁 TRX，确认以下实际失败点：

| 运行 | 已确认事实 | 证据限制 |
| --- | --- | --- |
| [alpha.1 / 38064180585](https://github.com/arukas/ClashTray/actions/runs/38064180585) | 官方门禁 18/19 通过，`ServiceReadinessTimeoutRetainsMissingUdpObservation` 失败于 `observedUdp` 断言，未到达本例期望的注入 UDP 观测。 | 日志和 TRX 只记录该断言及夹具目录，没有记录此时的 ServiceResponse 代码和错误文本，因此仍不能判定为何没有到达观测点，不能断言是核心启动时间或网络功能错误。 |
| [alpha.2 / 38065216699](https://github.com/arukas/ClashTray/actions/runs/38065216699) | Integration 62/62 通过；Core 774/779 通过，`OneTimeFailureUsesStableServiceCodeInsteadOfMessage` 的 5 个参数化用例均在夹具 `ReserveTestPort` 抛出“无法为运行时测试分配隔离端口”。源代码对应 32 次尝试后未找到符合互异、TCP/UDP 可绑定要求的端口。 | 发生在测试设置创建阶段；日志未保存候选端口、被占用/排除原因或 SocketError，不能据此确认是哪一种端口条件导致失败。 |

alpha.3 起发布入口依次运行 Integration 和 Core，避免两项目同时执行；本次 alpha.4 两条工作流的相关测试均通过。顺序执行及单次通过不能证明历史环境因素已彻底消除，因此本轮没有根据未复现的原因修改生产网络逻辑或放宽门禁。

历史证据在 `updated-actions-38064180585/{logs,official-results}/` 和 `updated-actions-38065216699/logs/`。两次失败 tag 和记录均保留。

用户明确授权使用工程 `.env` 内的 GitHub API key 排查后，首次及只延期后的尝试均返回 HTTP 401。早先的 `GET /user` 有效性检查被自动审批拒绝，理由是认证失败后继续测试同一令牌属于凭据探测；该检查没有执行。用户随后明确更新本机 `.env` 并要求再试，Actions 元数据、完整日志及失败 TRX 的认证访问成功，令牌导致的日志访问限制已解除。密钥未打印、未写入报告或提交，`.env` 被 Git 忽略。

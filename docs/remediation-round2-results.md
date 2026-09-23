# ClashTray 第二轮整改结果

日期：2026-09-24
实际起始基线：`51f141b`（与执行说明的评审基线一致）
工作区基线：执行前只有执行说明、历史评审报告两项未跟踪文档；它们均保留。未修改版本号、发布配置、真实代理/TUN/服务安装状态，也未生成安装包。

## 总览

| 项目 | 状态 | 结果摘要 |
|---|---|---|
| S1 | 已完成 | UI 关闭与进程退出回调统一经窗口 dispatcher；单飞、拒绝入队和回调异常都有正式测试。 |
| S2 | 已完成 | `UpdateSettingsAsync` 只接收字段 patch，在操作锁内基于最新设置合并并应用；表单只提交相对基线发生变化的字段。 |
| S3 | 已完成（故障边界明确） | 结构化退出结果、资源安全、服务/代理恢复责任及本地核心跨进程持久恢复记录均已实现并验证；记录无法安全读取或写入时保持未确认、跳过自动启动并如实报告。 |
| S4 | 已完成，性能边界见下 | 连接和日志使用稳定 row identity、增量协调和列表内部滚动。数据基准确认创建行数下降，但纯数据协调耗时更高；没有据此声称 UI 更快。 |
| S5 | 已完成必要提取 | 设置 patch 与退出结果/恢复责任已独立建模；`ClashTrayRuntime` 仍是 facade 和运行时所有者。 |
| S6 | 已完成受限转换器验证 | 语料先暴露并推动修复已确认的转换错误；能力范围保持受限，不依赖完整 YAML 解析器。两项真实官方核心集成测试通过。 |
| S7 | 已完成 | 未处理异常改为脱敏、同步、有界写入；所有文件写入/轮换共用单个进程内锁，失败在诊断边界内返回。 |

## S1：退出 UI 回调线程契约

**状态：已完成。** `ShutdownCoordinator` 通过异步 dispatcher 委托执行 `BeginShutdown` 和退出动作；`App.DispatchUiActionAsync` 在已有 UI 线程直接执行，否则使用 `DispatcherQueue.TryEnqueue` 并等待完成。未取得 dispatcher、入队被拒绝、UI 回调抛异常都转成明确 fault，不会留一个永不完成的等待任务。退出请求继续共享同一个 single-flight 任务。

关键文件：`src/ClashTray.App/ShutdownCoordinator.cs`、`src/ClashTray.App/App.xaml.cs`、`tests/ClashTray.Core.Tests/ShutdownCoordinatorTests.cs`。

正式回归测试：`ExitActionRunsOnOwnerThreadAfterAsynchronousCleanup`、`BackgroundQuitMarshalsBeginShutdownToOwnerThread`、`RepeatedQuitCallsSharePendingCompletionAndExitOnlyAfterCleanup`、`DispatcherRejectionCompletesSharedQuitWithExplicitFailure`、`UiCallbackExceptionCompletesQuitWithoutLeavingWaiterPending`。修复前的复现显示异步清理后退出 callback 落在非 owner 线程；定向测试修复后 5/5 通过，最终 Core.Tests 全量也通过。

设计边界：测试使用带专用线程和可控消息泵的 dispatcher 适配器，验证 owner-thread 契约与拒绝/异常传播；本轮没有启动 WinUI 窗口做真实 `DispatcherQueue` 关闭竞态验证。

## S2：设置字段合并与并发一致性

**状态：已完成。** 新增 `SettingPatchValue<T>` / `AppSettingsPatch`，能表达未提供、显式 `false`、零值和显式清空。运行时先取得现有 `OperationGate` 的操作 lease，再读取最新 `_settings`，应用 patch、验证、保存与应用；失败补偿使用同一锁内捕获的最新状态。主题、解锁项及 smoke-test 调用点改为单字段 patch。设置表单用打开时基线与拟提交值计算差异，并清除它不负责的配置选择、代理和 TUN 字段。

关键文件：`src/ClashTray.Core/AppSettingsPatch.cs`、`src/ClashTray.Core/ClashTrayRuntime.cs`、`src/ClashTray.App/SettingsPage.xaml.cs`、`src/ClashTray.App/MainWindow.Themes.cs`、`tests/ClashTray.Core.Tests/AppSettingsPatchTests.cs`、`tests/ClashTray.Core.Tests/RuntimeStateTests.cs`。

正式回归测试包括：`QueuedSettingsUpdatesPreserveChangesToDifferentFields`（使用 shared lease 确定性排队，分别修改刷新间隔和主题）、`QueuedUpdatesToSameFieldUseOperationOrder`、`ThemePatchPreservesActiveConfigurationProxyAndTunSettings`、`FailedQueuedSettingsApplicationRollsBackToLatestSettings`，以及 patch 对显式 null/false/0 和 `Diff` 的两个单元测试。修复前确定性复现曾把订阅刷新间隔 37 覆盖回旧值 24；最终全量 Core.Tests 387/387 通过。

设计偏差：没有添加 settings revision 冲突策略；明确 patch 的同字段命令由操作锁取得顺序决定，后一个成功写入覆盖前一个。

## S3：退出结果、资源安全与恢复责任

**状态：已完成；持久记录不可用时保持明确的未确认结果。** `ShutdownAsync()` 共享并返回结构化 `RuntimeShutdownResult`；现有 `IAsyncDisposable.DisposeAsync()` 等待同一 shutdown task。结果逐项记录操作安全点、TUN、System Proxy、核心、运行时资源、本地核心恢复记录及附加清理状态，并区分 `NotRequired / Completed / TimedOutUnknown / Failed`。默认总预算 30 秒；网络清理前置预算为总时限的三分之一且最多 5 秒。清理顺序为停止后台刷新/调度、quiesce 并取得操作安全点、等待 TUN 安全点、关闭 TUN、恢复代理、停止核心，之后才释放会话、transport 与运行时资源。网络请求在外层有限等待到期时不取消已接受操作；仍运行任务继续持有 operation lease 和其 CTS，资源不会提前释放。只有 TUN 状态被服务确认 Off 后才会请求停止服务核心。

本地核心路径补有版本化 `local-core-shutdown.json`。退出先写固定的 `.tmp` 暂存文件、限制记录不超过 4 KiB，再原子替换正式记录；记录只接受 `CorePathPolicy` 管理的 Mihomo 路径。进程身份由 PID、UTC 启动时间和完整映像路径组成。下一次 Runtime 初始化先读记录、核对版本及路径、仅在三项身份完全匹配时停止进程，再清理记录，且在该步骤之前不会自动启动核心。若上次进程恰好在记录完整写入、替换前退出，下次启动会校验并接续完整暂存记录；主记录与暂存记录同时存在时 fail closed，不猜测哪一个拥有进程。PID 被复用或映像路径不同的进程会被保留。恢复失败、记录损坏或歧义时会显示错误并跳过自动启动，不把状态改成已停止。

关键文件：`src/ClashTray.Core/RuntimeShutdownResult.cs`、`src/ClashTray.Core/ClashTrayRuntime.cs`、`src/ClashTray.Core/LocalCoreShutdownJournal.cs`、`src/ClashTray.Core/MihomoProcessManager.cs`、`src/ClashTray.Core/AppPaths.cs`、`src/ClashTray.App/App.xaml.cs`、`tests/ClashTray.Core.Tests/RuntimeDisposeTests.cs`、`tests/ClashTray.Core.Tests/LocalCoreShutdownJournalTests.cs`、`src/ClashTray.Service/ServiceRuntimeController.cs`、`src/ClashTray.Service/TunShutdownGuard.cs`。

正式故障测试覆盖：`DisposeUsesBoundedAdmissionWaitAndKeepsGateAliveForOutstandingReader`、`NoncriticalLogShutdownTimeoutDoesNotConsumeNetworkCleanupResult`、`UnknownTunResponseNeverReportsOffAndLateServiceResponseRetainsShutdownOwnership`、`UnavailableServiceDoesNotStopCoreWhileTunStateIsUnconfirmed`、`ProxyOwnershipConflictIsReportedAndPersistentJournalOwnsLaterRecovery`、`LocalCoreGateTimeoutPersistsJournalForNextRuntimeRecovery`、`RecoveryStopsOnlyExactPidStartTimeAndExecutableMatch`、`RecoveryPreservesProcessWhenPidWasReusedForAnotherImage`、`RecoveryRefusesProcessOutsideManagedMihomoPath`、`RecoveryPromotesCompleteStagedRecordAfterInterruptedAtomicReplace`、`RecoveryFailsClosedWhenMainAndStagedRecordsBothExist`、`IAsyncDisposableStillWaitsForTheSameStructuredShutdownTask`。S1/S3 定向集合实际通过 17/17；最终 Core.Tests 通过 387/387。测试在隔离目录中使用 fake pipe service、fake proxy controller、假恢复动作和专门启动的 `cmd.exe` 子进程；仅精确身份进程测试会终止该测试自身启动的子进程。未修改当前机器代理或服务。

恢复边界：若当前进程无法取得核心身份或持久记录因权限/磁盘错误无法写入，结果将为 `Failed`，核心仍可能继续运行；退出码为 2，且不会伪称已有持久恢复所有者。无法读取/验证 journal 时也不会杀进程或自动启动核心，需要用户处理该错误状态。非受管映像、PID 启动时间不匹配及主/暂存记录歧义均 fail closed。代理所有权 journal 仍由下次 app 恢复操作或既有 service recovery 入口处理，恢复时比较所有权并保留其他应用后来设置的值；服务 TUN 使用现有 allow-listed named-pipe `DisableTun` 路径，服务停止时仍执行既有 `TunShutdownGuard`。

未执行真实 WinUI Quit、实际 Service pipe、系统代理注册表或物理 TUN 场景；fake service 和启动的测试子进程不代表这些真实系统场景。
## S4：连接和日志列表增量刷新

**状态：已完成稳定身份与增量数据协调；UI 性能结论受限。** 新增泛型 `StableRowReconciler`，按 `ConnectionInfo.Id` 及 `LogEntry.Sequence` 缓存 row view model，对新增、删除、移动和内容变化分别协调。Connections 页面按稳定 ID 保持选中连接，更新详情；条目被过滤或删除时清除选择。日志采用独立递增序列号，重复行折叠保留原序号，清空后序号不复用，淘汰后相同时间/文本的新事件仍有不同 ID。列表页保留原搜索、排序、来源/级别过滤语义，不主动滚到日志底部。Connections/Logs 直接使用受限高度的 `ListView` 内部滚动容器，不再通过外层无界 `ScrollViewer` 测量列表；Rules/Settings 仍使用原滚动区域。

关键文件：`src/ClashTray.Core/StableRowReconciler.cs`、`src/ClashTray.Core/RuntimeListProjection.cs`、`src/ClashTray.Contracts/StateContracts.cs`、`src/ClashTray.Core/BoundedLogBuffer.cs`、`src/ClashTray.App/ConnectionsPage.xaml`、`src/ClashTray.App/ConnectionsPage.xaml.cs`、`src/ClashTray.App/LogsPage.xaml`、`src/ClashTray.App/LogsPage.xaml.cs`、`src/ClashTray.App/MainWindow.xaml`、`src/ClashTray.App/MainWindow.xaml.cs`。

正式测试覆盖行对象身份、删除语义、相同快照不产生额外通知、过滤/排序、日志折叠/清空/头部淘汰、同时间同文本的不同事件和同步投影端点切换。S4-T5 正式数据基准为 1,000 行 × 20 次：本次测得全量创建 20,000 行对象用时 0.52 ms；稳定行协调创建 0 行对象、更新已有行用时 7.06 ms。这个纯数据基准未创建 WinUI 控件；结果证明减少 row object churn，但协调器路径在此数据集上的纯 CPU 耗时更高，因此不宣称整体刷新更快或更低帧耗时。实际滚动、虚拟化、焦点和高 DPI 仍需真实 UI 测试。

## S5：按事务责任提取结构

**状态：已完成本轮必要提取。** 设置合并策略移入无 UI 依赖的 `AppSettingsPatch`；清理状态、责任类型和恢复责任生成移入 `RuntimeShutdownResult` / `RuntimeShutdownResultBuilder`。Core 不依赖 WinUI 或 Service 实现；dispatcher 留在 App 边界。`ClashTrayRuntime` 保留 public facade、组合和既有 OperationGate/session 所有权，未拆出需要十余个内部回调的“协调器”，也没有再添加第二套操作锁或状态源。

验收证据是 S2 patch 和 S3 structured shutdown 的 Core.Tests 可在 fake proxy/service 与隔离路径中独立运行；完整解决方案和公共调用路径均已构建/测试通过。本地核心恢复记录的读写故障边界列在 S3；该提取没有新增第二套并发或状态机制。

## S6：YAML 转换语法语料与边界

**状态：已完成受支持转换路径的测试和必要修复；不是完整 YAML 验证器。** 正式语料在修复前暴露带引号的受管 root key、带空白的 key/冒号、带嵌套/块标量的 TUN block、flow mapping 中嵌套集合及引号内逗号等转换问题。修复后的转换器只更新受管 root 字段和 `tun` 直接子项，保留嵌套同名字段、块标量文本、注释、代理组、规则和受管字段之外的 alias；不改写导入源文件。受管 `tun` 根值为 alias、或目标 flow map 不平衡时明确拒绝，不覆盖上次有效输出。没有引入 YAML 包或重写为通用 parser。配置商店另有 16 MiB 本地导入上限，新增大于该上限的隔离文件被拒绝且不创建 profile。

关键文件：`src/ClashTray.Core/RuntimeConfigBuilder.cs`、`tests/ClashTray.Core.Tests/RuntimeConfigBuilderYamlCorpusTests.cs`、`tests/ClashTray.Core.Tests/ConfigurationStoreTests.cs`。

语料测试 6 项、16 MiB 边界测试 1 项均在最终全量 Core.Tests 中通过（Core.Tests 总计 387/387）。转换器处理语料不等于 YAML 语法有效性证明；`ConfigurationStore.ValidateYaml` 仍是候选配置表层检查，最终语法接受与 Mihomo 语义由核心验证。系统现有 `%ProgramData%\ClashTray\core\mihomo.exe`。运行前核对了版本、官方发布 URL、归档 SHA-256 与仓库固定清单一致，并确认可执行文件 SHA-256 与安装清单一致、核心目录对当前用户只读；未下载/安装核心。`PinnedMihomoStartsWithTunDisabledAndServesLoopbackController` 和 `ServiceStartsRestartsAndStopsPinnedMihomoWithTunPermanentlyDisabled` 均通过。后者直接调用 `ServiceRuntimeController` 并使用假的网络健康探针，不代表实际 Windows Service/命名管道测试。YAML tags、复杂多文档语法等不在本转换器的保证范围内；本轮只为无法安全改写的受管 TUN alias 和不平衡 flow mapping 给出明确拒绝。

## S7：有界、脱敏诊断写入

**状态：已完成。** `App.UnhandledException` 不再直接创建无限追加的 `startup-error.log`，也不设置 `e.Handled = true`。新 writer 同步落盘，记录异常类型和经 `ErrorSanitizer` 处理的异常链；每条按最终 UTF-8 编码最多 16 KiB，最多 3 个轮转文件、每个不超过 1 MiB。所有目录写入共享一个静态锁；轮转和写入原子串行；追加前会截掉未换行的尾部残片。目录、文件、磁盘和 sanitizer 异常在 writer 边界内被转换成 `false`，不从原始未处理异常处理器二次抛出。

关键文件：`src/ClashTray.Core/BoundedDiagnosticWriter.cs`、`src/ClashTray.Core/ErrorSanitizer.cs`、`src/ClashTray.App/App.xaml.cs`、`tests/ClashTray.Core.Tests/BoundedDiagnosticWriterTests.cs`、`tests/ClashTray.Core.Tests/ErrorSanitizerTests.cs`。

新增正式测试用合成 URL 用户名/密码/query token、Authorization、普通密码字段、非 ASCII 长消息、嵌套异常、200 个并发写入、轮转、非法目录、占用文件及故意抛异常的 `Message` getter。writer 定向测试 5/5（含不完整尾行恢复）、writer+ErrorSanitizer 定向测试 8/8 均通过；最终全量 Core.Tests 387/387 通过。

偏差与限制：为保留用户既有日志数据，没有迁移或删除旧版本产生的 `startup-error.log`；该旧文件内容不在本轮处理。新格式的同步小记录执行磁盘 flush，文件/字节总量有上限，但底层文件系统的 I/O 等待时间由 OS 决定，没有额外硬实时上限。

## 最终验证记录

- `dotnet build ClashTray.sln --configuration Release --no-restore --property:Platform=x64 --property:Version=0.3.3-alpha.2 --verbosity:minimal`：通过，0 警告、0 错误。
- `dotnet test ClashTray.sln --configuration Release --no-build --no-restore --property:Platform=x64 --property:Version=0.3.3-alpha.2`：Core.Tests 387 通过，IntegrationTests 27 通过，均 0 失败、0 跳过（设置 `CLASHTRAY_MIHOMO_PATH` 使用已核验核心）。
- `dotnet build ClashTray.sln --no-restore -p:Platform=x64 -verbosity:minimal`：通过，0 警告、0 错误。
- `dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj --no-build --no-restore -p:Platform=x64 -verbosity:minimal`：387 通过，0 失败，0 跳过。
- `dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj --no-build --no-restore -p:Platform=x64 -verbosity:minimal`（设置 `CLASHTRAY_MIHOMO_PATH` 指向已核验核心）：27 通过，0 失败，0 跳过；其中官方核心测试 2 项通过。
- 官方核心信任核验：现有安装版本 `v1.19.31`，官方发布 URL 和归档 SHA-256 与仓库固定清单一致；实测 EXE SHA-256 与安装清单一致。两项官方核心测试通过。
- 真实 WinUI 退出/滚动/高 DPI、真实 Windows 服务 IPC、系统代理注册表、TUN、网络恢复、安装/升级/卸载：未执行。
- 用户指定的 `gpt-5.6-luna` / `max` 无法从当前会话工具中切换或核验；本记录不声称模型设置已满足。

## 交付检查

- [x] S1 dispatcher 线程、重复退出、拒绝及异常完成路径已验证。
- [x] S2 不同字段不丢失、同字段顺序明确、显式 false/null/0、回滚和主题保留已验证。
- [x] S3 结构化退出、资源安全及 service/代理/本地核心持久恢复路径已有正式故障测试；记录写入或验证失败时会报告未确认并跳过自动启动。
- [x] S4 稳定身份、增量行协调、选中详情、列表投影、日志序号/淘汰与数据基准已有证据；真实 UI 未验证。
- [x] S5 按事务责任提取已完成，没有新并发协调机制。
- [x] S6 受限语料、转换修复、不受管语义保留及输入上限已验证；官方 Mihomo 隔离核心测试通过；真实 Service pipe/TUN 未验证。
- [x] S7 合成敏感数据、最终编码限额、轮转、并发及异常隔离已验证。
- [x] 完整解决方案构建、全部单元测试、适用集成测试已运行；完整集成测试包含并通过官方核心用例。
- [x] 上一轮回归保证仍由完整 Core.Tests / IntegrationTests 覆盖并通过。
- [x] 未修改版本、真实网络代理/TUN/服务状态或发布包；初始未跟踪文档保留。

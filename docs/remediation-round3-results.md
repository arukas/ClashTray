# ClashTray 第三轮定向整改结果

日期：2026-09-24
基线：`master` / `bf5e69b` / `v0.3.3-alpha.2`

## 交付概况

F1–F4 均已完成实现，并已补充对应回归测试或故障注入证据。整套解决方案构建通过；Core 和 Integration 测试均全绿。测试使用已安装的官方 Mihomo Windows x64 可执行文件执行配置校验和 TUN 关闭的 loopback smoke test，没有安装服务或改动 Windows System Proxy、TUN、路由等机器网络状态。

O1–O4 未扩展成 Runtime 大规模拆分。F3 为维持选择身份所需的最小 UI 数据路径调整，是把 controller ID 和 controller generation 传到 Connections 页面。

## F1：损坏的退出恢复记录安全失败

**根因**

恢复记录的 `identity` 属性声明为非空。JSON 的 `identity: null` 或缺字段仍会反序列化为 `null`；验证表达式随后把它传给身份校验，可能抛出 `NullReferenceException`。异常发生在 Runtime 的其余初始化和自动启动判断之前，因此安全恢复记录错误可能中断初始化。

**修改**

- `src/ClashTray.Core/LocalCoreShutdownJournal.cs`：将记录身份改为可空，在访问进程身份前检查 null，并返回 `Succeeded=false`、保留恢复记录及明确错误详情。无效记录不会调用进程恢复回调。
- `tests/ClashTray.Core.Tests/LocalCoreShutdownJournalTests.cs`：覆盖 null、缺失 identity、未知版本、无效 PID/启动时间、缺失或空 executable path；逐项确认恢复回调次数为零且磁盘记录保持原样。
- `tests/ClashTray.Core.Tests/RuntimeDisposeTests.cs`：以有效活动配置和“自动启动”设置初始化 Runtime，同时种入 `identity: null` 记录；确认设置和活动配置仍加载、恢复错误可见、核心未启动且损坏记录未被删除。

**状态**

- 实现完成：是。
- 自动化验证完成：是；上述用例包含在 Core 全套 398 项通过结果中。
- 真实系统验收完成：否；未对任何真实进程执行恢复或终止操作。此限制符合本轮禁止改变真实服务/网络状态的要求。

## F2：受管 YAML 多行值完整替换或明确拒绝

**根因**

旧转换只移除受管根键所在的单行。块标量、折叠标量和跨行引号值的后续行会残留在配置中，可能形成错误 YAML 或把旧凭据/旧设置留在有效配置里。旧写入顺序在覆盖目标文件后才应用运行时 ACL；ACL 失败时，上次成功产物可能已经被新文件替代。

**修改**

- `src/ClashTray.Core/RuntimeConfigBuilder.cs`：按根节点完整边界移除受管项，覆盖块标量、普通缩进续行及完整闭合的多行引号标量；生成临时文件后先设置保护 ACL，再在同目录原子替换目标。无法安全判定的 anchor 定义、未闭合引号/flow 集合，以及不支持的 TUN 根值会给出明确 `InvalidDataException`，在写目标前退出。
- `tests/ClashTray.Core.Tests/RuntimeConfigBuilderYamlCorpusTests.cs`：验证 block/folded scalar 和多行引号字段整节点移除、邻近 root 内容保留；另以 anchor、未闭合 flow 集合和标量 TUN 覆盖失败路径，确认源文件和预先存在的最后成功产物内容都不变。
- `tests/ClashTray.IntegrationTests/OfficialMihomoInteropTests.cs`：新增使用 `RuntimeConfigBuilder` 产物调用官方 Mihomo `-t` 验证的集成测试，并确认源 YAML 未被修改。

转换继续生成 `external-controller: 127.0.0.1:<port>` 和空 `secret`，本轮没有改变 controller 空 secret 或端点能力。

**状态**

- 实现完成：是。
- 自动化验证完成：是；Core 测试通过，官方 Mihomo 对 builder 生成的多行字段替换配置校验通过。
- 真实系统验收完成：否；官方核心只在临时目录校验配置，没有加载用户配置、启动 TUN 或修改 Windows 网络设置。

## F3：连接身份边界、列表选择和关闭操作

**根因**

旧 parser 为缺失 ID 生成随机 GUID；空、重复和缺失身份因此可能混进 UI 列表。`StableRowReconciler` 在处理可见项时才发现重复键，发现前可能已更新缓存行。Connections 页以原始 connection ID 作为唯一 UI 键，切换 controller generation 后相同 ID 可能错误复用选择或行身份。

**修改**

- `src/ClashTray.Core/MihomoDataParser.cs`：在解析边界跳过空白、缺失、超长以及重复 ID，保留首个有效记录的原始 ID；不再造随机 ID。
- `src/ClashTray.Core/StableRowReconciler.cs`：在更改现有行或缓存之前校验来源键和可见投影键；遇到 null/重复键明确失败，行状态不被部分更新。
- `src/ClashTray.App/MainWindow.xaml.cs`、`src/ClashTray.App/ConnectionsPage.xaml.cs`：UI 行键包含 controller ID 和 generation；controller generation 改变时清空旧选择；选择同步使用 `try/finally`，避免异常后长期抑制选择事件。
- `tests/ClashTray.Core.Tests/MihomoApiCompatibilityTests.cs`：覆盖重复、空、空白、null、缺失 ID；验证刷新保留所选行身份并更新首个有效条目，关闭操作将同一个 ID URL 编码后发送至正确 endpoint。
- `tests/ClashTray.Core.Tests/StableRowReconcilerTests.cs`：验证重复来源键和重复投影键在改行前被拒绝，随后有效快照能恢复并复用原选中行。

**状态**

- 实现完成：是。
- 自动化验证完成：是；parser/reconciler/API 回归全部通过，WinUI 项目完整构建通过。
- 真实系统验收完成：否；未启动桌面应用或在真实 Connections 页执行排序、选择和关闭。桌面启动可能依据本机配置触发核心/服务操作；本轮约束禁止改变真实网络/服务状态，自动化测试没有替代该真实 UI 结论。

## F4：前置清理挂起、总预算和迟到任务资源依赖

**根因**

Runtime 先给退出准备步骤一段短期限，然后还用同一个前置期限等待 `OperationGate` 排空和 TUN 操作安全点。前置取消、远端刷新或订阅调度器一旦挂起，门控等待很快也会超时，System Proxy/Core 清理因此没有机会使用退出总预算中剩余的时间。单个未完成任务虽然有观察 continuation，但路径会立即返回，不能收集多个迟到任务并准确保留它们共同依赖的资源。

**修改**

- `src/ClashTray.Core/OperationAdmission.cs`：`BoundedCleanupStepRunner` 分开接收“等待期限”和“操作取消期限”，允许阶段等待超时后将仍在运行的工作交还调用方，同时让操作继续受退出总期限约束。
- `src/ClashTray.Core/ClashTrayRuntime.cs`：取消运行时工作、停止远端刷新和订阅调度器仍受短前置等待预算限制；阶段挂起后继续等待原有 `OperationGate`，并在剩余总期限内等待 TUN 安全点与执行网络清理。没有绕过或提前释放 gate。跟踪所有未完成步骤；在它们结束前不释放依赖资源和 cleanup lease。若准备步骤仍未结束，返回 `TimedOutUnknown`，保留资源并通过 continuation 观察所有迟到任务；Gate/TUN 安全点到总期限仍无法确认时，不请求代理/Core 清理，也不把未知状态记为成功。
- `tests/ClashTray.Core.Tests/OperationAdmissionTests.cs`：故障测试证明等待期限到期不取消仍在总期限内运行的操作，并在释放后观察其完成。
- `tests/ClashTray.Core.Tests/RuntimeDisposeTests.cs`：分别挂起“取消运行时工作”“停止远程端点刷新”“停止订阅调度器”，验证网络清理仍使用剩余预算、迟到步骤保持未知、网络操作不重复且资源状态不被迟到任务覆盖。另在 Gate 持有活动操作时验证代理/Core 清理不会提前执行或被报告成功。原有本地核心 Gate 超时恢复记录、日志挂起及 TUN 未确认测试仍保留并通过。

**依赖边界记录**

- 订阅调度最终会进入订阅刷新；实际配置/Core 修改受 `OperationGate` 保护，因此清理仍必须等它排空。
- 远端 endpoint 刷新只管理独立远程会话与读取型状态/日志，不操作本机 System Proxy、TUN 或核心；它挂起时不能成为跳过本机 Gate 的理由。
- Runtime cancellation 会通知后台 worker；具有本机状态副作用的已准入操作仍必须经过 Gate。迟到任务仍在运行时不释放其依赖的 Runtime 对象。
- System Proxy 原有 backup/ownership/transaction journals 仍用于识别 RestoreRequired 和后续恢复。本修复主要使用既有退出总预算；Gate 未能在总期限内安全排空时维持未知状态，未新增绕过门控的网络恢复动作。

**状态**

- 实现完成：是。
- 自动化验证完成：是；Core 故障注入与操作门控回归通过。
- 真实系统验收完成：否；未对真实服务、System Proxy 注册表、TUN 或路由执行启停/回滚操作。

## 最终验证记录

| 验证 | 结果 |
|---|---|
| `dotnet restore ClashTray.sln --verbosity minimal` | 成功，6 个项目恢复完成 |
| `dotnet build ClashTray.sln --no-restore --verbosity minimal` | 成功，0 警告、0 错误；包含 Contracts、Core、Service、App 和测试项目 |
| `dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj --no-restore --verbosity quiet` | 398 通过、0 失败、0 跳过 |
| `CLASHTRAY_MIHOMO_PATH=%ProgramData%\ClashTray\core\mihomo.exe dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj --no-restore --verbosity quiet` | 28 通过、0 失败、0 跳过；包括官方 Mihomo 多行配置校验和 TUN 关闭的 loopback smoke test |
| `git diff --check` | 通过，无 whitespace 错误 |

真实 UI、System Proxy 所有权冲突、真实服务重启和 TUN 路由验收本轮未完成，也未以 fake 测试替代其验收结论。

## 版本、工作树与后续发布

本轮没有更改版本、提交、推送、安装服务或发布。检查时 HEAD 是 `bf5e69b`，标签为 `v0.3.3-alpha.2`。原有三个未跟踪文档保持未修改：`docs/code-review-2026-09-23.md`、`docs/remediation-round2-2026-09-23.md`、`docs/review-round2-followup-2026-09-24.md`。本文件是本轮新增交付记录。

请求中“本轮不自行改版本、发布”和“后续发布 0.3.3 alpha”需要区分处理；若开始后续发布，需要先明确目标 alpha 标签/版本（当前指向 `alpha.2`），再进行独立的提交、推送和发布步骤。
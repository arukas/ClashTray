# ClashTray 操作状态与并发收敛 Agent Vibe

> 这是给编码 Agent 的专项执行稿，不是新的产品需求，也不自动授权实施。
> 只有用户明确说“按此文档修复 / 开始实施”等指令后，才可以修改功能代码。
> 用户最新明确指令优先；仓库根目录 `AGENTS.md` 始终是更高优先级约束。
> **用户安全红线：本轮绝对禁止操作真实 TUN。不得开启、关闭、切换、恢复或故障注入真实 TUN；不得借测试、验收或诊断之名触碰真实 TUN 网卡、路由或 DNS。**

- 日期：2026-09-18
- 指定模型：`gpt-5.6-luna`
- 推理强度：`max`
- 工作方式：单 Agent、按切片顺序实施；不要只给方案，不要在没有用户授权时改业务代码
- 范围：Core、Service、IPC、运行时状态、操作准入、退出收敛与相关测试
- 非范围：新产品功能、P2 功能、UI 重设计、技术栈替换
- 实机 TUN：明确排除；只允许不会接触真实 Service/TUN/网卡/路由/DNS 的 fake、mock、in-memory 或完全隔离测试
- 审查基线：完整解决方案构建 0 warnings / 0 errors；Core tests 270/270；Integration tests 21/21；合计 291 项
- 基线说明：以上数字只是 2026-09-18 审查现场，开工时必须在当前工作树重新验证，不能直接沿用

## 给 Luna Max 的执行指令

你正在修复一个真实 Windows 托盘代理客户端的并发正确性，不是在做命名整理，也不是给现有锁再包一层接口。

本轮目标是把“用户意图、操作准入、外部副作用、状态确认、结果提交、状态发布、退出清理”变成一条可证明、可取消、可超时、可恢复的闭环。完成后，快速点击、核心崩溃、Service 响应丢失、后台轮询、端点切换和应用退出不能再通过过时结果互相覆盖，也不能因为一个失败的后台发布任务让整个运行时永久等待。

必须使用 `gpt-5.6-luna`，reasoning effort `max`。实现时直接在仓库中工作，先写确定性失败测试，再做最小生产改动，使每个切片始终保持可构建、可测试。不要把整份文档一次性翻译成一个“大并发框架”。

## 一句话诊断

当前项目不是“完全没有抽象”：已有状态枚举、操作准入器、单飞/last-wins 辅助类、全局操作锁、发布节流器和 Service 边界。真正的问题是：

1. 抽象集中在锁和工具类，业务操作本身没有统一生命周期。
2. `ClashTrayRuntime` 的状态仍由多个异步来源直接读改写，immutable record 并不等于原子更新。
3. 进程内操作、跨进程请求和 UI 等待使用了同一类 CancellationToken 语义，取消后经常无法判断副作用是否已经发生。
4. 后台读取与设备写操作共享过粗的锁，产生锁车队、饥饿和“看起来像死锁”的长时间等待。
5. 退出清理仍然走普通准入路径，繁忙时可能静默跳过最重要的 TUN、System Proxy 和核心收尾。

审查没有证明一个经典 AB-BA 锁环死锁；不要把本轮简化为“找两把锁交换顺序”。已确认的是状态回退竞态、过时任务覆盖、锁车队、退出悬挂、后台 worker 终止后永久等待，以及跨进程未知结果。这些问题对用户的表现与死锁相似，但修复手段不同。

## TUN 安全红线

用户已经明确说明：操作真实 TUN 可能导致断网。因此本文中所有 TUN 修复、回归和故障注入都只能在代码层与隔离测试层完成。

严格禁止：

- 启动、停止、切换、恢复或重试真实 TUN。
- 向真实 Service 或真实 Mihomo controller 发送 `tun.enable=true` 或 `tun.enable=false`。
- 修改用户现有 TUN 偏好、活动配置或运行时副本来进行验证。
- 创建、删除、重置或探测后再修改真实 TUN 网卡、地址、路由、DNS 或 Wintun 状态。
- 启动可能根据用户已有 `TunEnabled` 偏好自动开启 TUN 的已安装 App/Service。
- 运行可能连接本机已安装 Service、真实 Mihomo 或真实网络栈的 TUN 测试。
- 为了获得“真实验收”而询问用户临时开启 TUN；本轮直接记为未实机验证。

允许：

- 阅读和修改 TUN 相关源代码。
- 使用 fake Service、fake controller、fake Windows health probe、mock pipe 和内存状态机。
- 运行已经通过源码确认不会连接真实 Service、不会启动真实 Mihomo、不会访问或修改真实网卡/路由/DNS 的隔离单元/集成测试。
- 编译所有 TUN 相关项目。

运行任一带有 Tun、ServiceInterop、OfficialMihomoInterop 或类似名称的测试前，先阅读 fixture/setup，确认它不会触达真实系统。如果无法证明完全隔离，就不要运行该测试，并在交付报告中写：

~~~text
真实 TUN：未验证（用户明确禁止操作，以避免断网）
~~~

这条红线优先于本文后续任何自动化、压力或真实 Windows 验收建议。

## 开工前必读顺序

1. 仓库根目录 `AGENTS.md`。
2. 本文。
3. `docs/architecture.md`。
4. `docs/tun-rapid-toggle-agent-vibe-2026-09-15.md`。
5. `docs/0.3.0/technical-design.md` 与 `docs/0.3.0/delivery-plan.md`；只读取和本轮状态/并发相关的部分。
6. 下列实现与现有测试：

   - `src/ClashTray.Core/ClashTrayRuntime.cs`
   - `src/ClashTray.Core/OperationAdmission.cs`
   - `src/ClashTray.Core/SnapshotPublishThrottle.cs`
   - `src/ClashTray.Core/EndpointSessionManager.cs`
   - `src/ClashTray.Core/ServicePipeClient.cs`
   - `src/ClashTray.Core/LocalDeviceCoordinator.cs`
   - `src/ClashTray.Core/MihomoProcessManager.cs`
   - `src/ClashTray.Contracts/StateContracts.cs`
   - `src/ClashTray.Service/ServiceCommandHost.cs`
   - `src/ClashTray.Service/ServiceRuntimeController.cs`
   - `tests/ClashTray.Core.Tests/RuntimeStateTests.cs`
   - `tests/ClashTray.Core.Tests/OperationAdmissionTests.cs`
   - `tests/ClashTray.Core.Tests/SnapshotPublishThrottleTests.cs`
   - `tests/ClashTray.Core.Tests/EndpointSessionManagerTests.cs`
   - `tests/ClashTray.Core.Tests/ServicePipeClientTests.cs`
   - `tests/ClashTray.Core.Tests/LocalDeviceCoordinatorTests.cs`
   - `tests/ClashTray.IntegrationTests/TunLifecycleTests.cs`
   - `tests/ClashTray.IntegrationTests/TunTransactionCoordinatorTests.cs`
   - `tests/ClashTray.IntegrationTests/BoundaryTests.cs`

冲突优先级：

1. 用户最新明确指令。
2. 根 `AGENTS.md`。
3. 正式产品/技术设计。
4. 本专项执行稿。

## 开工前保护现场

先执行只读检查并记录：

~~~powershell
git status --short
git branch --show-current
dotnet --info
rg -n "_snapshot\s*=\s*_snapshot\s+with|_operationLock|operationLockHeld|QueueSystemProxyRecovery|TryEnter|LatestWinsOperation|SingleFlightOperation|BooleanSingleFlight|RequestAsync" src tests
~~~

然后建立当前分支的真实基线：

~~~powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.Core.Tests\ClashTray.Core.Tests.csproj --configuration Debug --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.IntegrationTests\ClashTray.IntegrationTests.csproj --configuration Debug --property:Platform=x64 --no-restore
~~~

要求：

- 工作树不干净时，先识别用户已有修改，绕开并保留；不 reset，不 checkout 丢弃。
- 基线失败时，先判断是否由用户已有修改、环境或当前产品缺陷引起，并记录证据；不要悄悄改测试让基线变绿。
- 未经用户明确要求，不 commit、不 push、不建 PR、不发布。
- 真实 TUN 在本轮被明确禁止，不得请求或执行；System Proxy、服务安装/停止、路由、DNS、网卡或其他机器级状态变更仍需要用户明确授权。
- 完整 Integration Tests 命令只有在逐项确认 fixture 不会接触真实 TUN 后才能运行；否则只运行已确认隔离的测试集合，并精确报告排除项。

## 已确认风险与复现方向

以下不是“可能值得优化”，而是本轮必须被测试锁住的具体问题。行号基于审查时版本，代码变化后应以符号搜索为准。

### S0-1：核心重启后的过时 System Proxy 恢复会关闭刚重新启用的代理

调用链：

1. `RestartCoreCoreAsync` 在全局操作锁内先 Stop 再 Start。
2. Stop 进入 `UpdateCoreState(Stopped)`。
3. `UpdateCoreState` 调用 `QueueSystemProxyRecovery`，排入一个等待同一全局锁的后台恢复任务。
4. Restart 的 Start 成功，根据用户偏好重新启用 System Proxy。
5. Restart 释放全局锁。
6. 旧恢复任务随后获得锁，仍以 `coreRunning: false` 执行，关闭刚刚启用的 System Proxy。

审查锚点：

- `ClashTrayRuntime.cs`：`RestartCoreCoreAsync`，约 970–975 行。
- `QueueSystemProxyRecovery`，约 4837–4847 行。
- 实际撤销逻辑，约 4850–4885 行。
- `UpdateCoreState`，约 4910–4932 行。

根因：中间状态被当作异常核心丢失事件；恢复任务没有携带并复核 lifecycle epoch、进程 generation 和代理所有权 revision。

### S0-2：跨进程取消/超时后结果未知，却可能回退启动第二个本地核心

调用链：

1. `ServicePipeClient.SendAsync` 对连接、写请求和读响应都使用调用者 token。
2. 请求字节完整写入后，调用者取消或超时，客户端停止等待。
3. `ServiceCommandHost` 已经接收命令，使用 Service 自身 lifetime token 执行；客户端 token 不会撤销已发生的副作用。
4. 桌面端把某些 Timeout 当作“Service 没有执行”，并回退到本地进程路径。
5. Service 端核心可能稍后启动成功，桌面端又启动一个本地核心，造成端口冲突、未纳管进程或错误状态。

审查锚点：

- `ServicePipeClient.cs`：约 23–83 行。
- `ServiceCommandHost.cs`：约 149–176 行。
- `ClashTrayRuntime.cs` 启动路径：约 753–805 行。

根因：传输层没有区分 NotDispatched 与 UnknownOutcome；请求没有可用于去重、查询和对账的 RequestId；调用者取消错误地拥有了系统副作用的生命周期。

### S0-3：退出顺序允许关键清理因 Busy 被跳过

`DisposeAsync` 先尝试关闭 TUN、恢复 System Proxy、停止核心，之后才取消后台 runtime、scheduler 和 polling。与此同时：

- TUN 使用 `_operationLock.TryEnter`。
- 核心生命周期准入也可能使用 `TryEnter`。
- Dispose 捕获并忽略 Busy/异常。
- 后台轮询或其他操作仍可能占用准入/全局锁。

结果是退出看似完成，但 TUN、Service 核心或代理状态可能仍然保留。

审查锚点：

- `ClashTrayRuntime.cs`：`DisposeAsync` 约 2689–2775 行，后台取消约在 2736 行。
- TUN 入口约 2321 行。
- lifecycle admission 约 689 行。

根因：没有 Quiescing 阶段；退出清理被当作普通用户操作；没有独立于调用者取消的有界 cleanup token；失败被静默吞掉。

### S0-4：`_snapshot` 是多写者，旧读改写可以覆盖新状态

审查现场在 `ClashTrayRuntime.cs` 发现约 64 处 `_snapshot = _snapshot with ...`。同时：

- UI 命令、轮询、进程事件、端点事件、恢复任务和发布节流 worker 都可能异步运行。
- `Publish()` 自身仍修改 `_snapshot`。
- `SnapshotPublishThrottle` 从独立后台任务调用发布回调。

immutable record 只保证对象本身不可变，不保证“读取旧引用 → 生成新 record → 写回字段”是原子的。过时发布者可以把较新的 Core、TUN 或 System Proxy 状态整块写回旧值。

根因：没有唯一状态写者；状态提交与事件通知没有分离；没有全局 snapshot revision 和领域 generation 拒绝过时结果。

### S1-1：进程崩溃后，过时健康检查可能把 Failed 改回 Running

进程事件会写入 Failed，但没有立即使旧 controller binding 失效。已在途的 `RefreshCoreHealthAsync` 只核对 API 引用，不核对进程 generation/lifecycle epoch。旧健康响应随后可能写入 `_coreHealthConfirmed=true` 和 Running。

后台 polling 发现本地进程已不是 Running 时，可能只停止日志并 break，不提交 CoreLost。

审查锚点：

- `ClashTrayRuntime.cs`：`OnProcessStateChanged` 约 4834–4835 行。
- `RefreshCoreHealthAsync` 约 2795–2823 行。
- polling 分支约 3141–3152 行。

### S1-2：Service `GetStatus` 与 TUN 写命令并发，查询也会回写状态

`ServiceCommandHost` 允许多个管道客户端并发。`ServiceRuntimeController.GetStatusAsync` 绕过操作 gate，却读取并写入 `_tunState`、`_api` 和 `_activeCore`。一个较早开始、较晚完成的 status 查询可以在较新的 TUN 开启成功后把状态写回 Unknown/Off。

根因：查询不是纯观察；Service 端状态同样没有 revision/generation 约束的单写者。

### S1-3：全局操作锁把慢读取、远程 I/O 和设备写入绑在一起

已观察到：

- polling 持有全局 gate 跨越 API/网络调用和覆盖提交。
- 延迟测试可能持有同一 gate 数秒。
- endpoint 选择持锁等待远程首次刷新。
- lifecycle/TUN 采用 fail-fast admission，于是与无害读取冲突时返回 Busy。

这不是已证明的锁环，但会形成 lock convoy、饥饿、响应迟滞和退出清理失败。

### S1-4：现有 operation combinator 混淆调用者取消与共享工作的所有权

- `LatestWinsOperation` 可能保留已经取消的 pending intent，并用最终一次结果完成多批不相同的等待者。
- 后来的取消意图可能让较早、已经成功的调用者收到取消/失败。
- `BooleanSingleFlight` / `SingleFlightOperation` 可能让第一个调用者 token 控制共享系统操作；一个 UI 等待者取消就终止所有加入者依赖的工作。

根因：没有区分“取消等待结果”和“取消已经接受的系统副作用”。

### S1-5：Endpoint session 提交存在 TOCTOU

`EndpointSessionManager` 先 `IsCurrent`，再经过异步/锁窗口提交。旧 session 被 supersede 后，如果新 current 已经 Connected，旧操作可能依据全局 status 而不是本次 committed 标志返回一个未提交的旧 session。

审查锚点：`EndpointSessionManager.cs` 约 345–384 行。

### S1-6：发布节流 worker 一旦异常，后续请求可能永久等待

`SnapshotPublishThrottle` 的 `_publish` 回调异常会终止 worker。后续 `RequestAsync` 仍能放入 pending TCS，但再也没有 worker 完成它。如果调用点此时持有运行时操作锁，整个应用表现为死锁。

审查锚点：`SnapshotPublishThrottle.cs` 约 99–156 行。

## 必须建立的系统不变量

实现期间，任何局部设计都要能回答它维护了哪些不变量。最终至少满足以下约束。

### 状态不变量

1. 运行时 `AppSnapshot` 只有一个生产写入入口；最终生产代码不存在绕过 state store/reducer 的直接 `_snapshot = _snapshot with`。
2. `Publish` 只通知已经提交的快照，绝不修改领域状态。
3. 每个已提交快照都有严格单调递增的 `SnapshotRevision`。
4. Core、TUN、System Proxy、Controller session 至少各有能拒绝过时结果的 generation/revision；不要只靠对象引用相等。
5. 过时 I/O 结果可以被记录为 Superseded，但不能提交到当前状态。
6. UI 中的 On/Running 只来自 OS、Service 或 Mihomo 已确认的事实；用户偏好和“最后点击”不能冒充实际状态。

### 操作不变量

1. 每个会产生副作用的操作都有 `OperationId`、类型、目标、接受时的 epoch/generation、内部 deadline 和明确终态。
2. 用户 token 默认只取消该调用者的等待；操作一旦跨过不可逆/已分发点，必须继续对账到 Applied、Failed、RolledBack 或 UnknownOutcome。
3. latest-wins 只用于可以安全跳过中间值的选择操作。
4. TUN、核心 lifecycle、配置 apply、核心更新和退出清理不使用 latest-wins。
5. 后台读取不得长期持有设备 mutation gate；外部网络、文件或 IPC 等待不得发生在普通 monitor lock 内。
6. 不再通过 `operationLockHeld` 一类 bool 参数声明“相信调用者已经正确加锁”。

### IPC 不变量

1. 传输结果必须至少区分 NotDispatched、DispatchedAwaitingResult、Completed。
2. 请求完整写入 Service 后发生的超时、断连或调用者取消一律视为 UnknownOutcome，禁止直接启动本地 fallback。
3. Service 命令具有 RequestId；对可重试命令支持有界幂等去重或结果查询。
4. 启停、TUN 等命令的最终结果必须通过 Service/core 状态对账，而不是由“收到/没收到响应”推断。
5. App 与旧 Service、Service 与旧 App 的版本差异必须失败得明确；不能把协议不兼容解释成“Service 未执行”。

### 退出不变量

1. 退出先进入 Quiescing，拒绝新的用户写操作与后台刷新调度。
2. 先取消后台生产者，再等待/终止当前普通操作，最后执行最高优先级 cleanup。
3. cleanup 使用内部有界 token，不继承已经取消的 UI/Dispose 调用者 token。
4. TUN 关闭、代理恢复和 Service/core 停止不能因为普通 Busy 静默跳过。
5. 清理无法确认时必须记录并返回明确结果；不能空 catch 后假装安全退出。

## 推荐的最小目标结构

不要先造一个通用 actor framework。围绕当前缺陷建立三个窄组件，命名可按仓库风格调整，但职责不要混回 `ClashTrayRuntime`。

~~~text
UI / Tray / Scheduler / Process events
              |
              v
      Typed operation admission
      - operation id
      - conflict lane
      - epoch/generation
      - internal deadline
              |
              v
        External side effect
   Service / Mihomo / OS / storage
              |
              v
       Reconciliation result
   confirmed / failed / unknown / stale
              |
              v
     Single-writer state reducer
       monotonic revisions only
              |
              v
       Pure snapshot publication
      latest-only, failure-bounded
~~~

建议的窄类型：

- `RuntimeOperationContext`：OperationId、OperationKind、AcceptedSnapshotRevision、CoreLifecycleEpoch、ProcessGeneration、ControllerGeneration、Deadline。
- `OperationDispatchState`：NotDispatched、Dispatched、Completed。
- `OperationOutcome`：Applied、Superseded、Busy、CanceledBeforeDispatch、Failed、RolledBack、UnknownOutcome。
- `RuntimeStateCommand` 或等价 reducer message：只承载事实和条件提交元数据，不执行 I/O。
- `RuntimeStateStore`：唯一 snapshot owner，同步 reducer、异步通知。
- `RuntimeOperationCoordinator`：只负责准入、冲突和 quiescing，不负责业务 API 细节。

这些名字是建议，不要求机械照抄。禁止同时引入多套 coordinator、event bus、actor base class 和 command framework。一个组件只有在能消除现有重复竞态并有测试时才保留。

## 操作冲突矩阵

把现有“所有事情共用一个全局锁”替换为明确冲突关系。最终矩阵至少表达以下语义：

| 操作类别 | 策略 | 与谁冲突 | 提交条件 |
| --- | --- | --- | --- |
| Shutdown cleanup | 最高优先级、quiesce 后独占 | 所有新 mutation；等待或使旧 operation 失效 | 每一步真实确认或明确 Unknown/Failed |
| Core start/stop/restart | local-device 串行 | TUN、配置 apply、core update、shutdown | lifecycle epoch + process generation |
| TUN enable/disable/recovery | Service 单 owner、严格单飞 | lifecycle、配置 apply、core update、shutdown | Service + controller + Windows 状态 |
| Configuration apply/switch | 事务化串行 | lifecycle、TUN、core update、shutdown | journal/rollback + 新 generation |
| Core update | 事务化串行 | lifecycle、TUN、配置 apply、shutdown | 校验、原子替换、rollback |
| System Proxy 写入/恢复 | 独立 proxy lane，但由 lifecycle transaction 协调 | 其他 proxy 写入、shutdown；恢复需 epoch guard | OS 值 + ownership revision |
| Mode switch | endpoint-scoped latest-wins | 同 endpoint 的 mode mutation | endpoint generation + 定向确认 |
| Node switch | endpoint/group keyed latest-wins | 同 endpoint、同 group | endpoint generation + group identity |
| Delay test | node/group keyed single-flight | 只与同 key 合并 | endpoint generation |
| Endpoint select/connect | session-switch lane | 其他 endpoint switch、shutdown | 原子 committed 标志 + session generation |
| Metrics/logs/connections/providers | 并发只读、有界 | 不占 local-device mutation lane | generation check 后提交 |
| Snapshot publish | latest-only 通知 | 不与领域 mutation 竞争 | 已提交 snapshot revision |

如果实现发现某一行必须新增冲突，先写一条证明冲突的测试再调整矩阵。不要恢复为“一把锁最安全”。

## 测试编写规则

并发测试必须可确定复现，不依赖“多跑几次大概会撞上”。

- 用 `TaskCompletionSource`，并设置 `RunContinuationsAsynchronously`。
- 在 dispatch 前、请求完整写入后、外部响应到达前、commit 前提供窄测试 barrier。
- 用 `WaitAsync` 或测试框架 timeout 防止测试套件永久挂起；timeout 只保护测试，不替代生产同步条件。
- 不用固定 `Task.Delay` 安排线程交错。
- 可以加 20～100 次重复作为补充 soak，但必须先有 barrier 控制的单次确定性断言。
- 每个测试结束都断言 gate、pending waiter、worker、pipe/session 和 cancellation registration 已释放。
- 对操作结果断言具体 typed outcome，避免只断言“抛了某个 Exception”。
- 对状态断言 revision 单调、最终 confirmed state、旧 generation 未提交。
- 不为了让测试通过而放宽生产 timeout、增加无限 retry 或吞掉异常。

## 分批实施计划

每个阶段独立完成：先红测、再最小实现、跑相关测试、跑完整 Core/Integration tests、检查 diff。上一个阶段不稳定时不要叠加下一个大改。

### 阶段 0：冻结基线并建立竞态测试缝

目标：不改变用户行为，先让关键时序可控。

行动：

1. 画出当前 start/stop/restart、TUN、System Proxy recovery、polling、endpoint switch、snapshot publish、Dispose 调用链。
2. 列出 `ClashTrayRuntime.cs` 中所有直接 snapshot 写入，按来源分类，而不是立刻批量替换。
3. 复用现有 fake API/process/service；缺少时增加最窄的测试 seam。
4. 测试 seam 只暴露阶段 barrier 或 fake transport，不把生产内部锁公开成测试 API。
5. 记录当前完整基线数字和工作树状态。

先添加以下测试名或等价语义测试，使其在当前缺陷下稳定失败：

- `RestartCoreWithProxyPreferenceDoesNotRunStaleRecovery`
- `StartTimeoutAfterRequestWriteDoesNotFallbackToLocalCore`
- `CallerCancellationAfterDispatchReconcilesServiceState`
- `QuitWhilePollingStillDisablesTunAndStopsServiceCore`
- `ConcurrentPublishCannotRegressTunOrCoreRevision`
- `ProcessExitInvalidatesInFlightHealthResult`
- `ConcurrentGetStatusCannotOverwriteNewerTunState`
- `CanceledLatestPendingIntentDoesNotCancelPriorCaller`
- `SupersededEndpointCannotReturnAnUncommittedSession`
- `PublishCallbackFailureDoesNotLeaveFutureRequestWaiting`

完成信号：

- 每个缺陷都有确定性失败证据。
- 没有用 sleep 或压力循环假装复现。
- 除新增红测外，旧测试结果与基线一致。

### 阶段 1：先修跨进程“是否已执行”的语义

目标：从根上消除“响应没收到 = 命令没执行”的错误推断。

主要落点：

- `src/ClashTray.Core/ServicePipeClient.cs`
- `src/ClashTray.Core/LocalDeviceCoordinator.cs`
- `src/ClashTray.Contracts/StateContracts.cs` 或新增窄 IPC contract 文件
- `src/ClashTray.Service/ServiceCommandHost.cs`
- `src/ClashTray.Service/ServiceRuntimeController.cs`
- 对应 Core/Integration tests

必须实现：

1. 给每次 Service mutation 请求稳定的 RequestId。
2. 在 client transport 内显式跟踪 dispatch phase：

   - 尚未连接或未完整写入：NotDispatched。
   - 请求已经完整写入：Dispatched/UnknownOutcome 候选。
   - 完整响应已解析：Completed。

3. 调用者 token 在完整写入前可安全取消；完整写入后只取消该 waiter，不得把操作重解释成未执行。
4. Service 使用自己的有界 operation deadline 执行已接受命令，不由某个 UI waiter token 随意终止。
5. 为 lifecycle/TUN 等状态型命令增加有界去重或 request result/status reconciliation：

   - 同 RequestId 重试不能重复执行副作用。
   - 缓存必须有大小和 TTL 上限。
   - 进程/Service 重启导致结果缓存丢失时，通过真实 status 对账。

6. desktop fallback 规则：

   - 只有明确 NotDispatched 且现有产品策略允许本地 fallback 时才可 fallback。
   - Dispatched 后 timeout、断连、取消一律禁止直接 fallback。
   - 先查询/对账 Service/core；无法确认时返回 UnknownOutcome 和可行动错误。

7. 如果 contract 变化，增加版本兼容测试。升级过程中的旧 App/新 Service 或新 App/旧 Service 必须明确拒绝或安全降级，不能误启动第二个核心。

必须通过：

- 请求完整写入前取消：副作用没有执行，可按现有策略 fallback。
- 请求完整写入后取消：Service 最多执行一次；调用者取消等待不产生第二个核心。
- 响应丢失：重试相同 RequestId 不重复执行；最终通过状态对账。
- Service 执行成功但 client timeout：runtime 不启动 local core。
- UnknownOutcome 不被映射成 Stopped/Failed 后立刻触发破坏性恢复。

完成信号：代码中任何 local fallback 都能证明发生在 NotDispatched 阶段。

### 阶段 2：引入 lifecycle epoch，修复过时恢复与崩溃回写

目标：任何核心状态相关异步结果只能提交到它所属的生命周期。

主要落点：

- `ClashTrayRuntime.cs`
- `MihomoProcessManager.cs`
- `RuntimeStateTests.cs`
- `MihomoProcessManagerTests.cs`
- System Proxy ownership tests

必须实现：

1. 每次 start/stop/restart/config apply/core replacement 都推进明确的 CoreLifecycleEpoch。
2. 进程实例有 ProcessGeneration；controller binding 同时绑定该 generation。
3. intentional stop/restart 的中间 Stopped 不产生“异常核心丢失”恢复。
4. System Proxy recovery 只由明确的 UnexpectedCoreLost 事实触发，并携带：

   - lifecycle epoch；
   - process generation；
   - proxy ownership revision；
   - 触发时的 confirmed core state。

5. recovery 真正执行前重新核对：

   - epoch/generation 仍是当前；
   - 当前核心没有恢复 Running；
   - proxy ownership 仍属于触发时的 ClashTray 写入；
   - 没有更新的用户 proxy 意图。

6. 任一条件不成立时返回 Superseded/no-op，不改变 OS proxy。
7. 进程退出时先原子使 controller binding 失效，再发布 CoreLost/Failed。
8. 健康检查和 polling 捕获 binding generation，提交前复核；旧结果不得把 Failed 改回 Running。
9. polling 发现本地进程不再运行时必须提交 CoreLost 事实，不能只停止日志并 break。

必须通过：

- restart 中间 Stopped 不排入 destructive recovery。
- restart 成功并重新启用代理后，旧 recovery 即使晚到也不关闭代理。
- 真正意外崩溃且 ownership 未变化时仍能安全恢复代理。
- 崩溃前发起、崩溃后返回的 health success 被拒绝。
- 新进程已启动时，旧进程 exit/health 事件不能改变新状态。

完成信号：Core 状态转换和 proxy recovery 都能指出所属 epoch/generation。

### 阶段 3：建立可证明的 Quiescing 与退出收敛

目标：退出是最高优先级状态转换，不是“再试三个普通命令”。

主要落点：

- `ClashTrayRuntime.cs`
- operation coordinator/admission 组件
- subscription scheduler、polling、endpoint/session 的停止入口
- `RuntimeStateTests.cs`
- TUN integration tests

建议顺序：

1. 原子地把 runtime admission 改为 Quiescing。
2. 新的 UI mutation、scheduled refresh、polling tick、endpoint refresh 立即被拒绝或不再调度。
3. 取消后台生产者并等待它们有界退出；不要先等待它们自然走到下一次 delay。
4. 等待当前普通 mutation 在 deadline 内到达安全点；超时则推进 epoch，使其后续结果无法提交。
5. 使用独立 cleanup context 执行：

   - Service owner 的 TUN disable/reconcile；
   - owned System Proxy restore；
   - service/local Mihomo stop；
   - session、WebSocket、日志和 pipe dispose；
   - 最终状态发布。

6. cleanup 不能经过返回 Busy 的 `TryEnter` 普通入口；它应由 coordinator 的 quiescing owner 执行。
7. 每一步有独立 timeout、typed result 和日志；后一步是否继续由安全策略决定。
8. Dispose 可保持幂等；并发两次 Dispose 共享同一 cleanup task。
9. 清理失败不能空 catch。向上返回/记录聚合后的脱敏结果，并确保剩余资源仍尽力释放。

必须通过：

- polling 正持有慢 API 时退出：退出不会永久等，TUN 与核心 cleanup 仍被调用。
- TUN operation 正在进行时退出：不会启动相反命令队列；最终安全 Off 或明确 Unknown/Failed。
- 两次并发 Dispose：每个副作用最多执行一次。
- 调用者 token 已取消：内部 cleanup 仍按自己的 deadline 运行。
- publish worker 已失败：退出不会因等待 publish 永久卡住。

以上 TUN/退出测试全部使用 fake Service、fake controller 与 fake Windows probe；不得连接真实 TUN。

完成信号：代码中不存在“Dispose 里 TryEnter 失败后吞掉并继续假装完成”的路径。

### 阶段 4：把 runtime snapshot 收口为单写者

目标：消除 `_snapshot` 的多线程读改写覆盖，同时不把外部 I/O 塞进 reducer。

主要落点：

- `ClashTrayRuntime.cs`
- 新的窄 `RuntimeStateStore` / reducer 文件
- `SnapshotPublishThrottle.cs`
- `RuntimeSnapshotAdapterTests.cs`
- `RuntimeStateTests.cs`
- `SnapshotPublishThrottleTests.cs`

迁移方法：

1. 先建立一个拥有当前 snapshot 和 revision 的 store。
2. reducer 接收同步、小型、typed state commands；它不执行 HTTP、IPC、文件或 Windows API。
3. I/O 操作在外部完成后提交带 expected epoch/generation/revision 的事实。
4. reducer 在单写路径检查条件、生成新 snapshot、递增 revision。
5. 通知发生在提交之后、状态锁之外；订阅者异常不能回滚已提交状态。
6. 按领域逐批迁移直接写入：

   - Core lifecycle/health；
   - TUN；
   - System Proxy；
   - endpoint/controller session；
   - metrics/connections/logs/providers；
   - settings/config metadata。

7. 每迁移一类就删除该类旧直接写入，禁止长期保留“双写兼容”。
8. 最终使用搜索证明生产代码只剩 store 内一个 snapshot assignment。
9. `Publish()` 改成纯通知或移除；不得再修改状态。

`SnapshotPublishThrottle` 必须同时修复：

- 单次 publish callback 异常由 fault sink 记录，不让 worker 无声死亡；或 worker 进入显式 terminal-fault 状态。
- 无论采用哪种策略，未来 `RequestAsync` 必须继续可用或立即失败，绝不能返回永不完成的 task。
- worker 终止/dispose 时，所有 pending TCS 必须完成、取消或失败。
- callback 在 state lock 之外执行。

必须通过：

- 两个 barrier 控制的并发提交不能让 snapshot revision 回退。
- 旧 TUN/Core/Proxy 结果被 reducer 拒绝，不污染其他字段。
- 高频 metrics/log 更新不能覆盖 lifecycle 状态。
- 订阅者抛异常后，后续 RequestAsync 有界完成或 fail-fast。
- Dispose 与 pending publish 并发时无 orphan waiter。

完成信号：

~~~powershell
rg -n "_snapshot\s*=\s*_snapshot\s+with" src\ClashTray.Core
~~~

搜索结果只能位于唯一 state store/reducer 的内部实现，或为零。

### 阶段 5：修复 Service 状态 owner，并拆掉全局锁车队

目标：Service 查询不再回写新状态，后台读操作不再阻塞设备 mutation。

主要落点：

- `ServiceRuntimeController.cs`
- `ServiceCommandHost.cs`
- `ClashTrayRuntime.cs`
- operation coordinator
- `TunLifecycleTests.cs`
- `TunTransactionCoordinatorTests.cs`
- `BoundaryTests.cs`

Service 侧要求：

1. TUN lifecycle 只有一个写 owner。
2. `GetStatusAsync` 优先成为纯观察：读取本地 snapshot/真实状态后返回，不直接覆盖 lifecycle 字段。
3. 如果 status 必须触发 reconciliation，把 observation 作为带 generation/revision 的消息交给同一写 owner，不能查询线程直接写。
4. Service core/TUN 状态也使用单调 revision；旧 probe 不能覆盖新 command 结果。
5. pipe 并发客户端数量不是状态并发模型；必须有明确 owner。

Runtime 锁拆分要求：

1. polling、metrics、logs、connections、rules、providers 不占 local-device mutation lane。
2. 读取捕获 endpoint/process generation，I/O 完成后条件提交。
3. 延迟测试使用 node/group keyed single-flight，不持有设备全局锁。
4. endpoint 首次连接/刷新使用 session-switch lane，不持有 local-device lifecycle lane。
5. lifecycle、TUN、config apply、core update 保持严格冲突，不因拆锁而允许交叉。
6. System Proxy lane 的写入由 ownership revision 保护，并与 lifecycle transaction 明确协调。
7. 删除 `operationLockHeld` bool 协议；通过 typed operation context/lease 或唯一内部入口表达所有权。
8. 不允许 nested acquisition 顺序靠注释约定；如果确实需要多 lane，coordinator 必须以固定顺序一次性准入。

必须通过：

- 5 秒 delay test 在途时，core stop/restart 不因无关全局锁直接 Busy。
- 慢 metrics API 不阻塞退出准入。
- lifecycle 与 TUN 仍不能交叉。
- concurrent GetStatus 不能把已确认 On 写回 Unknown。
- endpoint 远程首次刷新不阻塞本地 TUN/System Proxy cleanup。

完成信号：全局锁不再跨越无关远程读取；冲突行为可以由矩阵测试解释。

### 阶段 6：修正 operation combinator、endpoint commit 与等待者语义

目标：复用工具本身不再制造跨调用者取消和错误完成。

主要落点：

- `OperationAdmission.cs`
- `EndpointSessionManager.cs`
- `OperationAdmissionTests.cs`
- `EndpointSessionManagerTests.cs`

Latest-wins 要求：

1. 每个 intent 有独立 ID、目标、waiter 和结果。
2. pending intent 被更新目标覆盖时，旧 waiter 得到 Superseded，不得收到后来目标的 Applied。
3. pending waiter 取消时，从 pending 集合移除或只取消该 waiter；不会取消 in-flight operation。
4. 如果多个 caller 请求完全相同目标，可显式 join；不同目标不能共享同一个成功结果。
5. 所有 TCS 使用 `RunContinuationsAsynchronously`。
6. completion、cancellation registration 和 pending slot 更新在同一同步边界完成。

Single-flight 要求：

1. shared operation 使用 coordinator/internal lifetime token。
2. 每个 caller token 只控制自己的 await。
3. 最后一个 waiter 离开是否取消底层工作必须是该操作类型的显式策略；TUN/lifecycle 默认不能因此取消已接受副作用。
4. 同 key join 与不同 key 独立必须有测试。

Endpoint commit 要求：

1. 每次创建 session 记录 selection generation。
2. 在一个同步边界内同时检查 current generation、写入 current session、设置本次 `committed=true`。
3. 返回值只依据本次 committed flag，不依据全局 status 是否 Connected。
4. 未提交 session 无条件 dispose，并返回 Superseded/null typed result。
5. 旧 session 的状态/事件在 supersede 后不能发布到新 endpoint。

必须通过：

- 后来的 canceled pending intent 不改变较早成功 caller 的结果。
- 20 个 mode/node 意图最终只应用允许的首个在途与最后目标，所有中间 waiter 得到明确 Superseded。
- 一个 single-flight waiter 取消不会取消另一个 waiter 的共享操作。
- barrier 强制发生 endpoint supersede 时，旧 session 永远不会作为成功返回。
- 未提交 session 和 cancellation registration 无泄漏。

完成信号：每个调用者得到属于自己 intent 的结果，不再“最后一次结果完成所有人”。

### 阶段 7：全量回归、压力验证与文档收口

自动化门禁：

~~~powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.Core.Tests\ClashTray.Core.Tests.csproj --configuration Debug --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.IntegrationTests\ClashTray.IntegrationTests.csproj --configuration Debug --property:Platform=x64 --no-restore
dotnet build ClashTray.sln --configuration Release --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.Core.Tests\ClashTray.Core.Tests.csproj --configuration Release --property:Platform=x64 --no-restore
dotnet test tests\ClashTray.IntegrationTests\ClashTray.IntegrationTests.csproj --configuration Release --property:Platform=x64 --no-restore
~~~

执行 Integration Tests 前必须审查 fixture/setup。只有确认测试不会连接本机已安装 Service、不会启动真实 Mihomo、不会读写真实 TUN/网卡/路由/DNS 时，才可以运行完整 Integration Tests 命令。否则：

1. 只运行已证明完全隔离的测试类或 category。
2. 禁止通过临时改用户配置、停服务或关 TUN 来“创造安全测试环境”。
3. 在报告中列出未运行测试及原因，统一标记为用户安全红线，不把它们写成通过。

如果解决方案配置支持直接完整测试，再补：

~~~powershell
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64 --no-build
dotnet test ClashTray.sln --configuration Release --property:Platform=x64 --no-build
~~~

压力/故障注入至少覆盖：

1. 100 轮 start/stop/restart + stale health response。
2. 100 轮 mode latest-wins 和同组 node latest-wins。
3. publish callback 周期性抛异常，未来请求仍有界完成。
4. Service 在“收到请求后、响应前”断开连接。
5. App 在 shutdown 时同时遇到慢 polling、endpoint refresh 和 TUN operation。
6. 进程 generation 快速更替时注入旧 exit、旧 health、旧 log 事件。
7. concurrent Service GetStatus 与 TUN enable/disable。
8. cancellation registration、worker、pipe、session 和 background task 数量不随循环增长。

真实 Windows 的非 TUN 验收只在得到用户对相应机器级操作的明确授权后执行：

1. 正常启动、停止、重启核心，确认不会产生第二个 Mihomo。
2. System Proxy 开启状态下重启核心，确认重启后仍按偏好和 ownership 保持正确状态。
3. 核心意外崩溃，确认 owned proxy 恢复，但外部应用改过的 proxy 不被覆盖。
4. 使用明确强制 TUN 关闭、且不会连接真实 TUN 的安全配置检查 App 退出；System Proxy、Service/core 只在各自获得授权时验证。
5. Service 重启和 App 重启后重新查询真实状态，不采用旧 snapshot。
6. 睡眠/恢复和网络变化期间不出现旧 generation 回写。
7. 不执行任何真实 TUN、TUN 路由或 TUN DNS 验收。

真实 TUN 始终明确写“未验证（用户明确禁止操作，以避免断网）”。其他实机项无法安全执行时也写“未验证”，不能把 fake、mock 或合成 smoke 写成真实 Windows 通过。

## 禁止用这些方式“修复”

- 给全局锁再换一个名字，继续让所有读写排队。
- 只增加 `lock (_snapshot)`；snapshot 引用会变化，也不能解决跨 await 和过时 I/O。
- 只把字段改为 `volatile` 或 `Interlocked.Exchange`；这不能让复合状态转换和代际校验正确。
- 通过增加 1～10 秒 `Task.Delay` 降低复现概率。
- 把 timeout 调得更长，或使用无限 timeout/无限 retry。
- 在请求可能已分发后直接 fallback 到本地核心。
- 让 UI caller cancellation 取消已被 Service 接受的设备操作。
- 在 Dispose 中继续 `TryEnter`，失败后空 catch。
- 让 GetStatus、health probe 或 polling 直接回写共享状态。
- 在 reducer/state lock 内执行 HTTP、IPC、文件、日志 callback 或 UI 事件。
- 为每个领域新建一把锁，然后依赖口头约定锁顺序。
- 用一个 bool 同时表示用户期望、操作进行中和真实确认状态。
- 只在 UI 禁用按钮；Core/Service 边界仍可并发进入。
- 吞掉异常、只返回 false，或把 UnknownOutcome 映射成普通 Failed。
- 一次性重写整个 `ClashTrayRuntime`，让回归 diff 无法审查。
- 为了本轮顺手实现 Wi-Fi/SSID、Remote 扩展、ARM64、历史流量、账号、遥测或其他 P2 功能。
- 改变 Mihomo controller 的 loopback-only 与显式空 secret 要求。
- 本轮以任何理由操作真实 TUN，或运行可能自动操作真实 TUN 的 App/Service/测试。
- 未经授权操作 System Proxy、服务、路由、DNS 或其他机器设置。

## 范围与兼容性边界

本轮允许：

- 新增少量窄 Core/Contracts 类型。
- 调整 App ↔ Service IPC envelope，并补版本兼容。
- 把 Runtime/Service 的状态写入迁移到单 owner。
- 删除已被新 coordinator/store 取代的重复 bool、lock 和 helper。
- 增加确定性并发测试、故障注入 fake 和必要日志。
- 更新 architecture 与本专项状态记录。

本轮不允许：

- 改 WinUI 技术栈、打包策略或 Service 信任边界。
- 开放任意 executable、shell、路径写入或网络管理 IPC。
- 改变用户配置格式，除非新字段确实需要持久化且有 versioned migration。
- 修改用户源 YAML 来绕开状态问题。
- 引入大型第三方 actor/reactive/concurrency 框架；优先使用 .NET BCL。
- 扩大 0.3.0 或 AGENTS.md 的产品功能范围。

如需新增持久化或 IPC 字段：

- 使用 versioned DTO。
- 老版本缺字段时有安全默认值。
- 未识别版本明确失败。
- 不记录 secret、订阅 URL、Authorization、完整 YAML 或节点凭据。

## 可观测性要求

新增结构化日志必须能在不泄密的情况下重建操作时序：

- OperationId / RequestId。
- operation kind 与 target 的脱敏标识。
- accepted snapshot revision。
- lifecycle epoch、process generation、controller/session generation。
- dispatch phase。
- admission、dispatch、response、reconcile、commit、rollback 各阶段耗时。
- outcome：Applied、Superseded、Busy、CanceledBeforeDispatch、Failed、RolledBack、UnknownOutcome。
- shutdown cleanup 每一步结果。

禁止记录：

- controller secret。
- subscription URL 与 query。
- Authorization header。
- 节点凭据。
- 完整配置/YAML。
- 不必要的用户绝对路径。

高频日志必须有界、可折叠或采样，不能因为修并发而制造新的日志洪水。

## 每个阶段的自检问题

动代码前后都回答：

1. 这个操作的唯一 owner 是谁？
2. 它何时算 accepted，何时跨过 dispatch point？
3. caller 取消时，是取消等待还是取消副作用？
4. 外部响应回来时，凭什么证明仍属于当前 epoch/generation？
5. 最终状态由谁确认：UI、Runtime、Service、Mihomo 还是 Windows？
6. 失败后机器处于什么网络状态？
7. 退出已经开始时，新请求能否进入？
8. callback/worker 抛异常后，谁完成所有 pending waiter？
9. 这个读操作为什么需要 mutation gate？
10. 测试是否用 barrier 精确制造了错误交错？

任何一题答不清楚，先补状态图或失败测试，不要继续堆锁。

## 完成定义

以下条件必须全部满足，才能说本轮完成：

- [ ] restart 不会执行过时 System Proxy recovery。
- [ ] 请求发送后丢失响应不会启动第二个 Mihomo。
- [ ] caller cancellation 与系统操作 cancellation 已分离。
- [ ] shutdown 会先 quiesce，再有界清理；关键清理不会因 Busy 静默跳过。
- [ ] Runtime snapshot 只有一个生产写入入口，revision 严格单调。
- [ ] 旧 process/controller/session generation 的结果无法提交。
- [ ] 进程崩溃后旧 health success 不能恢复 Running。
- [ ] Service GetStatus 不能覆盖更新的 TUN state。
- [ ] 无关读取不再持有 local-device mutation gate。
- [ ] latest-wins 与 single-flight 为每个 caller 返回正确结果。
- [ ] superseded endpoint 永远不会返回未提交 session。
- [ ] snapshot publish worker 故障不会留下永久等待。
- [ ] 新增并发测试全部使用确定性 barrier，而不是依赖 sleep。
- [ ] Debug/Release 完整 build 与 Core/Integration tests 通过，0 warnings / 0 errors。
- [ ] 非 TUN 的真实 Windows 项目要么完成并记录证据，要么逐项明确“未验证”及原因。
- [ ] 真实 TUN 固定报告为“未验证（用户明确禁止操作，以避免断网）”，没有发送过任何真实 TUN 命令。
- [ ] git diff 只包含本轮必要改动，用户已有修改完整保留。
- [ ] architecture/专项文档反映最终实际设计，不写尚未实现的承诺。

“编译通过”“快速点击没复现”或“加锁以后测试绿了”都不构成完成。

## Agent 完成回报格式

交付时严格按以下顺序向用户汇报：

1. 用户可见结果：哪些卡死、状态冲突、重复核心或退出残留已消除。
2. 根因与最终模型：唯一状态 owner、operation owner、epoch/generation、IPC dispatch 语义。
3. 分阶段改动：每个阶段改了什么，为什么按这个顺序。
4. 关键文件：只列真正改变职责的文件，并说明职责变化。
5. 自动化证据：完整命令、通过数、失败数、warnings/errors。
6. 确定性竞态证据：列出每个 barrier 测试证明的错误交错。
7. 真实 Windows 证据：Core、Service、System Proxy、退出、睡眠/恢复；没做的明确写未验证。TUN 单独固定写“未验证（用户明确禁止操作，以避免断网）”。
8. 兼容性与安全：IPC 版本、升级路径、secret/日志脱敏、loopback 边界。
9. 剩余限制：只报告真实存在的限制，不用“应该可以”代替验证。
10. 工作树状态：说明没有覆盖用户修改，是否存在未提交改动。

## 什么时候暂停并询问用户

只有缺失决定会实质改变以下事项时才暂停：

- Service/API 信任边界或新增特权命令。
- 安装、升级、卸载和版本不兼容策略。
- 用户数据迁移或潜在数据丢失。
- 真实机器 System Proxy、非 TUN 路由/DNS、服务或证书操作授权。
- P0/P1 产品范围或发行范围。

普通命名、文件拆分、内部类型和测试组织按最小、安全、可验证的方案继续，不把实现细节变成用户阻塞点。

不要为真实 TUN 操作向用户发起权限询问；用户已经明确禁止，本轮保持未验证即可。

## 终局画面

修复完成后，ClashTray 的每个重要操作都有明确边界：

- 点击只是意图，不直接等于状态。
- 准入决定操作是否接受以及与谁冲突。
- 外部副作用有唯一 owner 和独立 lifetime。
- 跨进程发送后即使丢失响应，也能知道是未发送还是结果未知，并通过状态对账。
- 所有异步结果携带 epoch/generation，过时结果只能成为 Superseded。
- 单写者 reducer 提交 confirmed state，发布层只转发最新快照。
- 后台读取不会挡住设备清理。
- 退出先停止新工作，再以最高优先级恢复网络和关闭资源。

达到这个状态，项目才从“有很多锁和状态枚举”进化为“操作行为有统一、可证明的抽象”。

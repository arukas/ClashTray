# ClashTray 快速交互与 TUN 自愈 Agent Vibe

> 这是给编码 Agent 的专项执行稿，不是新的产品需求，也不自动授权实施。
> 只有用户明确说“按此文档实现 / 开始修复”等指令后，才可以修改功能代码。
> 根目录 `AGENTS.md` 始终是更高优先级约束；用户最新明确指令优先于本文。

- 日期：2026-09-15
- 代码基线：`v0.3.0-alpha.4`，提交 `88f3035`
- 现场核心：Mihomo `v1.19.30`
- 状态：已实施，计划随 `v0.3.0-alpha.5` 发布；自动化通过，真实 TUN 开关验收未执行
- 范围：快速点击造成的操作积压、界面卡顿，以及快速切换 TUN 后出现 `Start TUN listening error: missing interface address` 并失效的问题

## Alpha 5 实施记录

- 模式和节点选择已改为 latest-wins；重复延迟测试共享同一在途任务，TUN 与核心生命周期使用严格准入，不再保存快速点击形成的过时命令队列。
- App 快照已改为 latest-only dispatcher handoff，并按当前页面定向刷新；远程 Controller 页面导航继续使用最后显示的投影快照，不回退为本机快照。
- TUN 写入已统一进入 Service transaction。核心先以 TUN 关闭配置启动，再由 Service 根据期望状态执行开启；Service 结合 controller、listener 错误和 Windows 地址/路由/DNS 探针确认结果。
- `missing interface address` 故障会触发有界恢复：先关闭并确认收敛，必要时最多重启 Mihomo 一次；只有用户最新期望仍为 On 才重试一次，失败后不会提交虚假的 On 状态。
- Windows 路由探针使用 `GetIpForwardTable2` 只读读取活动路由接口，未引入 shell、PowerShell 或 `netsh`；单张只支持 IPv4 或 IPv6 的网卡不会使整个探针失败。
- Release x64 构建结果为 0 warnings、0 errors；Core tests 257/257、Integration tests 16/16。
- 仓库自带的隔离 WinUI smoke flow 退出码为 0；已覆盖启动隐藏、面板显隐、五页导航、浅色/深色、节点搜索/滚动、延迟状态和设置草稿，使用合成数据且未初始化网络。
- 自动化故障注入已覆盖 REST 204 + listener error、配置缺少地址、Windows 地址未建立、关闭状态未知、恢复期间用户改为 Off、单次重启/重试上限和 gate 释放。
- 当前机器只执行了 Windows 路由表与网卡索引的只读实机检查。未实际开启或关闭 TUN，未修改 System Proxy、路由、DNS、网卡或 Service；真实设备压力、睡眠/恢复和故障复现仍按下文“真实 Windows 验收”标记为未验证。

## 给 Agent 的一句话

把“按钮点击一次就排入一次完整操作”的模型改掉：普通模式和节点切换采用 latest-wins 合并，TUN 与核心生命周期采用严格单飞；TUN 只有在 Mihomo 状态和 Windows 网络状态都确认后才算成功，失败时由 Service 自动恢复 Mihomo，而不是要求用户退出整个 ClashTray。

## 开工前必读

按顺序阅读：

1. `/AGENTS.md`
2. 本文
3. `docs/architecture.md`
4. `docs/core-status-fix-plan-2026-09-09.md`
5. 与本轮直接相关的实现和测试：
   - `src/ClashTray.App/MainWindow.xaml.cs`
   - `src/ClashTray.App/App.xaml.cs`
   - `src/ClashTray.Core/ClashTrayRuntime.cs`
   - `src/ClashTray.Core/SnapshotPublishThrottle.cs`
   - `src/ClashTray.Core/LocalDeviceCoordinator.cs`
   - `src/ClashTray.Core/MihomoApiClient.cs`
   - `src/ClashTray.Core/MihomoProcessManager.cs`
   - `src/ClashTray.Contracts/StateContracts.cs`
   - `src/ClashTray.Service/ServiceCommandHost.cs`
   - `src/ClashTray.Service/ServiceRuntimeController.cs`
   - `src/ClashTray.Service/TunShutdownGuard.cs`
   - `tests/ClashTray.IntegrationTests/TunLifecycleTests.cs`

不要因为这份专项稿扩大到 0.3.0 的 SSID、Remote Endpoint 或其他 P2 工作。

## 用户看到的问题

两个现象属于同一类“高频输入没有正确准入和收敛”的问题，但不能使用同一种调度策略：

1. 快速点击规则、全局、直连或节点选择时，点击会排队；每次操作完成后又执行大范围刷新和 UI 更新，表现为面板卡顿、按钮晚很久才追上用户最后一次选择。
2. 快速切换 TUN 时，偶发 Mihomo 日志：

   ```text
   Start TUN listening error: missing interface address
   ```

   此后 TUN 无法正常恢复，必须关闭并重启 ClashTray。重启之所以暂时有效，是因为 Mihomo 进程和半初始化的 TUN 生命周期被整体重新建立，而不是因为 UI 开关本身得到修复。

## 本轮完成后的用户体验

- 狂点模式按钮不会形成长队，最终状态只追随最后一次选择。
- 狂点同一代理组中的节点不会执行每一个中间选择。
- TUN 切换开始后，面板和托盘中的 TUN 命令立即进入忙碌状态，后续点击不排队。
- TUN 从关闭到再次开启，必须等上一次关闭真正收敛。
- HTTP 204、配置里的 `tun.enable=true` 或用户最后一次点击，都不能单独证明 TUN 已经成功。
- 出现 `missing interface address` 时，应用不能显示 TUN 已开启；Service 自动把机器恢复到安全的 TUN 关闭状态，并在条件安全时最多重启 Mihomo、重试一次。
- 即使恢复失败，也必须优先保证普通网络可用；不能无限重启、无限重试或留下错误的“已开启”显示。
- 用户不需要为了恢复 TUN 而退出整个 ClashTray。

## 当前代码证据

### 1. 全局锁实现了串行，但没有阻止排队

`ClashTrayRuntime` 的 `_operationLock` 是 `SemaphoreSlim(1, 1)`。模式切换、节点选择、TUN、核心生命周期等操作都会等待该锁。这样能避免同一时刻并发写入，却会把每一次快速点击都保存成待执行任务。

重点位置：

- `ClashTrayRuntime.SetModeCoreAsync`：约第 1529 行。
- `ClashTrayRuntime.SelectProxyAsync`：约第 1552 行。
- `ClashTrayRuntime.SetTunCoreAsync`：约第 2072 行。

串行不等于高频输入治理。等待队列越长，用户越会感觉界面“卡住”，过时命令也仍会依次修改状态。

### 2. TUN 的忙碌状态发布得太晚

`App.ToggleTunAsync` 只在当前快照为 `Enabling` 或 `Disabling` 时拒绝点击；但 `SetTunCoreAsync` 先等待全局锁、再保存设置，之后才把快照改为过渡状态。

因此存在竞态窗口：多次点击可以在第一笔操作发布 `Enabling/Disabling` 之前全部进入。UI 保护只能改善交互，不能作为正确性边界。

### 3. TUN 有两个写入入口

用户切换 TUN 时走 `LocalDeviceCoordinator → Service`，但 `ClashTrayRuntime.ApplyProgramTunPreferenceAsync` 仍可直接调用 `MihomoApiClient.SetTunAsync`。

这会造成桌面进程和 Service 同时拥有 TUN 写权限：

- Service 的 `_tunState` 可能与 Mihomo 实际状态不同。
- Service 无法观察和恢复桌面进程直接触发的失败。
- 启动、设置恢复、手动切换和核心重启可能使用不同的生命周期规则。

TUN 必须只有一个 owner：Windows Service。

### 4. Service 只确认配置布尔值

`ServiceRuntimeController.SetTunAsync` 当前流程是：

1. GET `/configs` 读取旧值。
2. PATCH `/configs` 修改 `tun.enable`。
3. 最多轮询五次，每次间隔 200ms，确认配置布尔值。
4. 失败时再 PATCH 回旧值。

`TunShutdownGuard` 也只确认 `/configs`。它没有确认 Windows 上的接口地址、相关路由和 DNS 是否已经建立或释放。

### 5. Mihomo 的 HTTP 成功不代表 TUN listener 成功

在 Mihomo v1.19.30 中，REST 配置处理器调用 TUN 重建后返回 204；TUN listener 创建失败时，错误由 `ReCreateTun` 写入日志，并把内部 TUN Enable 改回 false。这个失败不会以 HTTP 错误直接返回给 ClashTray。

因此：

- `PATCH /configs` 成功只能证明请求被接受。
- `/configs` 是必要确认，但不是完整的 Windows 网络健康确认。
- `Start TUN listening error:` 必须成为本次 TUN 操作的立即失败信号。

上游依据：

- [Mihomo v1.19.30 REST configs handler](https://github.com/MetaCubeX/mihomo/blob/v1.19.30/hub/route/configs.go)
- [Mihomo v1.19.30 ReCreateTun](https://github.com/MetaCubeX/mihomo/blob/v1.19.30/listener/listener.go)
- [Mihomo v1.19.30 TUN config parsing](https://github.com/MetaCubeX/mihomo/blob/v1.19.30/config/config.go)
- [sing-tun missing interface address issue](https://github.com/SagerNet/sing-tun/issues/58)

### 6. UI 仍可能积累过时快照

Core 已有 `SnapshotPublishThrottle`，默认 250ms 合并流式日志和数据刷新，这是现有正确资产，应复用而不是另建一套互相竞争的节流器。

但以下路径仍值得处理：

- `App.OnAppSnapshotChanged` 每个事件都调用一次 `DispatcherQueue.TryEnqueue`，没有“队列里只保留最新快照”的保证。
- `MainWindow.UpdateSnapshot` 每次都重建配置 ComboBox，并通知全部页面，包括当前未显示页面。
- 模式或节点修改后，`ExecuteControllerMutationAndRefreshAsync` 调用 `RefreshFromApiAsync`；后者除了版本和配置，还会读取代理、流量、内存、连接、规则和 providers。一次很小的写操作触发了一次大范围刷新。

## 操作调度规则

不要给所有按钮统一套一个“防抖”。按动作语义处理：

| 动作 | 调度策略 | 忙碌时的新请求 | 成功确认 |
| --- | --- | --- | --- |
| Rule / Global / Direct | latest-wins | 覆盖尚未开始的旧目标；当前完成后最多再应用一次最新目标 | 定向读取 mode |
| 同一代理组节点 | per-group latest-wins | 只保留该组最后一个节点 | 定向读取该组当前选择 |
| 延迟测试 | single-flight per node/group | 合并相同测试；必要时允许取消 | 对应测试结果 |
| TUN 开/关 | strict single-flight | 相同目标共享当前任务；相反目标直接 Busy/忽略，绝不排队 | Mihomo + Windows 状态 |
| 核心启动/停止/重启 | strict single-flight | 返回 Busy，不排队 | Service + Controller 健康状态 |
| 配置应用/切换 | 事务化串行 | 使用现有 coordinator 语义，不与 TUN/核心生命周期交叉 | 验证、提交或回滚 |
| UI 快照 | latest-only | 覆盖尚未渲染的旧快照 | UI 线程完成最新一次渲染 |

关键约束：latest-wins 适用于可以安全跳过中间状态的选择类操作；TUN、核心和配置事务不能跳跃或反转，必须严格单飞。

## 必须实现的方案

### A. 在真正的操作入口做原子准入

UI 点击事件只负责发意图。正确性保护必须位于 Core 和 Service 边界。

要求：

- TUN 操作在任何 `await`、设置保存或全局锁等待之前，原子地登记“操作进行中”。
- 相同目标的重复请求可以返回/等待同一个现有 Task；相反目标在忙碌期间返回明确的 Busy 结果，不能进入 `_operationLock` 队列。
- TUN 操作无论成功、失败、取消或抛异常，都必须在 `finally` 释放准入状态。
- 托盘菜单和主面板调用同一个入口，不能各自维护一套 bool。
- Core 侧做一次准入，Service 再做一次防御性准入。命名管道只有一个实例并不等于 TUN 生命周期已经安全。
- TUN 与核心 start/stop/restart、运行配置重新应用共享一个生命周期冲突矩阵。不能在 TUN 验证或恢复阶段重启核心，也不能在核心重启中插入新的 TUN 请求。

优先使用一个小而明确的 coordinator 或 operation slot，不要建立没有测试价值的通用命令框架。

### B. 立即发布过渡状态，但最终状态只能来自确认

操作成功获得准入权后立即发布：

- 开启：`TunState.Enabling`
- 关闭：`TunState.Disabling`

UI 随即禁用 TUN 开关和托盘 TUN 菜单项。开关保持显示最后一次确认的实际状态，可以配合进度提示；不能先乐观翻到目标状态。

内部应区分更细阶段：

```text
Off
  → Enabling
  → VerifyingEnable
  → On

On
  → Disabling
  → VerifyingDisable
  → Off

任意失败
  → Recovering
  → Off / Failed / Unknown
```

这些细阶段可以是 Service 内部模型。若扩展公共 `TunState`，必须同步升级 Contracts、Service、App、安装包和升级测试；不得造成旧 App 与新 Service 对同一枚举值的错误解释。

### C. 把 TUN 写操作全部收口到 Service

移除桌面运行时直接执行 `api.SetTunAsync(...)` 的 TUN 偏好恢复路径。所有本机 TUN 写入都必须经过 Service，包括：

- 面板和托盘手动切换；
- 核心启动后的程序偏好应用；
- 配置切换后的 TUN 恢复；
- 睡眠/恢复或网络变化后的修复；
- 核心重启、退出和卸载前的关闭。

建议形状：

1. 生成的运行时配置以安全、可控的 TUN 初始状态启动；不要改写用户的源 YAML。
2. Controller 健康后，由 Service 使用同一个内部 TUN transaction 应用期望状态。
3. `AppSettings.TunEnabled` 表示用户期望；`TunState` 表示实际确认状态。两者不能用同一个 bool 冒充。
4. 如果本轮不新增持久化字段，至少只在操作确认成功后提交偏好；失败时恢复原偏好，且恢复结果必须与实际状态一致。

Service IPC 保持 allow-list。不得因为健康检查引入任意命令、任意接口名写入、Shell、PowerShell 或 `netsh` 执行能力。

### D. TUN 开启和关闭都执行条件式收敛

固定 `Task.Delay(1000)` 只能临时降低复现概率，不能作为完成方案。使用有界、条件式轮询，并集中定义 timeout。

#### 关闭路径

1. 读取并记录当前确认状态。
2. PATCH `tun.enable=false`。
3. 确认 `/configs` 报告 false。
4. 通过只读 Windows 网络探针确认当前 Mihomo TUN 不再拥有活动接口地址、路由和 DNS 状态。
5. 连续两个样本稳定后才提交 `Off`；样本间隔建议 300～500ms，总超时建议 5～10 秒，最终数值应集中配置并测试。

不要要求 Wintun 设备对象一定从系统中消失；持久设备可以存在。要确认的是本次 Mihomo TUN 的活动地址和路由已经释放。

#### 开启路径

1. 在写入前验证生效配置至少能产生一个合法的 TUN IPv4 或 IPv6 接口地址。
2. 记录本次 ServiceRequest ID、开始时间和进程 generation。
3. PATCH `tun.enable=true`。
4. 同时等待以下证据：
   - `/configs` 最终稳定为 true；
   - 本次操作之后没有出现 `Start TUN listening error:`；
   - Windows 探针看到对应 TUN 接口拥有合法地址，并出现所需活动路由；
   - Mihomo 进程和 Controller 仍属于同一个 generation 且健康。
5. 全部通过才提交 `On`。

Service 可订阅其 `MihomoProcessManager.LogLineReceived`，为当前 TUN operation 保存一个很小的有界事件窗口。只把已知 TUN 成功/失败信号关联到当前 operation，不要把无界原始日志复制进 Service 内存。

日志是快速负面信号，Windows 状态和 Controller 状态是最终确认。不要仅依赖某一条可能随 Mihomo 版本变化的成功文案。

### E. 明确处理 `missing interface address`

该错误说明 sing-tun 在创建系统栈时没有得到可用接口地址。需要区分两个来源：

1. 生效配置本身无法产生有效 TUN 地址。
2. 快速关闭后系统资源尚未收敛，下一次重建落在瞬态错误中。

验证时记录脱敏后的必要信息：

- operation/request ID；
- Mihomo process generation；
- TUN stack、device name、auto-route；
- 是否存在合法 IPv4/IPv6 prefix；
- FakeIP prefix 是否有效；
- 各阶段耗时与 Windows 探针结论。

不得记录完整配置、订阅 URL、节点凭据、Authorization 或 controller secret。

不要简单向用户配置硬塞一个任意 `inet4-address`。Mihomo 顶层 TUN 配置通常会从 DNS FakeIP 范围推导地址；Agent 必须先检查 ClashTray 生成后的实际运行配置，再决定是否需要验证或补默认值。只改运行时副本，不改用户源配置。

### F. 失败后由 Service 自动自愈

当出现 `Start TUN listening error:`、开启验证超时或 Windows 地址/路由验证失败时：

1. 立即把当前开启操作判定为失败，绝不能发布 `On`。
2. 进入内部 `Recovering` 阶段，阻止全部冲突操作。
3. 强制请求 `tun=false`。
4. 等待关闭状态和 Windows 网络状态收敛。
5. 如果 Controller 已确认 false、但当前 Mihomo 仍不能重新建立干净状态，则由 Service 受控重启 Mihomo；启动时强制保持 TUN 关闭。
6. 确认新进程 generation、Controller 和普通网络路径健康。
7. 只有用户期望仍为开启、配置验证通过且没有更新的关闭意图时，才允许自动重试一次。
8. 第二次失败后停止，不再循环；最终保持 TUN 关闭，返回可行动的错误。

如果连 `tun=false` 都无法确认，不能为了“自动恢复”盲目强杀核心。保持 `Unknown/Failed`、禁止继续切换，给出明确诊断；网络安全高于自动化成功率。

建议用户文案：

```text
TUN 启动失败，Mihomo 已自动恢复，当前 TUN 已关闭。
原因：缺少可用的接口地址。
```

若自动恢复本身未完成：

```text
TUN 状态无法确认。为避免影响网络，未继续重试；请检查服务和诊断日志。
```

所有新增文本同时写入 `Strings/zh-CN/Resources.resw` 和 `Strings/en-US/Resources.resw`。

### G. 普通快速点击采用 latest-wins 和定向刷新

模式切换：

- 当前请求执行时，只保存一个 `pendingMode`。
- 新点击覆盖 `pendingMode`。
- 当前操作结束后，如果目标已变化，最多再执行一次最新目标。
- 不执行被覆盖的中间模式。
- 保留现有 endpoint generation 和 remote/local command policy 检查。

节点切换：

- 以代理组为 key 保存最后选择。
- 同组中间选择可以丢弃；不同组仍遵循 Controller 写入串行和 generation 检查。
- 如果当前组或 endpoint 已变化，旧响应不能提交到新会话。

修改后的确认刷新必须按动作收窄：

- 模式切换只确认 `/configs` 中的 mode。
- 节点切换只确认目标 proxy group 当前选择，必要时读取一次 `/proxies`。
- 不要因为模式或节点切换重新读取 traffic、memory、connections、rules 和 providers。
- 后台周期刷新继续负责全量数据；操作完成可发送一个轻量快照更新。

可以把 `ExecuteControllerMutationAndRefreshAsync` 的 `includeRulesAndProviders` bool 演进为明确的刷新范围，但不要创建无边界的通用刷新 DSL。

### H. UI 只渲染最新快照

`App.OnAppSnapshotChanged` 改为 latest-only dispatcher handoff：

- 任一时刻最多只有一个 UI dispatcher 回调等待执行。
- 新快照覆盖 pending snapshot，而不是再排一个回调。
- 回调处理完后，如果期间又有新快照，再处理最新一份。
- 退出时不会留下访问已关闭窗口的 continuation。

`MainWindow.UpdateSnapshot`：

- 配置列表内容和 ID 未变化时，不清空重建 ComboBox。
- 只更新活动页面，或让各页面依赖不可变集合引用/版本进行增量更新。
- 保留现有页面的 reference-equality 优化和 `SnapshotPublishThrottle`。
- 不把 250ms throttle 加到需要立即反馈的核心/TUN过渡状态上；过渡状态立即发布，流式指标和日志继续合并。

目标不是减少所有刷新，而是让过时刷新不会占用 UI 线程。

## 建议代码落点

Agent 可根据测试性调整命名，但职责不能漂移：

| 文件/区域 | 预期改动 |
| --- | --- |
| `MainWindow.xaml.cs` | 点击期间正确禁用、非乐观开关、避免配置列表无变化重建、只刷新必要页面 |
| `App.xaml.cs` | TUN 不再依赖快照竞态防重；UI dispatcher latest-only 合并；托盘与面板共享命令状态 |
| `ClashTrayRuntime.cs` | 选择类 latest-wins；TUN 原子准入；移除直接 TUN API 写入；定向确认刷新 |
| `LocalDeviceCoordinator.cs` | 暴露窄、可测试的 Service TUN transaction 入口，不做本地越权 fallback |
| `StateContracts.cs` | 必要时补充操作结果/状态；若改 IPC，保证 App 和 Service 同步升级 |
| `ServiceRuntimeController.cs` | TUN 单 owner、单飞、分阶段验证、日志关联、失败恢复、一次性核心重启/重试 |
| `TunShutdownGuard.cs` | 从仅配置布尔确认升级为共享的关闭收敛验证；保持 fail-safe |
| `MihomoProcessManager.cs` | 复用有界日志事件，不要复制第二套进程读取器 |
| 新的 Windows TUN health probe | 只读检查接口地址/路由；不执行任意 shell；提供 fake 实现或窄测试 seam |
| Core/Integration tests | 并发准入、latest-wins、REST 204 + listener error、延迟释放、自愈、锁释放和状态一致性 |
| 中英文 `.resw` | Busy、Recovering、自动恢复成功/失败等文案 |

## 先写的失败测试

实现前先让这些测试失败，避免只靠人工狂点判断：

### 操作准入

- 同时发起 20 个 `SetTunAsync(true)`，底层 Service EnableTun 只执行一次；其余请求共享结果或返回 Busy。
- `Enable` 尚未完成时发送 `Disable`，Disable 不进入等待队列。
- 操作成功、失败、取消和超时后，gate 都会释放；下一次正常请求可执行。
- TUN 过渡中，RestartCore 和配置 apply 不会与之交叉。
- App 入口、托盘入口和直接 Core 调用都无法绕过 gate。

### TUN 确认与恢复

- PATCH 返回 204，但随后注入 `Start TUN listening error: missing interface address`：结果必须失败，绝不能是 `On`。
- `/configs=true` 但 Windows 探针没有有效接口地址：保持 `Enabling/Failed`，不能提交 `On`。
- `/configs=false` 后 Windows 状态延迟释放：在探针稳定前不能开始下一次 Enable。
- listener error 后，Service 先关闭并等待收敛，再重启 Mihomo；无需重启 App。
- 自动重试最多一次；第二次失败后保持 Off，不形成重启风暴。
- 用户在恢复阶段的最新期望已经变为 Off 时，不进行自动重新开启。
- 核心 generation 在操作中变化时，旧操作结果不得提交。
- 无法确认 false 时不强杀核心，返回 Unknown/Failed 并保持安全路径。

### 普通点击和 UI

- 快速提交 Rule → Global → Direct → Rule，最终只保证首个在途目标和最后目标得到执行，中间 pending 值被覆盖。
- 同一组连续选择 20 个节点，最终节点正确，Controller 调用数有界，不等于 20。
- 模式/节点成功后不触发 traffic、memory、connections、rules 和 providers 的同步刷新。
- 短时间发布大量 AppSnapshot，UI dispatcher pending callback 始终最多一个，最终渲染最新快照。
- metrics-only snapshot 不重建代理控件、配置 ComboBox 或用户正在编辑的设置字段。

## 自动化验收门槛

- [ ] 1 秒内模拟点击 TUN 20 次，最多一个 TUN transaction 在途，没有反向命令排队。
- [ ] TUN 关闭尚未收敛时，开启操作不会到达 Mihomo。
- [ ] REST 204 加 listener error 永不显示为 On。
- [ ] `missing interface address` 后自动恢复 Mihomo，ClashTray App 无需退出。
- [ ] 恢复完成后要么确认 On，要么安全 Off；不得处于“显示 On、实际不可用”。
- [ ] 自动恢复只有一次核心重启和至多一次重新开启，不会形成循环。
- [ ] 模式和节点快速切换最终值正确，过时请求不会逐个执行。
- [ ] UI dispatcher 不积累快照，面板在高频点击和日志/流量刷新同时发生时仍可操作。
- [ ] 所有失败路径释放 operation gate。
- [ ] 日志完整但脱敏，不包含 secret、订阅 URL、节点凭据或完整 YAML。
- [ ] Core、Service、Contracts、App 全部以同一版本进入安装包。

## 真实 Windows 验收

自动化测试不能替代真实 TUN 验收。以下动作会影响网络，只有得到用户明确授权后才能执行：

1. 使用非生产测试配置启动 Mihomo，记录初始网卡、地址、路由和 DNS。
2. 正常开启/关闭 TUN，确认 UI、Service、`/configs` 和 Windows 状态一致。
3. 连续执行 50～100 轮有节制的开关测试，再执行用户原始的快速点击复现。
4. 注入或复现一次 `missing interface address`，确认自动恢复后无需退出 App。
5. TUN 切换期间执行核心重启、配置切换、睡眠/恢复和网络适配器变化，验证冲突序列化。
6. Service 重启、App 重启、Mihomo 崩溃后重新查询真实状态，不使用上次按钮值冒充。
7. 验证失败恢复后普通网络、DNS 和路由可用，没有 ClashTray 拥有的残留状态。

如果无法在当前环境安全执行，必须明确报告“未验证”，不能把 fake handler 结果写成真实 TUN 通过。

## 推荐实施顺序

1. 建立并发准入、latest-wins 和 TUN 失败注入测试。
2. 实现 Core 入口原子准入，先堵住新请求排队。
3. 实现模式/节点 latest-wins 与定向刷新，解决快速点击卡顿。
4. 移除桌面端直接 TUN 写入，统一 Service owner。
5. 在 Service 中实现 TUN operation phase、日志关联和 Windows health probe。
6. 实现 `missing interface address` 的关闭、收敛、核心重启和一次重试。
7. 实现 UI latest-only snapshot handoff 和增量渲染。
8. 跑完整 Debug/Release build 与 test。
9. 获得授权后执行真实 Windows TUN、路由和 DNS 验收。
10. 更新用户文档和发行说明，记录实际验证证据。

## 构建与测试基线

```powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64
dotnet build ClashTray.sln --configuration Release --property:Platform=x64
dotnet test ClashTray.sln --configuration Release --property:Platform=x64
```

若测试环境不能加载 WinUI/MSIX，不得只跑 Core Tests 后声称整个解决方案通过。精确记录每个命令、通过数、失败数和未运行原因。

## 禁止用这些方式“修复”

- 只在 UI 上禁用按钮或增加 500ms debounce。
- 在 TUN 关闭后无条件 `Task.Delay(1000)`，不检查实际状态。
- 继续让 App 和 Service 都直接 PATCH TUN。
- 把 HTTP 204 当作 TUN 成功。
- 只看 `tun.enable`，不看 listener 错误和 Windows 地址/路由。
- 遇到失败无限重试或循环重启核心。
- TUN 状态 Unknown 时仍把托盘图标和开关显示为 On。
- 为了恢复而引入任意 shell/PowerShell/netsh IPC。
- 修改用户源配置、写入任意固定 TUN 地址或重置用户设置。
- 为了减少卡顿而停止正常后台状态刷新。
- 吞掉异常、只返回 false，或把所有失败都显示成“核心未运行”。
- 在未经授权时切换真实 TUN、System Proxy、服务或机器网络设置。

## Agent 完成回报格式

交付时按以下顺序汇报：

1. 用户可见结果：快速点击怎样被合并，TUN 错误怎样自动恢复。
2. 状态模型和 owner：谁拥有 TUN，期望状态和实际状态怎样区分。
3. 关键文件：每个文件为何改变。
4. 自动化证据：完整 build/test 命令和精确结果。
5. 故障注入证据：REST 204 + listener error、延迟释放、核心重启一次、无重复队列。
6. 真实 Windows 证据：网卡、地址、路由、DNS、睡眠/恢复；未执行的必须明确写出。
7. 剩余限制：不能用“应该可以”代替实测结论。

## 完成定义

本轮不是“按钮不那么容易点”就完成，而是同时满足：

- 选择类操作不积累过时队列。
- TUN 和核心生命周期严格单飞。
- TUN 只有一个 Service owner。
- TUN 成功由 Controller、listener 信号和 Windows 状态共同确认。
- `missing interface address` 能自动恢复到安全状态，不要求退出 ClashTray。
- 失败不让用户断网，不留下虚假的 On，也不产生重启风暴。
- UI 只消费最新状态，日志和流量刷新期间仍然顺滑。
- 自动化与真实 Windows 验收结果都如实记录。

达到这些条件，才算真正解决这次“快速点击卡顿 + TUN 后续失效”。

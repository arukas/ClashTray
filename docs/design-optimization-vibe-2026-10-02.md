# ClashTray 代码设计优化 Agent Vibe 指南

日期：2026-10-02。审查基线：`0029bf1`（`release: prepare concise 0.3.4 stable notes`）。

状态：待实施指南。本文件记录六项优化建议、实施要求和验收标准，不表示代码已经修改或验收通过。执行时以实际 HEAD 和工作区为准，先核对下述问题是否仍存在；已修复的项目用当前证据关闭，不能重复改造。

模型：`gpt-6.1-sol`；reasoning effort：`max`。用户所说的 `6.1-sol-max` 在本指南中对应这两个独立设置。实际模型由执行会话配置选择，不能通过提示词或仓库文本声称已经切换。

## 1. 任务与执行方式

目标是在保持现有用户行为和安全边界的前提下，完成设置回滚、IPC 请求恢复、Runtime 职责、YAML 处理、设置编辑模型、测试环境隔离六项优化。

优先保持现有操作协调、控制器代际校验、运行绑定确认、System Proxy 所有权恢复、有界日志、按页面需求刷新的机制。通过小切片降低修改和验证成本，不一次性重写运行时。

用户明确要求“执行本指南”或发送配套实施提示词后，持续完成全部六项代码、测试和交付记录。不要只交一份新计划，不要每完成一个切片就询问是否继续。缺少某项环境时继续完成不依赖它的工作，并如实记录验收缺口。仅准备或修改本指南不授权开始实现。

开工前依次读取：

1. 根 `AGENTS.md` 及受影响目录内适用的指令。
2. 本指南、`docs/development-policy.md`、`docs/architecture.md`。
3. 六项涉及的当前代码、正式测试、`.github/workflows/ci.yml`。
4. 验证阶段所需的 `packaging/Test-OfficialMihomo.ps1`、`packaging/Test-CoverageGate.ps1` 和相关环境说明。

核对 Git 状态和 Windows/.NET/WinUI 工具链，保留全部无关已修改与未跟踪文件。历史整改记录用于理解既有约束，不能把历史待办全部重新纳入本轮。

## 2. 审查证据与验证边界

2026-10-02 审查实际运行：

| 项目 | 当次结果 | 解释 |
| --- | --- | --- |
| Release x64 全解决方案构建 | 通过，0 警告、0 错误 | 使用 `--no-restore` |
| Core.Tests | 670 项：662 通过，8 失败 | 八项失败均报告 CoreUpdaterTests 清理临时 `program/logs` 时拒绝访问；清理异常可能覆盖主体异常，不能据此认定主体全部通过 |
| IntegrationTests，排除 `RequiresOfficialMihomo` | 29 项：25 通过，4 失败 | 受限令牌创建一项、TLS/握手两项、服务日志 ACL 清理一项；环境因素与产品缺陷需分别定位 |
| 官方 Mihomo 专项及真实 Windows UI/网络流程 | 未执行 | 不得引用历史成功结果替代本轮验证 |

审查确认 A1 存在静默吞掉设置恢复异常的代码路径，尚未进行双重故障注入复现；A2 是请求生命周期设计缺口，尚未证明发生过重复副作用；A3—A5 属于结构优化，没有声称发现新的 YAML 绕过或 UI 故障。A6 有上述实际失败记录。

测试失败不得直接归因为产品回归，也不得未经调查全部归为沙箱问题。重新执行时记录身份、权限与环境能力的必要诊断，不采集真实凭据。

## 3. 范围与不变量

- 保持 Windows x64、C#/.NET 10、WinUI 3、独立 Mihomo 进程与受限服务 IPC。沿用当前发布方式，本轮不处理历史 MSIX 方向与现有安装器的迁移。
- 保持本地控制器仅绑定 `127.0.0.1`、显式空 secret；不增加启动时已安装核心哈希重算。更新来源、校验规则、ACL、路径限制与远程/本机权限隔离不得放宽。
- System Proxy 与 TUN 状态来自确认结果；保留未知结果、取消前/后派发区分、所有权冲突保护、退出恢复与配置/订阅/更新回滚。
- 现有已实现的本地和远程行为都应保持兼容。不得借重构接入 Wi-Fi/SSID、扩展 P2、增加平台、增加遥测或新增产品功能。
- 不为拆类而改变操作先后、锁语义、请求频率、队列/缓存上限、恢复期限、UI 刷新节流或安全判定。必要契约变化需有测试和兼容说明。
- 不添加统一消息总线、通用事务框架或一整套新 UI 框架作为前置条件。新增接口必须有真实的边界隔离或测试价值。
- 文档执行默认不包含提交、推送、tag、版本变更、发布、系统软件安装、正式服务安装或操作用户当前真实代理/TUN。后续用户已有明确授权时按该授权执行；真实系统验收优先使用隔离 Windows 测试环境。

## 4. A1：让设置回滚失败可见，并恢复一致性

代码入口：

- `src/ClashTray.Core/ClashTrayRuntime.Settings.cs`：`SaveSettingsForOperationAsync`、`RestoreSettingsAfterOperationFailureAsync`、`UpdateSettingsAsync`。
- `src/ClashTray.Core/ClashTrayRuntime.Network.cs`：System Proxy/TUN 偏好保存与失败处理。
- 既有设置存储替身及 RuntimeSettings/RuntimeSystemProxy/服务协调测试。

问题：恢复方法先改内存，再写旧设置，空 catch 隐藏第二次写盘失败。新偏好已落盘、网络操作失败、旧偏好写回失败时，当前内存与重启后读取值可能不同。

实施要求：

1. 先建立确定性的双重故障回归：第一次保存成功，网络操作失败，恢复保存失败。核对内存、持久化、已确认网络状态和错误信息四者。
2. 返回明确的恢复结果，或采用项目已有的类型化失败契约。保留原始操作失败和恢复失败，不用后一个覆盖前一个。
3. 恢复失败进入脱敏日志与可见状态。复用现有错误面板和恢复机制；不要把“内存已改回”表述为“设置已恢复”。
4. 明确保存顺序、恢复期限、取消语义和后续恢复触发点。下一次读取持久化状态时应能解释并处理未完成恢复；如需恢复记录，必须有界、原子、兼容现有设置，不能记录 secret。
5. 统一普通设置、System Proxy 与 TUN 的恢复结果表达，但保持三者原有副作用顺序和网络安全策略；不要强行改成一套相同操作流程。

验收：正常成功、初次写盘失败、网络失败但回滚成功、网络与回滚均失败、已派发取消、重启后恢复均有行为断言。外部代理所有权变更不被覆盖；不把未确认网络状态显示为成功。

## 5. A2：完成跨进程请求身份与结果恢复闭环

代码入口：`ServicePipeClient.cs`、`LocalDeviceCoordinator.cs`、`ServiceRuntimeController.cs`、`ServiceCommandHost.cs`、`StateContracts.cs`，以及 Runtime 的未知服务请求结果核对路径。

问题：服务端缓存 RequestId 和命令指纹，但客户端每次发送都创建新 ID。一次业务请求响应丢失后，客户端无法通过当前接口重取该请求的原始结果。

实施要求：

1. 将“业务操作身份”和“单次连接尝试”分开。同一次业务操作的受控重试使用相同 ID 和不可变命令内容，不同用户操作使用新 ID。
2. 选择一个窄方案：重连同一请求以加入/取回缓存结果，或查询原请求结果。说明取舍，避免同时引入多套恢复协议。
3. 维持同 ID、不同命令拒绝执行；并发相同请求只执行一次。限制缓存容量、条目保留、响应大小、重试次数与总期限，等待取消不得误称已撤销服务副作用。
4. 缓存过期、服务实例变化或进程重启后，不得假定请求未执行。保留 UnknownOutcome 和已确认状态核对；不能在旧结果不可知时自动重放非幂等操作。
5. 明确协议版本或能力协商。旧 App/新 Service、新 App/旧 Service 必须安全兼容或明确拒绝，不能悄悄失去去重保证。服务端仍独立校验权限、路径、运行绑定与目标。
6. 覆盖启停、TUN、安装和回滚的适用策略；不要对所有命令套无限或无条件自动重试。

验收：通过隔离传输或测试服务模拟“执行成功后丢弃响应”，重连取得结果且副作用计数为一；另测并发重复、指纹冲突、响应 ID 不匹配、等待取消、过期、实例更换、缓存饱和和旧协议行为。测试不得启动或停止用户正式服务。

## 6. A3：收敛 Runtime 职责与状态归属

代码入口：`ClashTrayRuntime.cs` 及 Lifecycle/Shutdown/Settings/Endpoints partial、`ServiceRuntimeController.cs`、`ControllerSessionGuard.cs`、`RuntimeStateStore.cs`、`ClashTrayRuntime.TestHooks.cs`。

问题：partial 分文件仍共享同一对象状态。构造函数混合依赖组装、环境选择与测试注入；生命周期文件同时处理启动、两种进程路径、绑定确认、轮询和恢复。

实施顺序：

1. 为现有入口和状态行为补充必要的特征测试，记录各锁的获取顺序及状态写入者。复用已有并发/退出回归，避免大量镜像实现测试。
2. 把生产依赖组装集中到应用入口或专门工厂；核心运行逻辑只接收职责明确的依赖，不以 nullable 参数猜测生产或测试环境。保留必要兼容入口。
3. 优先提取“核心生命周期与健康确认”的单一责任模块，明确运行绑定、进程/控制器代际、服务归属和健康证据的持有者；Runtime 保留应用编排与公开门面。
4. 为本地进程与服务进程建立窄执行接口，复用纯策略或结果校验，保留各自身份验证、特权检查、失败清理和未知结果处理。不要因复用让服务信任 App 提交的安全事实。
5. 不让提取出的模块通过持有整个 Runtime 或大量任意回调重新形成循环依赖。明确模块输入、结果、拥有的资源和释放时机。
6. 保留操作 gate、lease、写优先、共享操作与退出静默期的既有语义；跨模块 I/O 后仍检查代际，旧响应不得提交到新状态。
7. 将健康检查中的测试特例转成可注入的身份/监听观察能力。测试提供观察结果，生产健康判定本身仍执行；不能用测试标记跳过安全判定。

验收：启动/停止/重启、服务失联与恢复、本地回退、控制器替换、并发切换、代理恢复、取消和退出超时测试保持通过；新增观察替身能覆盖归属已确认、未知和外来进程拒绝。架构文档说明依赖方向、状态归属和锁协议。不得以行数变少作为唯一完成证据。

## 7. A4：统一 YAML 结构识别边界

代码入口：`RuntimeConfigBuilder.cs`、`MihomoListenerPlanAnalyzer.cs`、`YamlMappingKeyReader.cs`、配置候选验证器，以及 YAML corpus/监听计划/官方互操作测试。

问题：生成器和监听分析器分别实现部分文档、缩进、引号和 flow collection 识别。已共享键解码，但对其余结构的理解仍可能漂移。本轮不是已发现 YAML 绕过的漏洞修复。

实施要求：

1. 先列出当前实际支持、明确拒绝、保留但无法确认的语法，建立生成与监听分析共同使用的语料和预期，不凭历史描述推测支持范围。
2. 提取有界读取与共享结构识别结果，至少集中键解码、节点范围、文档边界和必要 flow/引号处理；生成与分析消费同一语义约定。
3. 保留未受管配置的语义、受管字段唯一有效覆盖、循环/深度/字节/行数上限与取消检查。输入增长或读写竞争也不能绕过读取上限。
4. 对 aliases、anchors、merge keys、block scalars、转义键、多文档、重复键和嵌套 flow 明确策略。无法确认的监听不得被当作“不存在”；不无意扩大或缩小现有可接受配置范围。
5. 若评估成熟解析库，先写依赖、许可证、语义保真、有界资源和打包影响的短说明再做选择。共享结构层是目标，不要求构建通用 YAML 解析器，也不以盲目反序列化再输出替代保真。
6. 将生成文件交给仓库固定的官方 Mihomo 验证。保留原子写入、ACL、受限路径、失败后旧文件与元数据恢复。

验收：一组语义等价的支持写法生成一致的受管行为和监听证据；负向语料明确拒绝或无法确认且不放行危险启动。原 YAML corpus 和官方核心配置/监听互操作测试通过；没有官方执行证据时单独标记未验证。

## 8. A5：提取设置编辑模型与页面命令边界

代码入口：`SettingsPage.xaml.cs` 的 `SaveButton_Click`、`LoadSettings`、端点/网络开关/维护操作，以及 `AppSettingsPatch.cs` 与现有 UI smoke、语言资源测试。

实施要求：

1. 提取不依赖 WinUI 控件的编辑模型，负责已加载基线、输入草稿、字段修改状态、校验、外部设置合并及 patch 生成。命名可调整，不强制引入新 MVVM 包。
2. 明确区分 NumberBox 的未提交文本与有效数值、无效输入、用户主动修改、外部值变化。保持草稿，不让周期快照覆盖未保存内容。
3. 提取可等待的命令执行逻辑，统一忙碌、错误和完成反馈。页面保留事件入口、绑定、对话框、焦点与可访问性；`async void` 仅作为框架事件入口可以保留。
4. 设置模型不直接承接服务或特权操作。端点编辑、System Proxy/TUN 命令、维护操作逐步分开，保留目标捕获、命令权限和确认状态来源。
5. 语言重启提示、主题即时应用、设置 patch 不覆盖其他入口管理的字段等现有行为保持。新增用户文本提供中英文资源，错误经过脱敏。

验收：用户编辑端口时外部主题/配置更新；同字段冲突合并；未提交数值文本；无效端口；保存失败保留草稿；重复保存；页面离开期间完成；目标端点切换；代理/TUN 失败后显示确认状态。纯编辑逻辑用单元测试，WinUI 焦点、键盘和草稿展示单独做真实 UI 验收，不能拿模型测试替代。

## 9. A6：隔离测试权限能力并修复 fixture 清理

代码入口：`CoreUpdaterTests.cs`、`TestDirectoryCleanup.cs`、`ServiceFileLoggerTests.cs`、`QueryLimitedProcessIdentityTests.cs`、`RemoteEndpointTransportTests.cs` 和 CI。

实施要求：

1. 定位八项 Core 清理失败及四项集成失败的环境条件，区分测试主体失败与 finally 清理失败，保留两者诊断；不要只重试删除或吞掉清理错误。
2. 统一 fixture 目录生命周期。必须验证绝对路径位于本次测试专属根下，处理 reparse point 风险；仅在明确属于本次 fixture 的路径恢复清理所需 ACL，禁止修改真实 ProgramData、用户配置或仓库外未知目录权限。
3. 普通更新逻辑测试与 Windows ACL 策略验证分离。以有测试价值的窄权限策略边界或专用 fixture 实现，不把放宽生产 ACL 当测试修复。
4. 将 ACL/受限令牌/TLS/官方 Mihomo 等能力需求标记清楚。开发者可运行不依赖这些能力的测试；完整 CI/验收必须在具备能力的 Windows 环境执行全部必要用例。
5. 缺少能力可报告“环境不可用”，不能在强制门禁中静默跳过、降低覆盖率或吞掉真实 TLS 错误。TLS 测试不得关闭证书验证或自动降级。
6. 需要并发或超时测试时用可控任务、替身和时钟；保留必要的真实 OS 集成，不把安全边界全部 mock 掉。

验收：普通用户可执行的测试与所需特权有明确区分；fixture 清理不掩盖主体异常、不遗留本次受保护测试目录；CI 必需安全测试与官方核心测试实际执行。产出按类别记录通过、失败、跳过、环境阻塞及诊断。

## 10. 切片顺序与验证命令

执行顺序：基线核对 → A1 → A6 → A2 → A3 → A5 → A4 → 最终回归。A6 可先做支撑 A1 验证的最小 fixture 修复，但不能把其他项目无限延期。每项完成独立可审查的代码、测试和记录，再进入下一项。

每个生产切片完成后，构建全解决方案并运行受影响测试；首次缺依赖时正常 restore。依赖未变化可使用 `--no-restore`。

```powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln -c Release -p:Platform=x64 --no-restore
```

最终回归使用本轮独立且全新的结果目录，以下 `design-optimization-final` 如已存在则改为本次唯一目录，避免覆盖和误读历史覆盖率：

```powershell
dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --collect:"XPlat Code Coverage" --logger "trx;LogFileName=core.trx" --results-directory artifacts/design-optimization-final/core -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura
dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj -c Release -p:Platform=x64 --no-build --logger "trx;LogFileName=integration.trx" --results-directory artifacts/design-optimization-final/integration
.\packaging\Test-OfficialMihomo.ps1 -Configuration Release
.\packaging\Test-CoverageGate.ps1 -CoverageDirectory artifacts/design-optimization-final/core
```

官方门禁可通过 `-ExistingCorePath` 和 `-ExistingArchivePath` 显式提供与仓库固定清单一致的隔离测试核心和归档，不能替换用户正在使用的核心。完整集成测试涉及官方核心时，按当前脚本与测试支持要求设置 `CLASHTRAY_MIHOMO_REQUIRED=true` 及两个明确的路径变量；不要把上面未设置环境时的跳过算作通过。当前覆盖率门槛为 Core 行 70%、分支 65%；执行时遵循仓库当时不低于此值的门槛。

不要为本轮改依赖版本或产品版本。WinUI/实际服务与网络行为变化需在隔离 Windows 环境验收，记录普通 App 与服务权限、退出/恢复、控制器重连、设置草稿、焦点与键盘行为；真实环境不足时完成隔离验证后列出待验收项。

## 11. 交付与完成条件

实现开始时创建并持续更新 `docs/design-optimization-results-2026-10-02.md`，六项各自记录：实际基线、问题证据、设计决策、文件、测试命令与数量、失败分类、兼容性、尚未验证的行为和下一步。该文件不能在仅编写指南时提前写成已完成。

同步更新必要的架构、设置、协议和测试环境说明。交付时六项均需有状态：已实现并验证、代码完成但验收未完成、或明确外部阻塞；不能把未完成项从范围中删除。代码完成与正式发布条件分别判断。

完成标准是：A1 失败恢复可见且有一致性处理；A2 丢响应可安全核对原操作；A3 职责与状态归属清楚且真实判定可测试；A4 生成/分析语义统一并有官方验证；A5 编辑逻辑可独立验证且 UI 行为保持；A6 权限能力明确、清理可靠且强制边界测试实际执行。只编译成功、只减少行数、只把异常隐藏都不满足。

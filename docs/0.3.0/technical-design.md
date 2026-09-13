# ClashTray 0.3.0 技术设计

> 状态：规划基线，待用户批准实施
> 设计版本：1.0
> 对应需求：[0.3.0 产品需求](product-requirements.md)

## 1. 设计目标

本设计在不改变 ClashTray 既有三方信任边界的前提下，为 SSID 自动切换和远程 Mihomo 控制器建立可测试的内部边界。

设计必须同时满足：

- 本机 Service、Mihomo、System Proxy 和 TUN 的安全语义不因远程功能改变。
- 手动与自动配置切换只有一条事务路径。
- 一个控制器会话的延迟响应不能污染另一个会话。
- HTTP 与 WebSocket 不产生不同的认证或证书判断。
- 0.2.0 数据可以无损升级，新功能存储损坏时本机路径仍然可用。
- 改造采用可验证的渐进提取，不一次性重写现有运行时。

## 2. 当前基线与主要问题

### 2.1 当前有效边界

- src/ClashTray.App 是 WinUI 3、托盘、窗口和用户交互层。
- src/ClashTray.Core 承担本机运行时、Mihomo API、设置、配置、订阅、System Proxy 等领域逻辑。
- src/ClashTray.Service 是本机特权边界，只接受窄化的命名管道命令。
- src/ClashTray.Contracts 保存 App、Core 与 Service 使用的状态及 IPC 契约。
- 本机 Mihomo controller 固定为 loopback，Service 只从受管 ProgramData 目录启动经过验证的核心。

这些边界继续保留。

### 2.2 0.3.0 必须先处理的耦合

| 现状 | 风险 | 0.3.0 处理 |
| --- | --- | --- |
| src/ClashTray.Core/ClashTrayRuntime.cs 同时负责生命周期、设置、配置、Controller、System Proxy、TUN 和 UI 快照 | 增加第二个端点后容易把本机动作误发到远程，或让远程健康度影响本机网络 | 以特征测试保护现状，再提取协调器和会话对象 |
| SetActiveConfigurationAsync 先保存活动配置，再重启核心 | 启动失败时 UI 和持久状态可能指向未生效配置 | 引入提交点、事务日志和回滚 |
| CreateApiClient 固定创建 127.0.0.1 客户端 | 不能表达多个目标、独立认证和会话代际 | 引入 EndpointDescriptor、EndpointSession 与工厂 |
| ApplyProgramOverridesCoreAsync 同时修改 controller 与本机网络状态 | 对远程 controller 复用会导致远程配置变更或本机代理误联动 | 拆成目标命令与 LocalDeviceCoordinator，远程路径不可达本机动作 |
| RuntimeSnapshot 混合本机设备、活动配置和 controller 数据 | UI 无法可靠区分“当前电脑”和“正在查看的目标” | 拆成 LocalDeviceSnapshot、ControllerSessionSnapshot 和 AppSnapshot |
| MihomoApiClient 单独创建 ClientWebSocket | REST 与 WebSocket 容易使用不同的证书策略 | 统一 EndpointTransportPolicy |
| UI 存在硬编码中文，AppSettings 没有语言字段 | 不能兑现 zh-CN / en-US 的 P1 要求 | 建立 MRT Core .resw 资源与语言设置 |
| 版本同时出现在 MSBuild、脚本、安装器和 UI | 预发布与正式产物可能版本漂移 | 以 Directory.Build.props 为单一版本源并做发布校验 |

## 3. 目标架构

~~~mermaid
flowchart LR
    UI[WinUI / Tray / View Models]
    APP[AppSnapshot Composer]
    LOCAL[LocalDeviceCoordinator]
    SWITCH[ConfigurationSwitchCoordinator]
    NET[NetworkContextMonitor]
    POLICY[NetworkSwitchPolicyEngine]
    ESM[EndpointSessionManager]
    SESSION[EndpointSession]
    CAP[EndpointCommandPolicy]
    API[MihomoApiClient]
    SERVICE[ClashTray Service]
    CORE[Local Mihomo]
    REMOTE[Remote Mihomo]
    STORE[Versioned Stores + DPAPI]

    UI --> APP
    APP --> LOCAL
    APP --> ESM
    NET --> POLICY
    POLICY --> SWITCH
    UI --> SWITCH
    SWITCH --> LOCAL
    LOCAL --> SERVICE
    SERVICE --> CORE
    ESM --> SESSION
    SESSION --> CAP
    CAP --> API
    API --> CORE
    API --> REMOTE
    NET --> STORE
    ESM --> STORE
    SWITCH --> STORE
~~~

### 3.1 分层规则

- Contracts 只保存跨边界的不可变数据与窄 IPC 契约，不依赖 Core、App 或 Service。
- Core 保存纯策略、存储、端点传输、会话和本机操作协调，不依赖 WinUI。
- App 保存 WinUI、资源加载、Windows 网络权限交互和界面组合，不向 Service 暴露新任意命令。
- Service 的 allow-list 不因远程端点增加而扩张；远程请求永远不经过 Service。
- UI 只能调用面向目标且需要 Capability 的命令入口，不能直接持有通用 MihomoApiClient 后绕过策略。

## 4. 核心领域模型

### 4.1 目标身份

建议增加以下概念：

- EndpointId：稳定 ID。本机使用保留值 local，远程使用随机 GUID。
- EndpointKind：Local 或 Remote。
- EndpointDescriptor：非秘密元数据，包括名称、根 URI 和传输安全模式。
- ControllerSessionGeneration：每次目标、认证或传输策略变化时递增的 64 位值。
- EndpointCapability：允许的命令标记。
- EndpointSessionState：Disconnected、Connecting、Connected、Reconnecting、AuthenticationFailed、CertificateFailed、Incompatible 或 Failed。

EndpointId 与配置 ID 是两套完全不同的标识。切换远程目标绝不能改变本机 ActiveConfigurationId。

### 4.2 快照拆分

建议用三个不可变快照替代继续扩张 RuntimeSnapshot：

LocalDeviceSnapshot：

- Service 与本机 Core 生命周期。
- 本机活动配置及配置列表。
- System Proxy 与 TUN 的实际状态及期望状态。
- 本机端口、核心版本、更新状态。
- 自动切换状态与最近一次切换结果。

ControllerSessionSnapshot：

- EndpointId、EndpointKind、显示名称和安全标识。
- 会话状态、代际、最后确认时间与错误类别。
- controller 版本、模式、流量、内存。
- 代理组、节点、Provider、规则、连接和日志。
- 当前能力集合。

AppSnapshot：

- LocalDeviceSnapshot。
- 当前 ControllerSessionSnapshot。
- 可选择目标摘要列表。
- 当前语言、主题及全局非秘密错误。

迁移期间可以保留 RuntimeSnapshot 作为兼容适配器，但新功能不得继续向它加入同时属于本机和远程的模糊字段。适配器在所有调用方迁移后删除。

### 4.3 能力模型

EndpointCommandPolicy 接收 EndpointKind、握手能力和请求命令，返回 Allow 或带稳定错误码的 Deny。允许能力是“目标类型静态允许项”和“端点实际支持项”的交集。

建议能力：

- ObserveStatus
- ObserveProxies
- ObserveProviders
- ObserveRules
- ObserveConnections
- ObserveLogs
- SwitchMode
- SwitchProxy
- TestDelay
- RefreshProvider
- CloseConnection
- ClearCache
- UpdateGeo
- ManageLocalConfiguration
- ControlLocalCore
- ControlSystemProxy
- ControlTun
- UpdateLocalCore
- OpenLocalDashboard

Remote 的后六个本机能力固定为 false。拒绝发生在任何 HTTP、WebSocket 或 Service 调用之前，并写入不含 secret 的审计日志。

## 5. 组件职责

| 组件 | 层 | 单一职责 |
| --- | --- | --- |
| LocalDeviceCoordinator | Core | 串行管理本机 Core、System Proxy、TUN、受管运行配置和 Service |
| ConfigurationSwitchCoordinator | Core | 执行配置预检、事务、提交、回滚和崩溃恢复 |
| ConfigurationSwitchJournal | Core | 持久化不含秘密的切换阶段和检查点 |
| EndpointStore | Core | 原子读写远程端点非秘密元数据 |
| EndpointSecretStore | Core | 用 DPAPI CurrentUser 保存和删除 secret |
| EndpointCertificateStore | Core | 保存逐端点自定义 CA 公钥副本 |
| EndpointUriNormalizer | Core | 解析、规范化和拒绝危险 URI |
| EndpointTransportFactory | Core | 从一份策略创建 HttpClient 与 ClientWebSocket |
| EndpointSession | Core | 管理单一目标的握手、快照、实时流、取消和代际 |
| EndpointSessionManager | Core | 选择活动目标、创建/释放会话、管理退避 |
| EndpointCommandPolicy | Core | 在命令执行前强制能力 allow-list |
| NetworkRuleStore | Core | 原子保存规则元数据和受保护 SSID |
| NetworkSwitchPolicyEngine | Core | 无副作用地把稳定网络上下文计算为切换决定 |
| INetworkContextSource | Core 契约 | 提供当前网络状态和变化事件，便于单元测试 |
| WindowsNetworkContextSource | App 基础设施 | 调用 Windows API，处理位置权限、多适配器和恢复事件 |
| AppSnapshotComposer | App | 聚合本机与活动会话快照，供 UI 绑定 |
| LocalizationService | App | 解析语言设置和非 XAML 资源，禁止翻译用户数据 |

WindowsNetworkContextSource 放在 App 侧，使 Core 的策略测试不依赖真实 Wi-Fi、位置权限或 UI 框架。若现有依赖更适合放入 Core 的 Windows 专用基础设施目录，也必须保留 INetworkContextSource 测试缝，并且不能让策略依赖 WinUI。

## 6. 配置切换事务

### 6.1 统一请求

ConfigurationSwitchRequest 至少包含：

- OperationId
- Source：Manual、SubscriptionRefresh、NetworkRule、NetworkDefault 或 Recovery
- TargetConfigurationId
- ExpectedNetworkRevision，可选
- CancellationToken

所有入口先构造请求，再进入同一个协调器。任何入口不得直接写 ActiveConfigurationId 或自行重启核心。

### 6.2 持久检查点

ConfigurationSwitchJournal 放在用户数据目录，包含：

- SchemaVersion
- OperationId
- Source
- Stage
- PreviousConfigurationId
- CandidateConfigurationId
- PreviousCoreIntent
- PreviousSystemProxyPreference 与最后确认状态
- PreviousTunPreference 与最后确认状态
- PreviousControllerGeneration
- StartedAtUtc

Journal 不保存订阅 URL、SSID、端点 secret、配置正文或代理凭据。每个阶段原子更新。事务成功或已安全回滚后删除；删除失败只产生清理警告，不重复执行已提交事务。

### 6.3 正常流程

~~~mermaid
sequenceDiagram
    participant Caller as Manual / Network Policy
    participant Switch as ConfigurationSwitchCoordinator
    participant Store as Config + Journal Store
    participant Local as LocalDeviceCoordinator
    participant Mihomo as Managed Mihomo

    Caller->>Switch: Switch(request)
    Switch->>Switch: Acquire local operation lock
    Switch->>Store: Resolve target and validate metadata
    Switch->>Mihomo: Validate generated candidate
    alt validation fails
        Switch-->>Caller: Rejected; old state unchanged
    else core was stopped
        Switch->>Store: Atomically commit active configuration
        Switch-->>Caller: Committed; core remains stopped
    else core was running
        Switch->>Store: Write journal + checkpoint
        Switch->>Local: Make owned System Proxy safe
        Switch->>Store: Promote candidate runtime config
        Switch->>Local: Restart managed core
        Switch->>Mihomo: Confirm generation + REST health + identity
        Switch->>Local: Confirm TUN and reapply desired System Proxy
        Switch->>Store: Commit active ID and clear journal
        Switch-->>Caller: Committed
    end
~~~

具体顺序：

1. 获取现有本机操作锁；检查是否已是目标配置。
2. 解析目标 profile，确认文件归属、大小、编码和元数据。
3. 在健康旧核心仍运行时生成 pending runtime 配置并完成 Mihomo 验证。
4. 捕获已确认的本机状态并写入 journal。
5. 若核心原本停止，原子保存 ActiveConfigurationId 并结束，不启动核心。
6. 若 ClashTray 拥有 System Proxy，先恢复原代理值但保留用户偏好，避免重启窗口出现死代理。
7. 把当前 active runtime 配置保存为受管回滚副本，原子提升 pending 配置。
8. 通过 LocalDeviceCoordinator 执行一次受控重启。
9. 只有新 controller 代际、REST 健康、配置身份和 TUN 实际状态均满足要求，才视为新核心健康。
10. 按已保存偏好恢复 System Proxy；再次从 Windows 读取并确认。
11. 最后提交 ActiveConfigurationId、发布新快照并清理 journal 和回滚副本。

在步骤 7 到 11 之间，UI 可以显示 Switching，但不能把 CandidateConfigurationId 标为已生效。

### 6.4 失败和回滚

- 步骤 3 之前失败：删除 pending 文件，旧状态完全不变。
- 步骤 4 到 7 失败：恢复运行配置文件，保持旧核心，清理 journal。
- 新核心未健康：确保 owned System Proxy 不指向不可用端口，恢复旧 runtime 配置并启动旧核心。
- 旧核心恢复健康：恢复旧 TUN/System Proxy 偏好，保留 PreviousConfigurationId，报告“切换失败，已恢复”。
- 旧核心也无法恢复：System Proxy 保持关闭；TUN 必须由 Service 确认关闭或明确进入 Failed；保留 journal 与诊断 ID，报告高优先级错误。
- 取消只在安全点生效。开始提升 runtime 配置后，协调器必须完成提交或回滚，不能直接抛出取消让机器停在中间状态。

### 6.5 启动恢复

App 启动发现未完成 journal 时：

1. 先按现有 ownership 逻辑确保 System Proxy 不指向未确认核心。
2. 查询 Service、进程和 controller 实际状态。
3. 根据 journal Stage 和 runtime 文件标记决定完成提交或回滚。
4. 无法证明候选已健康时优先回滚。
5. 恢复结束后才启动订阅计划、SSID 监听和远程实时流。

恢复过程必须幂等；App 连续崩溃或重启不能重复交换文件。

## 7. SSID 自动切换设计

### 7.1 网络上下文

NetworkContext 是一次不可变观察：

- Revision
- ObservedAtUtc
- ConnectivityKind：None、Ethernet、WiFi 或 Other
- CurrentSsidProtected，仅在边界内短时解密比较
- InterfaceIdentity，仅用于当次多接口消歧，不持久化
- PermissionState
- IsAmbiguous

WindowsNetworkContextSource 只查询当前连接，不扫描网络。Windows 对 Wi-Fi 信息访问可能要求精确位置授权；AccessDenied 是预期产品状态，不作为未处理异常。

### 7.2 规则模型

NetworkRuleStore 建议保存：

- SchemaVersion
- Enabled
- DefaultConfigurationId，可选
- Rules：RuleId、ProtectedSsid、ConfigurationId、Enabled、CreatedAtUtc、UpdatedAtUtc

SSID 使用 DPAPI CurrentUser 加密。保存前以解密后的精确值检查唯一性；日志和错误只使用 RuleId。配置被删除时规则保留为 Disabled + MissingTarget，避免静默改指向其他配置。

### 7.3 决策优先级

NetworkSwitchPolicyEngine 是纯函数，输入当前上下文、规则、活动配置、手动覆盖、冷却状态和配置有效性，输出：

1. NoAction：功能关闭、权限不足、网络模糊、处于有效手动覆盖或目标已生效。
2. SwitchToMappedConfiguration：精确 SSID 命中唯一有效规则。
3. SwitchToDefaultConfiguration：没有命中且设置了有效默认配置。
4. KeepCurrentWithWarning：规则目标无效、存储损坏或没有默认值。

优先级为：安全状态阻止切换 > 手动覆盖 > 精确规则 > 显式默认配置 > 保持当前。

### 7.4 事件管线

~~~mermaid
flowchart TD
    E[Windows 网络事件 / Resume] --> C[读取最新 NetworkContext]
    C --> D[5 秒稳定防抖]
    D --> P[纯策略计算]
    P -->|NoAction| S[发布可解释状态]
    P -->|Switch| X{冷却与目标检查}
    X -->|目标相同或冷却| L[只保留最新 revision]
    X -->|允许| T[ConfigurationSwitchCoordinator]
    T --> R[提交或回滚]
    R --> K[30 秒冷却后重评最新 revision]
~~~

实现建议使用容量为 1 的 latest-wins channel。事件处理器不直接重启核心。Resume 事件先触发稳定等待；防抖期间只替换待评估上下文。

### 7.5 手动覆盖

- 自动切换开启时，用户手动选择本机配置会创建内存中的 ManualOverride。
- 覆盖与当时 NetworkRevision 绑定，不持久化 SSID 或历史。
- SSID 改变、Resume、App 重启、自动开关重置或用户点击恢复自动时清除。
- 清除后立即读取最新网络并走防抖；不直接假设旧观察仍有效。

## 8. 远程端点设计

### 8.1 地址规范化

EndpointUriNormalizer 执行：

- 只接受 http 或 https。
- 必须包含合法主机；端口范围 1 到 65535。
- 路径必须为空或 /。
- 拒绝 user-info、query、fragment、控制字符和超长 URI。
- 域名使用一致的 IDN 规范化；IPv6 使用标准方括号格式。
- 保存规范化后的 origin，并在 UI 中显示 scheme、host、port。

0.3.0 不支持反向代理 path prefix。需要该能力时应作为后续明确设计，而不是把任意路径直接拼接到 API 请求。

### 8.2 存储

EndpointStore 的非秘密记录：

- SchemaVersion
- EndpointId
- DisplayName
- NormalizedBaseUri
- TransportSecurityMode：HttpsSystemTrust、HttpsCustomCa 或 HttpExplicit
- SecretReference，可选
- CustomCaReference，可选
- InsecureHttpAcknowledgedAtUtc，仅 HTTP
- CreatedAtUtc、UpdatedAtUtc

EndpointSecretStore 使用 ProtectedDataScope.CurrentUser。secret 长度、解密错误和空值有明确结果类型。DPAPI blob 不与错误详情拼接。普通设置导出只包含非秘密元数据，并且 0.3.0 默认不实现端点导出。

EndpointCertificateStore 只接收不含私钥的 X.509 CA 证书，使用端点 ID 命名，不信任用户提供的文件名。删除端点时按精确 ID 清理。

### 8.3 统一传输策略

EndpointTransportPolicy 是 HttpClient 与 ClientWebSocket 的唯一输入，包含：

- 规范化 base URI。
- secret 的短生命周期内存表示。
- TLS 验证器。
- UseProxy=false，避免 controller 流量绕回当前 System Proxy 或把授权头交给系统代理。
- REST 超时与 WebSocket 握手超时。
- EndpointId 与 SessionGeneration。

MihomoApiClient 不再自行决定地址、Authorization 或证书回调。它接收由 EndpointTransportFactory 创建的 transport。Authorization 使用请求头；空 secret 时完全省略该头。

REST 和 WebSocket 均须验证：

- 系统信任模式：使用 Windows 正常链、主机名、有效期和用途校验。
- 自定义 CA 模式：只在该端点的自定义根集合中重建链；仍拒绝名称不匹配、过期、用途错误、吊销策略失败或额外的 SslPolicyErrors。
- HTTP 模式：不执行 TLS，但必须存在该端点的有效风险确认；不能从 HTTPS 错误自动降级。

不得实现返回 true 的通用证书回调，不得修改 CurrentUser 或 LocalMachine 根证书库。

### 8.4 会话生命周期

EndpointSessionManager 始终有一个本机描述符，并按需创建一个活动 EndpointSession：

1. 用户选择目标。
2. 取消前一个 session 的 REST 请求与 WebSocket。
3. 递增全局选择 revision，并创建新 generation。
4. 只读调用 /version 和必要兼容性接口。
5. 成功后发布 Connected 快照并启动当前页面需要的实时流。
6. 失败时按错误类型决定是否退避；认证和证书错误等待用户修改，不自动反复请求。
7. 暂时网络错误使用 1、2、5、10、30 秒上限的带抖动退避。
8. 每个响应提交前核对 EndpointId、generation 和选择 revision。

非活动端点只保留元数据和最近一次显式测试结果，不维持后台 WebSocket。

### 8.5 命令路径

所有 controller 写操作使用：

UI 意图 → TargetCommand → EndpointCommandPolicy → 当前 EndpointSession → MihomoApiClient。

TargetCommand 显式携带 EndpointId、ExpectedGeneration、Capability 和参数。策略拒绝、代际不符或 session 非 Connected 时不发送请求。

本机特权路径使用：

UI 意图 → LocalDeviceCommand → LocalDeviceCoordinator → Service / Windows。

两个命令类型不得共用“任意 URL + 任意动作”的通用方法。即使 UI 发生错误，也不能构造一个 Remote TargetCommand 进入 LocalDeviceCoordinator。

## 9. 本地化设计

### 9.1 资源布局

建议使用 WinUI 3 MRT Core 标准布局：

- src/ClashTray.App/Strings/zh-CN/Resources.resw
- src/ClashTray.App/Strings/en-US/Resources.resw

XAML 静态文本优先使用 x:Uid；代码生成的状态、错误、通知和托盘菜单通过 ResourceLoader 使用稳定资源键。资源键描述语义，不包含某一种语言文本。

Mihomo 返回的节点名、规则、Provider、配置名和原始日志保持原样。领域层错误使用稳定 ErrorCode 与结构化参数，App 层最后一步本地化，不让 Core 保存最终中文或英文句子。

### 9.2 语言应用

- AppSettings 增加 Language：system、zh-CN 或 en-US。
- system 根据 Windows 首选语言选择资源，无法匹配时回退到 en-US，再回退默认资源。
- 0.3.0 以可靠性优先：如果 MRT Core 不能对已创建窗口完整刷新，保存设置并提示“下次启动 App 生效”；不自动重启 Core 或 Service。
- 启动时在创建任何窗口、托盘菜单或通知之前设置语言覆盖。

### 9.3 资源完整性

自动化测试解析两份 resw：

- 资源键集合一致。
- 值非空且没有重复键。
- 格式化占位符集合一致。
- 安全错误、目标身份、开关名称和可访问性名称属于必需键。
- 禁止在关键 XAML 与菜单构造代码中新增未豁免的硬编码用户文本。

构建产物需检查 PRI 和两种语言资源真实存在，不能只让源代码测试通过。

## 10. 版本与发布元数据

Directory.Build.props 中的 VersionPrefix 是产品版本单一来源。其他位置改为：

- App 关于页读取程序集信息，不保存硬编码版本。
- packaging/Build-EXE.ps1 的 PackageVersion 默认从 MSBuild 读取；显式参数必须与项目版本兼容。
- Inno Setup 只接收脚本传入的已验证版本。
- GitHub release workflow 校验 tag、MSBuild 版本、安装器文件名和 release-manifest.json。
- 预发布标签通过 VersionSuffix 或显式发布参数生成，不把 alpha 版本写死在 UI。

发布校验发现任一版本不一致时立即失败。

## 11. 数据文件与迁移

### 11.1 建议文件

| 文件 | 所有者 | 内容 | 失败隔离 |
| --- | --- | --- | --- |
| settings.json | 当前用户 | 既有设置、Language、新功能总开关 | 保留既有损坏隔离策略 |
| network-rules.json | 当前用户 | 规则元数据、DPAPI SSID、默认配置 | 只禁用自动切换 |
| remote-endpoints.json | 当前用户 | 非秘密端点元数据 | 只隐藏远程列表 |
| secrets/endpoint-{id}.bin | 当前用户 | DPAPI secret | 单端点认证失败 |
| certificates/endpoint-{id}.cer | 当前用户 | 不含私钥的逐端点 CA | 单端点 TLS 失败 |
| configuration-switch-journal.json | 当前用户 | 不含秘密的切换检查点 | 启动时先执行安全恢复 |
| ProgramData runtime pending / rollback 文件 | Service 与受管用户 | 已验证的短期运行配置 | 完成提交或回滚后清理 |

实际路径由 AppPaths 生成，禁止在业务代码散布字符串路径。

### 11.2 存储 DTO

持久化 DTO 与领域 record 分离，每个文件包含 SchemaVersion。这样给 AppSettings 或快照增加字段时，不把构造器顺序意外变成永久磁盘协议。

写入步骤：

1. 在同目录创建随机临时文件。
2. 限制大小并使用 UTF-8 无 BOM。
3. Flush 后以原子替换提交。
4. 必要时保留一个上一版本备份。
5. 失败时删除已知临时文件，不覆盖原文件。

读取遇到无效 JSON、超限或字段错误时移动为带时间戳的 corrupt 文件；权限与 I/O 错误保持原文件不动。

### 11.3 从 0.2.0 升级

- 先加载并验证原 settings.json，未知字段保留或通过显式 DTO 迁移。
- Language 缺失时设为 system。
- 自动切换缺失时设为 false。
- network-rules 与 remote-endpoints 缺失视为空，不创建虚假默认记录。
- ActiveConfigurationId、SystemProxyEnabled、TunEnabled 和订阅元数据保持原值。
- 新存储迁移失败不能阻止既有 System Proxy 所有权恢复。
- 迁移只执行本地数据转换，不连接远程端点、不读取 SSID、不重启核心。

## 12. 并发、超时与资源上限

| 项目 | 设计 |
| --- | --- |
| 本机变更 | 复用一个异步操作锁；配置切换、生命周期、TUN、端口、更新互斥 |
| 网络事件 | 容量 1 的 latest-wins 队列；5 秒防抖，30 秒冷却 |
| 会话请求 | 每次请求带 EndpointId 和 generation；提交前再次核对 |
| 连接测试 | 默认 10 秒总预算，只读 |
| REST 普通读取 | 默认 10 秒；特殊长操作使用显式预算 |
| Controller 写操作 | 默认 15 秒，并服从用户取消 |
| WebSocket 握手 | 10 秒；断线最大 30 秒退避并带抖动 |
| REST 响应 | 单响应默认最大 8 MiB，按接口可进一步收紧 |
| WebSocket 消息 | 单消息默认最大 256 KiB，超限关闭该流并报告 |
| 活动端点 | 同时最多一个实时会话 |
| 端点数量 | 最多 32 |
| 显示名称 | 最多 64 个 Unicode 标量值，去除控制字符 |
| URI / secret | URI 最大 2048 字符；secret 最大 4096 字符 |
| 日志 | 每个活动会话最多 500 条，连续重复项折叠 |

超时和上限集中为可测试配置，不散布魔法数字。真实 Mihomo 大数据场景验证后可以调优，但不得取消上限。

## 13. 错误、日志与诊断

新增稳定错误类别：

- NetworkPermissionRequired
- NetworkAmbiguous
- NetworkRuleTargetMissing
- ConfigurationValidationFailed
- ConfigurationSwitchFailedRolledBack
- ConfigurationSwitchRollbackFailed
- EndpointUriInvalid
- EndpointAuthenticationFailed
- EndpointCertificateFailed
- EndpointIncompatible
- EndpointCapabilityDenied
- EndpointStaleResult
- EndpointTransportFailed
- StoreMigrationFailed

Core 记录 ErrorCode、OperationId、EndpointId 或 RuleId、阶段、耗时和脱敏异常类别。App 根据 ErrorCode 本地化。不得记录：

- 原始 SSID。
- secret 或 Authorization。
- 含 query 的订阅 URL。
- DPAPI 明文或 blob。
- 配置正文。
- 自定义 CA 文件原始路径中的用户名。

证书诊断可以记录证书 thumbprint、subject 的有界摘要、失败策略和端点 ID，但不能提供“忽略并继续”动作。

## 14. 渐进实施策略

不要一次性重写 ClashTrayRuntime。建议顺序：

1. 为现有本机启动、停止、配置切换、System Proxy、TUN 和会话代际补特征测试。
2. 引入新快照与适配器，保持 UI 行为不变。
3. 提取 LocalDeviceCoordinator，不改变 Service 协议。
4. 引入 ConfigurationSwitchCoordinator，让现有手动切换先走新事务。
5. 提取 EndpointTransportFactory 与本机 EndpointSession，仍只连接 loopback。
6. 用 EndpointCommandPolicy 包住现有 controller 写操作。
7. 在本机路径稳定后接入 SSID。
8. 最后允许创建 Remote EndpointSession。
9. 迁移 UI 到 AppSnapshot 后删除兼容适配器和不可达旧路径。

每一步都应能构建、测试并保持本机日常路径可用。

## 15. 建议代码落点

精确命名可按仓库风格调整，但职责不可重新混合：

~~~text
src/ClashTray.Contracts/
  EndpointContracts.cs
  NetworkSwitchContracts.cs
  StateContracts.cs

src/ClashTray.Core/
  LocalDeviceCoordinator.cs
  ConfigurationSwitchCoordinator.cs
  ConfigurationSwitchJournal.cs
  Endpoints/
    EndpointStore.cs
    EndpointSecretStore.cs
    EndpointCertificateStore.cs
    EndpointUriNormalizer.cs
    EndpointTransportFactory.cs
    EndpointSession.cs
    EndpointSessionManager.cs
    EndpointCommandPolicy.cs
  Network/
    INetworkContextSource.cs
    NetworkRuleStore.cs
    NetworkSwitchPolicyEngine.cs

src/ClashTray.App/
  Infrastructure/
    WindowsNetworkContextSource.cs
  Strings/
    zh-CN/Resources.resw
    en-US/Resources.resw
  Presentation/
    AppSnapshotComposer.cs

tests/ClashTray.Core.Tests/
  ConfigurationSwitchCoordinatorTests.cs
  EndpointCommandPolicyTests.cs
  EndpointTransportPolicyTests.cs
  EndpointUriNormalizerTests.cs
  NetworkSwitchPolicyEngineTests.cs
  VersionedStoreMigrationTests.cs

tests/ClashTray.IntegrationTests/
  LocalMihomoSessionTests.cs
  RemoteEndpointTlsTests.cs
  RemoteEndpointWebSocketTests.cs
  ConfigurationSwitchRecoveryTests.cs
~~~

若当前项目的目录约定不同，可以合并文件夹，但不得把 Windows 权限 UI、纯策略和网络传输重新塞回单个巨型运行时类。

## 16. 安全边界复核

0.3.0 完成时应仍有三个本机信任边界：

1. Desktop App：用户设置、端点、订阅、System Proxy、UI。
2. Windows Service：受限 IPC、本机核心生命周期、TUN、受管 ProgramData。
3. Local Mihomo：loopback controller。

Remote Mihomo 是第四个不可信网络对端，不是第四个本机特权主体。它只能通过显式、受能力约束的 HTTP/WebSocket client 被访问，不能获得 Service pipe、任意文件路径、System Proxy 或 TUN 入口。

## 17. 参考资料

- [Microsoft：Wi-Fi 信息与位置访问变化](https://learn.microsoft.com/windows/win32/nativewifi/wi-fi-access-location-changes)
- [Microsoft：Windows App SDK / MRT Core 字符串本地化](https://learn.microsoft.com/windows/apps/windows-app-sdk/mrtcore/localize-strings)
- [Microsoft：HttpClientHandler 证书校验回调](https://learn.microsoft.com/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback)
- [Microsoft：X509ChainPolicy TrustMode](https://learn.microsoft.com/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.trustmode)
- [MetaCubeX：Mihomo API](https://wiki.metacubex.one/en/api/)

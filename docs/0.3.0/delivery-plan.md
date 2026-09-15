# ClashTray 0.3.0 交付与质量计划

> 状态：0.3.0 Beta 收口；SSID/Wi-Fi 功能明确顺延
> 对应需求：[产品需求](product-requirements.md)
> 对应设计：[技术设计](technical-design.md)

> **Beta 范围声明：** 0.3.0 不包含 Wi-Fi/SSID 配置切换、SSID 读取、网络变化监听、网络规则编辑或位置权限处理。SSID 相关工作项和验收项仅保留为后续版本参考，不是本 Beta 的发布门禁。

## 1. 交付策略

0.3.0 按“先保护本机路径，再增加自动化，最后接入基础远程”的顺序开发。Wi-Fi/SSID 自动切换从本版本切除并整体顺延。任何里程碑都必须保持仓库可构建、相关测试可运行、本机日常代理路径可用。

版本序列建议：

| 版本 | 目标 |
| --- | --- |
| 内部基线 | 本地化、数据版本化、测试夹具和单一版本源 |
| 0.3.0-alpha.1 | 本机状态 / controller 会话拆分，事务化手动配置切换 |
| 0.3.0-alpha.2 | 本机 P0 路径与受管 Mihomo 互操作收口 |
| 0.3.0-alpha.3 | 基础远程 Mihomo 端点纵向切片 |
| 0.3.0-beta.1 | 集成、迁移、安全、性能与 UI 硬化；SSID 明确不包含在内 |
| 0.3.0-rc.1 | Windows 与安装包矩阵候选版 |
| 0.3.0 | 所有门禁通过后的正式版 |

每个 alpha 都必须是可以真实运行的纵向切片，不发布只有静态 UI 或未接通安全边界的空壳。

## 2. 计划基线

规划审查时的已知基线：

- 目标平台为 Windows 10 22H2 / Windows 11 x64。
- 当前发行形态为 Full、NoCET、Mini 三种 Inno Setup x64 安装包。
- 现有本机 controller 固定在 127.0.0.1，Service IPC 和受管核心路径已经形成安全边界。
- src/ClashTray.Core/ClashTrayRuntime.cs 是 0.3.0 最大的变更集中点，应以特征测试和渐进提取降低风险。
- tests/ClashTray.IntegrationTests 当前覆盖较少，需要从契约边界测试扩展到真实 Mihomo、TLS/WebSocket、迁移和切换恢复。
- 2026-09-13 规划审查运行以下基线命令，Core 96 项、Integration 5 项，共 101 项测试通过：

~~~powershell
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64
~~~

上述结果是规划时基线，不替代实现完成后的重新验证。

## 3. 依赖关系

~~~mermaid
flowchart LR
    V[版本源 + 数据迁移]
    L[zh-CN / en-US 资源]
    T[快照与目标模型]
    C[配置切换事务]
    E[统一 Endpoint Transport]
    R[远程端点]
    H[硬化与发布]

    V --> T
    L --> S
    L --> R
    T --> C
    T --> E
    E --> R
    C --> R
    R --> H
~~~

0.3.0 Beta 的关键路径是：目标模型 → 配置切换事务，以及目标模型 → 统一传输 → 远程端点。SSID 自动切换不是本版本关键路径，相关网络上下文和权限工作整体顺延；远程功能不得在能力隔离和统一 TLS 完成前抢跑。

## 4. 里程碑计划

### M0：基线冻结与 P1 收口

建议工期：3–5 个工程日
产物：内部基线

交付内容：

- 保存完整 build/test 基线和关键本机流程验收记录。
- 为 ClashTrayRuntime 的启动、停止、重启、配置选择、System Proxy、TUN 和 controller 代际增加特征测试。
- 以 Directory.Build.props 为版本单一来源，消除脚本与 UI 的 0.2.0 硬编码。
- 建立 zh-CN / en-US MRT Core 资源、语言设置和资源完整性测试。
- 引入版本化存储 DTO、原子写入公共设施和 0.2.0 迁移夹具。
- 为新错误定义稳定 ErrorCode，并确认现有脱敏器覆盖端点与网络规则数据。

退出标准：

- 现有 101 项测试继续通过，新特征测试通过。
- 两种语言资源在 Debug、Publish 和至少一个安装包中可加载。
- tag、程序集、安装器参数、文件名不一致时构建失败。
- 从代表性 0.2.0 settings fixture 升级不改变已有网络偏好。

### M1：运行时边界与事务化切换

建议工期：7–10 个工程日
产物：0.3.0-alpha.1

交付内容：

- 新增 LocalDeviceSnapshot、ControllerSessionSnapshot、AppSnapshot 和兼容适配器。
- 提取 LocalDeviceCoordinator，保持 Service 命令面不变。
- 引入 EndpointKind、EndpointId、SessionGeneration 和 EndpointCommandPolicy。
- 把本机 Mihomo 先包装成唯一的 Local EndpointSession。
- 引入 ConfigurationSwitchCoordinator、journal、pending runtime 配置和启动恢复。
- 让手动配置选择及活动订阅更新通过统一事务。
- 把 ApplyProgramOverridesCoreAsync 中的本机动作与 controller 动作拆开。

退出标准：

- Core 层测试证明 Remote kind 无法构造本机 Service、System Proxy、TUN 或核心更新动作。
- 配置在核心停止时切换不会启动核心。
- 配置在核心运行时的成功、预检失败、新核心失败且回滚成功、回滚失败、App 中途重启五条路径有自动化或受控集成证据。
- ActiveConfigurationId 只在提交点改变。
- System Proxy 在任何失败路径都不指向未确认可用的端口。
- 0.2.0 本机 UI 主流程没有可见回退。

### M2：Wi-Fi / SSID 自动切换（延期，不属于 0.3.0）

本里程碑从 0.3.0 移出，整体顺延到后续版本。0.3.0 Beta 不创建 `WindowsNetworkContextSource`，不读取 SSID，不监听网络事件，不提供网络规则编辑，不申请或引导位置权限；`INetworkContextSource`、`NetworkRuleStore`、策略引擎及其测试仅作为未来版本设计/代码预留，不计入本 Beta 交付。

后续版本重新立项时，仍需单独验证权限、抖动、恢复、多接口、手动覆盖、规则存储和配置切换回滚；不得把本 Beta 的 UI 隐藏或合成 smoke 结果解释为这些能力已交付。

### M3：远程 Mihomo 端点

建议工期：8–12 个工程日
产物：0.3.0-alpha.3

交付内容：

- EndpointStore、EndpointSecretStore、EndpointCertificateStore 和损坏隔离。
- EndpointUriNormalizer 与最多 32 个端点限制。
- HTTPS 系统信任、逐端点自定义 CA、显式 HTTP 例外。
- EndpointTransportFactory，同源创建 HttpClient 与 ClientWebSocket。
- EndpointSessionManager、目标选择、代际取消和有界退避。
- 远程目标标识、端点编辑/测试/删除 UI。
- 远程只读视图与允许的模式、节点、延迟、Provider、连接、缓存和 Geo 操作。
- Core 能力拒绝测试和 secret / URI / 证书日志脱敏。

退出标准：

- 正确系统证书、未知 CA、自定义 CA、过期证书、名称错误、错误用途和 HTTP 例外均按设计处理。
- 同一 TLS 矩阵同时覆盖 REST 与 WebSocket。
- secret 不在 URL、日志、错误、普通备份和测试快照中。
- 远程命令无法触发任何 Service IPC 或本机网络状态改变。
- 切换目标、删除目标和快速重连时旧代际数据不会覆盖当前 UI。
- 远程端点完全不可用时，本机目标和托盘快捷动作仍完整可用。

### M4：集成、性能与体验硬化

建议工期：5–8 个工程日
产物：0.3.0-beta.1

交付内容：

- 扩展真实 Mihomo 集成测试和本地 HTTPS/WSS 测试服务器。
- 对大代理组、大连接列表、日志洪水、慢响应、半开 WebSocket 和取消竞态做压力验证。
- 检查 UI 键盘操作、屏幕阅读器、高对比度、200% DPI、长中文/英文/端点名。
- 测试新存储损坏、DPAPI 解密失败、证书删除失败和磁盘写入失败。
- 补齐用户文档、安全警告、设置说明和发布说明草稿。
- 静态审查所有 Service 调用点和证书验证回调。

退出标准：

- 没有无界集合、无界重试或 UI 线程网络解析。
- 所有远程命令都能追溯到 EndpointCommandPolicy。
- 所有配置选择都能追溯到 ConfigurationSwitchCoordinator。
- 用户可见文本资源完整，关键界面没有新增硬编码文本。
- Beta 连续日常使用期间没有代理残留、重启风暴或跨目标状态污染。

### M5：发布候选与正式发布

建议工期：5–7 个工程日，加至少 3–7 天候选版观察
产物：0.3.0-rc.1 → 0.3.0

交付内容：

- 在 Windows 10 22H2 与 Windows 11 x64 上执行完整验收矩阵。
- 构建 Full、NoCET、Mini，验证版本、哈希、第三方清单、升级与卸载。
- 验证 0.2.0 → 0.3.0 升级和保留数据的回退流程。
- 完成安全复核、已知限制、发行说明和支持排障文档。
- 对 RC 期间发现的问题只接受修复、测试和文档变更，不再扩范围。

退出标准：

- 第 8 节的全部发布门禁通过。
- 第 9 节没有开放的停止发布项。
- 正式 tag、程序集、三种安装器、SHA-256 sidecar 和 release manifest 版本一致。
- 候选版观察期没有新的 P0/P1 回归或 0.3.0 安全缺陷。

## 5. 工作分解与需求追踪

| 工作项 | 内容 | 需求 |
| --- | --- | --- |
| W-030-001 | 版本单一来源与发布一致性检查 | BAS-002 |
| W-030-002 | 设置 DTO、schema、迁移与原子存储 | BAS-003、BAS-004 |
| W-030-003 | zh-CN / en-US 资源与语言设置 | LOC-001 至 LOC-006 |
| W-030-004 | 快照拆分与 AppSnapshot 适配 | BAS-001、REM-015 |
| W-030-005 | LocalDeviceCoordinator | SWI-002、REM-013、REM-014 |
| W-030-006 | EndpointCommandPolicy | REM-012 至 REM-014 |
| W-030-007 | 配置切换事务、journal 与恢复 | SWI-001 至 SWI-010 |
| W-030-008 | 网络上下文与权限适配（后续版本） | 延期的 NET-001、NET-002、NET-010 至 NET-012 |
| W-030-009 | 规则存储与纯策略（后续版本） | 延期的 NET-003 至 NET-006、NET-013 至 NET-015 |
| W-030-010 | 网络事件管线和手动覆盖（后续版本） | 延期的 NET-007 至 NET-009 |
| W-030-011 | 端点 URI、元数据、secret 与 CA 存储 | REM-001 至 REM-006、REM-016 |
| W-030-012 | 统一 HTTP/WebSocket transport | REM-007 至 REM-010 |
| W-030-013 | EndpointSessionManager 与远程快照 | REM-009 至 REM-011、REM-015、REM-018 |
| W-030-014 | 远程目标 UI 与允许命令 | REM-012 至 REM-018 |
| W-030-015 | 集成、安全、性能与安装矩阵 | 全部 |
| W-030-016 | 用户文档、发布说明与回退说明 | 全部 |

每个合并请求或变更批次至少标注一个工作项和对应需求编号，避免“做了功能但无法证明满足哪个验收条件”。W-030-008 至 W-030-010 在 0.3.0 Beta 中保持延期状态，不得作为当前版本已交付功能统计。

## 6. 测试计划

### 6.1 单元测试

必须覆盖（SSID 相关项仅为后续版本参考，不是 0.3.0 Beta 门禁）：

- 后续版本再覆盖 NetworkSwitchPolicyEngine 的所有状态与优先级组合；0.3.0 Beta 不把它作为交付项。
- EndpointUriNormalizer 的有效 URI、危险 URI、IDN、IPv6 和边界长度。
- EndpointCommandPolicy 的 Local / Remote 全能力矩阵和未知命令默认拒绝。
- TLS policy 对系统信任、自定义根、名称、有效期、用途和其他错误的判断。
- 会话 generation 和选择 revision 对陈旧响应的拒绝。
- ConfigurationSwitchCoordinator 的阶段、取消安全点、提交、回滚和 journal 恢复。
- DPAPI store 的 round-trip、错误分类和日志脱敏。
- schema 迁移、损坏隔离、原子写失败和未知字段。
- 两种语言资源键、占位符与必需资源完整性。
- 版本解析与 tag / artifact 一致性。

### 6.2 组件与集成测试

建立以下可重复夹具：

1. 真实受管 Mihomo：使用固定测试版本和最小安全配置，覆盖 REST、WebSocket、重启和配置身份。
2. 本地 HTTP/HTTPS/WSS 测试服务器：动态生成临时 CA 与证书，不安装到系统根证书库。
3. 后续版本使用假 NetworkContextSource 确定性重放 Wi-Fi、Ethernet、权限、多接口、抖动和 Resume 序列；0.3.0 Beta 不创建生产网络上下文源。
4. 临时 AppPaths：验证存储、迁移、损坏、journal 和清理，不触碰真实用户数据。
5. 假 ServiceClient：记录调用顺序并断言 Remote 命令永远不产生 Service 请求。

集成测试至少包含：

- 新配置健康、失败、超时、进程崩溃、controller 迟到和回滚。
- System Proxy 已拥有、被第三方修改和恢复失败。
- TUN 开启状态下的配置切换成功与失败。
- REST 成功但 WebSocket 证书失败的防分叉测试。
- WebSocket 半消息、超大消息、断线、重连和旧流晚到。
- 远程端点更新 secret 或 CA 后旧 session 立即失效。
- 删除活动端点时所有未完成请求取消。

### 6.3 真实 Windows 验收

以下行为不能只用 mock 宣称完成：

- Windows 位置权限、Wi-Fi/SSID 连接切换、多网卡和 Ethernet 自动切换属于后续版本，不纳入 0.3.0 Beta 实机验收。
- 本机核心的睡眠恢复和网络适配器变化仍按已有 P0/P1 网络安全范围单独验收，不启用 SSID 自动切换。
- System Proxy 注册表实际状态、所有权冲突和 WinINet 通知。
- TUN 与 Service 的实际状态。
- Explorer 重启后的托盘恢复。
- Windows 10 / 11 的 PRI 资源、通知、对话框与网络 API。
- 多显示器、各任务栏位置、100%/150%/200% DPI、高对比度。
- Full、NoCET、Mini 的安装、升级、卸载和依赖提示。

每次手工验收记录系统版本、安装包、步骤、结果、日志诊断 ID 和未验证项，不记录真实 SSID 或 secret。

### 6.4 回归测试

每个里程碑重新执行 AGENTS.md 的既有发布场景，重点包括：

- 无效 YAML 和订阅不可用。
- controller / 端口冲突。
- 核心崩溃与日志洪水。
- UI 重启而 Service/Core 仍运行。
- Service 重启。
- System Proxy 所有权冲突。
- TUN 失败与回滚。
- 本机核心的睡眠恢复与网络适配器变化；SSID 自动切换恢复顺延。
- Explorer 重启。
- 首次安装、升级、卸载保留/删除数据。

## 7. CI 与构建门禁

### 7.1 每次变更

- dotnet restore
- dotnet build ClashTray.sln --configuration Debug --property:Platform=x64
- dotnet test ClashTray.sln --configuration Debug --property:Platform=x64
- 格式、编译警告、资源完整性、版本一致性和 secret 扫描
- 受影响的确定性集成测试

### 7.2 预发布

- Release 配置完整 build/test。
- Full、NoCET、Mini 三种产物构建。
- 安装器版本、架构、文件清单、Mihomo 与 MetaCubeXD 固定来源及 SHA-256 校验。
- 干净 VM 安装、0.2.0 覆盖升级、卸载保留数据与删除数据。
- Windows 10 22H2 / Windows 11 x64 真实验收。

### 7.3 质量规则

- 新编译警告必须处理或有具体注释说明，不使用广泛 suppress 掩盖问题。
- 对新功能的失败路径测试数量不能明显少于成功路径。
- 不允许依赖真实用户凭据、真实 SSID 或公网远程 controller 的 CI。
- 测试证书、secret 和配置必须随机生成在测试临时目录，结束后按精确路径清理。

## 8. 发布门禁

| 门禁 | 必须证据 |
| --- | --- |
| 范围 | 0.3.0 非目标没有被静默加入；需求追踪表完整 |
| 本机回归 | 现有单元/集成测试及核心本机手工路径通过 |
| 配置事务 | 成功、预检失败、回滚成功、回滚失败、崩溃恢复证据齐全 |
| SSID / Wi-Fi | 明确排除在 0.3.0 Beta；不读取 SSID、不监听网络变化、不提供网络规则或位置权限流程 |
| 远程安全 | Core 能力矩阵拒绝测试；无 Service IPC 泄漏 |
| TLS | REST/WSS 系统信任与自定义 CA 完整负向矩阵 |
| 隐私 | 日志、错误、备份和产物扫描无 secret、SSID、订阅凭据 |
| 迁移 | 代表性 0.2.0 数据升级；失败不破坏原数据或网络 |
| 本地化 | zh-CN/en-US 资源完整，PRI 在三种产物可用 |
| 性能 | 日志洪水、大集合、慢端点、反复断线下资源有界 |
| 平台 | Windows 10 22H2 / Windows 11 x64 通过 |
| 发行 | Full、NoCET、Mini 版本、哈希、清单一致 |
| 文档 | 用户说明、已知限制、排障和回退步骤已更新 |

任何门禁标记为“未验证”都不能等价为“通过”。

## 9. 停止发布条件

以下任一问题未关闭时，不得发布 0.3.0 正式版：

- 远程端点可以直接或间接触发本机 Service、核心生命周期、System Proxy、TUN 或本机核心更新。
- 新配置失败后留下指向停止端口的 ClashTray-owned System Proxy。
- 配置切换、网络抖动或 Resume 形成核心重启循环。
- 若后续版本重新启用 SSID，位置权限拒绝不得导致崩溃、循环弹窗或本机功能不可用；该项不是 0.3.0 Beta 门禁。
- secret、Authorization、订阅凭据或原始 SSID 出现在日志、错误、普通备份或发行产物。
- REST 和 WebSocket 使用不同的 TLS 或认证判断。
- 自定义 CA 绕过主机名、有效期、用途或其他证书错误。
- 旧远程 session 的结果覆盖当前目标。
- 0.2.0 升级丢失配置、订阅、System Proxy/TUN 偏好或破坏数据文件。
- 语言选择改变核心、代理、TUN、活动配置或活动目标。
- Full、NoCET、Mini 的功能、版本或数据格式不一致。
- 存在未复现、未解释的网络中断、路由残留或代理所有权覆盖。

## 10. 风险登记

| 风险 | 概率 | 影响 | 缓解 |
| --- | --- | --- | --- |
| ClashTrayRuntime 提取引入本机回归 | 高 | 高 | 先补特征测试；小步迁移；保留兼容适配器 |
| 后续版本重新引入 Windows SSID 权限时表现不一致 | 后续版本 | 高 | 本 Beta 不启用；重新立项时以权限状态和实机矩阵验收 |
| 后续版本快速网络事件触发重启风暴 | 后续版本 | 高 | 本 Beta 不启用；重新立项时使用 latest-wins、防抖、冷却和同目标去重 |
| 配置切换过程中 App 或 Service 崩溃 | 中 | 高 | 持久 journal、幂等启动恢复、代理先安全化 |
| REST 与 WSS 证书实现分叉 | 中 | 高 | 单一 transport policy；同一测试矩阵 |
| 远程命令越过能力边界 | 中 | 极高 | Core allow-list；类型分离；负向 Service 调用测试 |
| DPAPI 数据不可解密或被复制到另一用户 | 中 | 中 | 单端点错误；可删除重建；不阻止本机路径 |
| 本地化导致布局或资源缺失 | 中 | 中 | 资源键测试；长文本/DPI/高对比度验收 |
| 三种安装包版本漂移 | 中 | 高 | 单一版本源；发布前一致性失败门禁 |
| 工期不足导致安全折中 | 中 | 极高 | 整体顺延远程端点到 0.3.1，不删 TLS/回滚测试 |

## 11. 排期与范围切线

以一个主要开发流、现有代码可持续构建为假设，完整 0.3.0 预计需要 7–10 个工程周，另加 3–7 天 RC 观察。真实 Windows 设备、安装器和网络权限问题可能影响日历时间。

如果可用时间少于约 7 个工程周：

1. 保留 M0、M1，并将 SSID M2 整体顺延；0.3.0 只发布本机路径收口和已纳入范围的基础远程能力。
2. 若远程能力无法安全收口，则将远程端点整体移动到 0.3.1，但不通过重新加入 SSID 来填补范围。
3. 不允许只发布缺少自定义 CA 验证、能力隔离、WebSocket TLS 测试、迁移或 secret 保护的“半个远程功能”。

便携版、ARM64、历史流量、热键和第三语言不作为调换项进入空出的时间。

## 12. 发布与回退

### 12.1 发布前

- 冻结需求和数据 schema。
- 从干净 0.2.0 安装升级到 RC。
- 备份代表性 settings、配置和订阅元数据；确认备份不含解密 secret。
- 保存 0.2.0 安装器、哈希和回退说明。
- 发布说明明确 HTTP 端点风险，并明确声明 0.3.0 Beta 不包含 SSID/Wi-Fi 自动切换；SSID 权限和相同 SSID 无法区分等内容留待后续版本。

### 12.2 回退

- 0.3.0 新功能数据使用独立文件，0.2.0 不应读取或删除。
- 回退前先切回本机目标；SSID 自动切换未在 0.3.0 Beta 启用，不需要执行 SSID 自动切换关闭流程。
- 走正常卸载/安装流程，保留用户数据；不得用脚本删除 LocalAppData 或 ProgramData。
- 验证 0.2.0 能忽略新增 settings 字段并继续读取既有配置。若不能，发布前提供受测试的向下转换工具或明确阻止不安全降级。
- 回退不恢复远程 secret 到明文，也不删除用户未选择删除的数据。

### 12.3 正式发布后

- 只观察用户主动提供的本地日志，不新增遥测。
- 安全、代理残留、迁移和崩溃恢复问题优先于新功能。
- 需要紧急关闭远程功能时通过本地版本修复或用户开关，不依赖云端 kill switch。

## 13. 完成定义

0.3.0 只有在以下事项全部成立时才算完成：

- 代码实现覆盖 0.3.0 Beta 的正式需求，且没有通过 UI 隐藏替代 Core 安全边界；延期的 NET 需求不计入本版本完成定义。
- 自动化测试、真实 Windows 验收、安装包矩阵和升级测试全部有证据。
- 本机网络在每个失败场景下保持或恢复到已确认安全状态。
- 新存储有迁移、损坏隔离、原子写入和清理行为。
- 两种语言、三个安装包、两个 Windows 主版本行为一致。
- 文档与实际行为一致，已知限制清晰。
- Git 工作区没有意外生成物、secret、证书私钥、用户日志或订阅数据。
- 所有停止发布项关闭，RC 观察完成。

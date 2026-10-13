# 结构优化实施记录（2026-10-10）

范围来自 [代码结构审查报告](code-structure-review-2026-10-10.md) 的 S1–S8。用户已授权逐项实施，每个独立改动一个 commit，全部完成后推送 GitHub 并发布 alpha。保持现有功能范围、服务协议 3、网络恢复策略和发布变体。

起点：`f015e16` / 0.3.5，master 与 origin/master 一致。保留工作区原有未跟踪文件，只提交本轮明确列出的文件。当前执行会话 metadata 确认模型 `gpt-6.1-sol`、reasoning effort `max`。

基线：Release x64 全 solution 0 警告 / 0 错误；Core 758/758；隔离 Integration 32/32。原始证据见 `artifacts/structure-review-20261010`。正式服务、实际代理/TUN和机器安装状态不在本次结构重构的操作范围。

## 执行顺序

1. 固定 SDK / 依赖解析，按现有类型拆契约与并发策略文件。
2. S1：共享核心启动就绪确认。
3. S2：提取服务请求台账、更新处理边界。
4. S3：收敛生命周期、设置提交、配置操作上下文及退出资源所有权。
5. S4：提取设置页职责与窗口展示投影。
6. S5：显式测试组装、测试观察源与可引用展示逻辑边界。
7. S6：共用可选资源读取，收敛快照投影和日志发布。
8. S7/S8：模块目录、内部依赖约束与当前文档入口。
9. 完整 Release / Debug、官方核心 / 安全能力门禁、覆盖率与隔离 WinUI smoke；准备 0.3.6-alpha.1，推送并确认 GitHub Actions 产物和 prerelease。

## 分片结果

后续每片在对应 commit 中追加实现范围及实际验证。任何未完成的机器场景在 alpha 说明中明确记录，历史报告不作为本轮通过证据。

### 工具链选择

新增 `global.json`：SDK 10.0.400，只允许当前 feature band 的稳定补丁更新。CI 与 Release 的 setup-dotnet 统一读取该文件。完整 Release x64 构建 0 警告 / 0 错误；Core 758/758、隔离 Integration 32/32 通过。证据在 `artifacts/structure-optimization-20261010/sdk`。依赖锁文件在最终项目边界稳定后生成与验证。

### 按职责拆分契约和准入类型

`StateContracts.cs` 拆成状态、控制器数据、运行快照、设置、服务协议、运行绑定和服务消息文件；`OperationAdmission.cs` 按现有类型拆为六个文件。命名空间、协议 3、声明与实现逐字等价（忽略文件级空白和重复 namespace）。Release 构建 0 警告 / 0 错误；相关 Core 53/53、隔离 Integration 32/32。证据：`artifacts/structure-optimization-20261010/types`。

### S1 共用启动确认

新增 `CoreStartupReadiness`，集中期限、控制器抢占、有效端口 / TUN 初始 Off、监听观察、绑定构造及重试策略。本地和服务分别提供自己拥有的进程/API/OS 证据；服务边界保留 CoreStartException 映射与独立载荷校验。绑定提交前再次确认进程代际。新增 10 个故障 / 确认证据用例。Release 构建 0 警告 / 0 错误；相关 Core 63/63、隔离 Integration 32/32、校验官方 v1.19.31 的真实 Mihomo Integration 19/19。证据：`artifacts/structure-optimization-20261010/startup-readiness`。未修改正式网络或服务状态。

### S2 服务请求台账

新增 `ServiceRequestLedger`，只拥有指纹、加入执行、原子完成、TTL 和分组配额。协议 / 服务实例校验、可信清理资格、操作执行期限和网络副作用留在统一 dispatcher。新增纯台账用例验证进行中不失效、完成窗口、独立配额、取消等待及载荷/实例身份冲突。Release 构建 0 警告 / 0 错误；相关 Core 31/31、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/service-ledger`。

### S2 服务核心更新事务

`ServiceCoreUpdateHandler` 单独拥有下载、最小配置校验、安装取消/失败回滚与手动回滚，返回窄结果；dispatcher 保留协议响应和同一个生命周期/TUN gate。官方来源校验、路径策略、健康检查与回滚顺序沿用原实现。Release 构建 0 警告 / 0 错误；更新相关 Core 24/24、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/service-updates`。

### S3 设置提交与恢复归属

`SettingsCommitCoordinator` 集中当前偏好、持久化存储、恢复记录、字段合并、存储失败及 off-only 网络意图；普通设置、配置选择和网络开关共用这个提交入口。Runtime 继续拥有准入、系统代理/TUN 的真实状态及启动项回滚。既有故障注入证明关闭优先、外部同字段冲突保护、失败记录保留与恢复顺序。Release 构建 0 警告 / 0 错误；全量 Core 768/768、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/settings-commits`。

### S3 配置切换操作上下文

移除持有整个 Runtime 且共享可变 ActiveLease 的适配器。每次切换新建 `ConfigurationSwitchOperations`，持有该次调用的不可变 lease、窄存储和明确副作用委托；订阅刷新只依赖状态采集委托。提交/阶段记录/健康检查/回滚仍在原操作锁内。Release 构建 0 警告 / 0 错误；配置/订阅/设置/并发 Core 52/52、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/configuration-context`。

### S3 本地生命周期事务

`CoreLifecycleCoordinator` 拥有 `LocalCoreStartupTransaction`：本地配置重建、有限端口重试、进程身份、API 观察、就绪绑定和失败回收。Runtime 只取得确认结果后接入控制器并发布；初始共享规划通过 `CoreControllerPortPlanning` 保留候选/尝试上限。未确认的服务结果仍禁止本地回退。Release 构建 0 警告 / 0 错误；启动/所有权 Core 101/101、隔离 Integration 36/36、真实官方 Mihomo 19/19。证据：`artifacts/structure-optimization-20261010/local-start-transaction`。

### S3 退出清理和资源保留

`ShutdownCleanupCohort` 明确持有未结束的清理任务，统一等待期限/执行取消的分类和迟到故障观察。资源所有权在所有任务结束后才释放；Runtime 保留清理阶段计划与持久恢复结果。既有超时、取消、未知服务结果、清理优先及退出顺序故障回归通过。Release 构建 0 警告 / 0 错误；退出相关 Core 44/44、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/shutdown-cohort`。

### S5 可直接引用的展示逻辑

新增小型 `ClashTray.Presentation`，把托盘回调、窗口位置、退出协调、端点表单、启动反馈、页面命令及原生声明从 App 移入该程序集，保持命名空间与行为。App 和 Core.Tests 直接引用同一份生产程序集；移除测试项目的七个 App 源文件链接。Release 构建 0 警告 / 0 错误；相关 Core 40/40、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/presentation-assembly`。WinUI 控件组装仍由后续 Debug smoke 验证。

### S4 设置页状态与命令

提取 `SettingsDraftController`（不可变提交检查点）、`SettingsEndpointEditor`（编辑/测试状态与端点命令）和 `SettingsNetworkPresentation`（确认状态与能力生成展示值）。页面只捕获控件文本、提供 WinUI 对话框及渲染；原有保存中继续编辑、外部字段合并与 NumberBox 未提交文本语义保留。隐藏网络规则沿用隐藏入口，改用共享页面代际/准入，避免离开页面后显示旧完成结果。新增 3 个草稿/确认状态用例，资源检查扩展到 Presentation。Release 构建 0 警告 / 0 错误；相关 Core 46/46、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/settings-presentation-final`。

### S4 窗口展示格式

`PanelDisplayFormatting` 集中核心/会话/错误的资源键映射及流量/内存格式，MainWindow 保留本地化、错误脱敏和 WinUI 渲染。单位、精度与回退键保持原行为。Release 构建 0 警告 / 0 错误；相关 Core 15/15、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/window-formatting`。

### S5 显式运行时测试组装

现有测试的旧 Runtime 构造迁移至测试侧 `RuntimeTestBuilder`：路径必传、默认服务/代理/启动项均隔离、伪控制器所有权在调用处明确注入。移除依赖 paths 是否为 null 的 Compatible 组装；公开 Runtime 构造统一为 Production，隔离工厂显式组装。`FixtureCoreOwnershipObservationSource` 移到 tests/Shared；生产内部故障 hook 保留最小回归入口，但必须接收观察源，不再自行安装假 Owned 证据。Release 构建 0 警告 / 0 错误；全量 Core 771/771、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/explicit-runtime-fixtures`。真实进程用例仍使用 Windows 观察源。

### S6 共用只读资源采集

`ControllerResourceSampler` 提供只读结果、诊断和可选资源失败策略，供本地和远程管线复用。资源需求、健康/会话代际、提交和 reference 稳定仍由各协调器管理；本地在失败时取最新已确认值，远程保留本次读取前的确认快照，原取消差异明确表达。Provider 直接解析两个响应，移除 JSON 拼接/重解析。新增 3 个取消/最新回退/脱敏用例。Release 构建 0 警告 / 0 错误；相关 Core 48/48、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/controller-resource-sampler-final`。不据此宣称量化性能收益。

### S6 快照纯读取与日志发布

移除 Snapshot getter 的日志 drain，在两个明确发布入口的同步边界内提交待发布日志。应用错误仍立即进入状态，Mihomo 日志继续有界批量/节流发布。新增回归在真实发布 gate 上阻止异步发布，证明重复读取 RuntimeSnapshot/AppSnapshot 不改变 revision 或日志引用，下一次发布包含待处理行。Release 构建 0 警告 / 0 错误；相关 Core 32/32、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/pure-snapshot-reads`。

### S6 本机与控制器展示上下文

`PanelSnapshotContext` 一次持有本机事实、当前控制器和该次页面兼容投影，窗口的端点/代际/能力不再分别保存八个字段。旧 RuntimeSnapshot 适配器限制在现有页面 API 边界。托盘直接消费 LocalDeviceSnapshot，移除为托盘构造完整投影；选中远程 Controller 的失败/运行状态不改变本机图标或代理/TUN 含义。新增跨上下文回归。Release 构建 0 警告 / 0 错误；相关 Core 24/24、隔离 Integration 36/36。证据：`artifacts/structure-optimization-20261010/typed-panel-context`。

### S7 模块目录与架构约束

Core 根目录源码全部按职责归档；App 分 Shell/Pages，Service 分 Hosting/Dispatch/Updates/Tun，Contracts 与 Presentation 按稳定类型分组。保持命名空间和源码行为；路径分析器、Debug 排除、源码检查同步更新。WinUI 发布资源递归补拷贝并保持 XBF 相对目录。新增 4 个项目/程序集/服务用例边界约束；[模块与服务共享类型清单](module-map.md) 说明保留 friend 访问的原因。审查报告证据链接固定到原始基线提交，避免文件移动后指向错误位置。

Release / Debug 全 solution 0 警告 / 0 错误；全量 Core 779/779，隔离 Integration 37/37。隔离 Debug WinUI smoke 退出 0，页面资源、窗口显示/隐藏/最小化恢复、设置草稿与保存、节点/连接/日志交互、远程能力/旧结果、隐藏 SSID 入口等自动检查通过；人工查看 Settings/Proxy 的捕获图未发现本次布局异常。使用合成数据且没有初始化核心、正式服务或托盘注册，不修改真实启动项/网络。证据：`artifacts/structure-optimization-20261010/module-directories`、`module-debug-build.log`、`module-ui-smoke`；多显示器/真实托盘/DPI/高对比度仍需机器验收。

### S8 锁定依赖与发布门禁

八个项目生成 NuGet 锁文件；统一包含 Windows x64 还原图，使普通构建和自包含 App/Service publish 共用解析结果。CI/Release 使用 locked restore，测试不再次隐式 restore，EXE publish 强制 RestoreLockedMode。中央版本未升级。Release 补充与 CI 同样的 Windows 安全能力无跳过门禁。

默认平台 locked restore、Release 全 solution 构建、App/Service 的 win-x64 self-contained locked publish 均通过；构建 0 警告 / 0 错误，相关 Core 17/17、隔离 Integration 37/37。证据：`artifacts/structure-optimization-20261010/dependency-locks`、`locked-publish-app.log`、`locked-publish-service.log`。最终 alpha 再执行安全门禁和两种打包变体。

官方门禁参数核对移除一处重复的 `--no-restore`（原脚本已有该参数）。锁定构建后的真实官方 Mihomo 19/19、安全能力 6/6 均无跳过；证据：`locked-official/official-mihomo-results/official-mihomo.trx`、`locked-security/security.trx`。

### S8 当前文档入口

README、setup/release、架构、设置/测试说明与新 docs 索引同步到本轮职责和发布范围。文档区分结构重构、隔离证据、强制能力门禁及未验收机器场景；历史报告原样保留。构建示例使用 global.json / locked restore，发布示例指向即将准备的 0.3.6-alpha.1。原生代码和功能未由此新增。

## 最终 alpha 验证

版本元数据由 `Directory.Build.props` 统一提供：`VersionPrefix=0.3.6`、`VersionSuffix=alpha.1`；`Get-ProductVersion.ps1` 读取为 `0.3.6-alpha.1`。本地验证的生产源码提交为 `246fc33`，后续实施报告提交仅补充文档；GitHub 发布重新构建 tag 对应源码。

| 验证 | 实际结果 | 证据（相对 `artifacts/structure-optimization-20261010/final-alpha`） |
| --- | --- | --- |
| 全 solution locked restore | 通过，8 个锁文件未发生漂移 | `restore.log` |
| 全 solution Release / Debug x64 | 均为 0 警告、0 错误 | `release-build.log`、`debug-build.log` |
| Core 全量测试 | 779/779，通过，无跳过 | `core/core.trx` |
| Integration 全量测试 | 62/62，通过，无跳过；包含下列官方/安全分类 | `integration/integration.trx` |
| 官方 Mihomo 互操作门禁 | 19/19，通过，无跳过；v1.19.31 官方归档 SHA-256 校验通过 | `official/official-mihomo-results/official-mihomo.trx` |
| Windows 安全能力门禁 | 6/6，通过，无跳过；临时对象的 ACL、restricted token、TLS | `security/security.trx` |
| Core 覆盖率门禁 | 行 80.50% / 分支 74.02%；原门槛 70% / 65% 保持不变 | `core/f502ca3b-c181-4835-9a81-a60df481cf09/coverage.cobertura.xml` |
| 隔离 Debug WinUI smoke | 退出 0，无 failure.log；19 张 PNG、10 份 JSON 检查记录 | `ui-smoke` |

WinUI smoke 使用真实生成的 WinUI 组件和合成状态。设置草稿/NumberBox 未提交文本、保存重载、页面代际、节点滚动/延迟状态、连接排序/详情/选择保留、日志过滤/有界淘汰、远程旧结果和能力限制、隐藏 SSID 入口、窗口显示/隐藏/最小化恢复均通过内置检查。人工复核 `page-Settings.png` 与 `nodes-dark.png`；此次没有观察到路径调整或逻辑提取造成的控件装载/布局异常。记录确认 `WindowsStartupRegistryChanged=false`、`NetworkStateChanged=false`、`TunTouched=false`。这不包含物理输入与整机网络验收。

源码集中点的同口径变化如下（包含空行/注释，Runtime 排除 TestHooks；仅用于说明归属移动，不作为质量或性能指标）：

| 对象 | 原基线 | 本轮完成后 |
| --- | ---: | ---: |
| Runtime 的生产 partial 文件合计 | 5,141 行 | 4,443 行 |
| ServiceRuntimeController | 1,704 行 | 1,290 行 |
| SettingsPage.xaml.cs | 1,326 行 | 1,278 行 |
| MainWindow.xaml.cs | 1,047 行 | 983 行 |

Runtime 仍负责服务接管、轮询和跨事实源的用例编排；SettingsPage 仍保留动态 WinUI 行、对话框和控件事件。生产内部故障 hook 与 Service 的受限 friend 访问仍存在，当前约束和原因见 [模块与依赖边界](module-map.md)。后续应随具体维护需求继续收敛，而不是仅为行数增加接口、容器或框架。本轮未声称完全消除共享状态，也未声称量化性能提升。

未执行的机器验收：真实安装的服务身份与生命周期、实际 System Proxy/TUN 切换与失败恢复、安装/升级/卸载、Windows 登录启动、睡眠恢复、网络适配器变化、Explorer 重启、多显示器/任务栏位置、物理鼠标/触摸/键盘、高 DPI、高对比度、辅助技术及 Windows 10。发布说明明确为 alpha，保留这些限制。

## 安装包检查与交付

本地 Inno Setup 7.1.0 成功编译 Full 和 NoCET，App/Service 均以 win-x64 self-contained 和 RestoreLockedMode 发布。按脚本验证固定的官方 Mihomo / MetaCubeXD 归档校验、x64 可执行文件、许可证与来源元数据；安装包 SHA-256 sidecar 与实文件一致。迁移后的 `App.xbf`、`ClashTray.App.pri`、`Shell/MainWindow.xbf` 及五个 `Pages/*.xbf` 均已进入两种载荷。

打包前检查 `D:\ClashTray\packaging\.stage` 的实际绝对路径、顶层生成产物清单及无 reparse point；清理只作用于该生成暂存目录。没有执行两个安装器，也没有安装工具或更改机器配置。

| 本地验证产物 | 大小 | SHA-256 |
| --- | ---: | --- |
| Full | 83,645,649 bytes（79.77 MiB） | `06aaa277d62aba7d2e6f1eedb66265ee5951f9d2a65205495fd8c6b1ddbf838f` |
| NoCET | 83,663,318 bytes（79.79 MiB） | `60bee26e8967afe549a902982efc347286ceba4fd10603904ad0c96101d54eef` |

证据：`final-alpha/package-Full.log`、`package-NoCET.log`、`packages/{Full,NoCET}/verification.json`。上表为本地验证包；GitHub Actions 会从最终 tag 重新构建，公开安装器以发布页附带的 SHA-256 为准。

交付版本为 `v0.3.6-alpha.3`，通过仓库 Release workflow 发布为 prerelease；分支仍为 master。用户原有未跟踪报告与发布清理文件未提交或改动。本轮每个独立结构改动单独提交，工具链、文档、版本与验证记录也分别提交；完整历史见 [本轮提交比较](https://github.com/arukas/ClashTray/compare/f015e164190bf6eb682c2aa3952204deb1f66b49...v0.3.6-alpha.3)。

- [Alpha 发布页](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.3)
- [CI 与 Release 执行记录](https://github.com/arukas/ClashTray/actions)
- [版本发布说明](releases/0.3.6.md)

最终推送、CI 和发布产物状态需以对应 GitHub 执行结果核验，本地构建不替代远端发布完成确认。

### 首轮远端门禁与诊断

`766ab07` 已推送 master 和 `v0.3.6-alpha.1`。对应 [CI 38064177421](https://github.com/arukas/ClashTray/actions/runs/38064177421) 完整通过；[Release 38064180585](https://github.com/arukas/ClashTray/actions/runs/38064180585) 构建通过，在官方 Mihomo 互操作门禁失败，尚未生成发布资产。公开 API 注释只有退出码，匿名浏览器和公开日志 API 无法取得具体测试详情；不把它未经证实地归为环境问题。

新增门禁失败注释：保留 dotnet 的退出码，读取该次 TRX 的非 Passed 测试，输出有长度限制且经过 workflow command 转义的名称/失败信息，再保持原有失败判定。测试数量、无跳过要求与覆盖率门槛未降低；本轮不自动把测试失败当作通过。后续使用新的 alpha 序号触发正常发布，不重写已有 tag。

诊断脚本语法检查通过，实际官方门禁再次 19/19 无跳过。对生产失败报告分支注入包含 `%`、CR/LF 和 workflow command 文本的失败 XML，确认仍拒绝发布，且输出为一条已转义的 error annotation。证据：`release-diagnostics/official-mihomo-results/official-mihomo.trx`、`release-diagnostics/failure-reporting-check.json`。

最终交付版本后移至 `0.3.6-alpha.2`，只补充发布诊断与版本/当前文档；生产结构和网络行为沿用上述全量验证的源码。上表保留 alpha.1 本地验证包的实际数字，alpha.2 安装包和校验值由对应 tag 的 GitHub Release 流程重新生成。`alpha.1` tag 保留作为首轮失败记录，未覆盖或删除。

### 第二轮远端测试入口

`96671e6` / `v0.3.6-alpha.2` 本地 locked restore、完整 Release 构建（0 警告/错误）、Core 779/779、Integration 62/62、官方 19/19、安全 6/6 全部通过，证据见 `artifacts/structure-optimization-20261010/final-alpha2`。[CI 38065151943](https://github.com/arukas/ClashTray/actions/runs/38065151943) 同样完整通过。[Release 38065216699](https://github.com/arukas/ClashTray/actions/runs/38065216699) 已通过构建、官方和安全门禁，在 solution 测试入口失败，未生成发布资产；公开 API 没有具体测试信息，根因尚未确认。

新增 `Test-ReleaseSuite.ps1`，按 CI 的 Integration → Core 顺序逐个运行完整测试项目，避免两个项目的进程/端口夹具同时执行；保存每个项目 TRX 并发布经过转义的非 Passed 摘要。退出码、零测试、缺少 TRX、失败和跳过均拒绝发布。Release 始终上传这些结果供诊断。此调整统一测试执行方式和可观测性，没有删除测试或降低门槛；不把尚未确认的失败归因为并行执行。

新入口在本地实际运行 Integration 62/62 → Core 779/779，全部无跳过。另用临时合成 TRX 和该脚本的实际执行入口注入 MissingTrx、EmptyTrx、Skipped、Failed、Nonzero 五种故障，均拒绝后续项目/发布；Passed 场景依次执行两个项目，失败消息 CR/LF/% 转义通过。证据：`release-suite-check/{integration,core}.trx`、`release-suite-faults-v2/checks.json`；合成故障记录不计入实际测试通过数。

交付版本继续递增为 `0.3.6-alpha.3`，当前文档入口和版本化发布说明同步更新。两个失败 tag 均保留，未生成 prerelease 资产；本轮 C# 生产代码自全量验证后没有再改动，后续改动只涉及发布诊断、执行顺序、版本和文档。最终 alpha.3 的 CI / Release 及资产以公开 GitHub 结果核验。

### 最终交付及发布后代码复审

`caf2f7727a6e28cc887694d76cc85d1ee2c63e4a` / `v0.3.6-alpha.3` 已发布为 [GitHub prerelease](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.3)。[CI 38065956218](https://github.com/arukas/ClashTray/actions/runs/38065956218)与 [Release 38065960608](https://github.com/arukas/ClashTray/actions/runs/38065960608)成功，7 项公开资产和发布来源清单已核验。公开 Full 与 NoCET 安装器的 SHA-256 分别为 `57abafbe7b6dd042359909859697eebdd5ea085cfe84a5e9c5173e213b016e9b` 与 `32d71e0233f09c559918fc0266a2b587612c94439b0985bf713692c376a2a3fa`；上文 alpha.1 本地包的数字作为历史保留。

2026-10-11 补做发布差异自审和调用链复审；针对性复跑 Core 173/173 和 Service 13/13 全部通过、无跳过。131 个基线到发布提交的纯搬迁 blob 一致，拆分的契约和操作原语一致性检查、XAML 输出及发布故障注入检查通过。继续核对全部状态文案时，确认并修复 1 项 P2 现有缺陷：核心验证配置时误显示“已停止”；补充“验证中”/“Validating”的状态映射和资源。修复后完整 Release/Debug 构建均为 0 警告/错误，Core 779/779、隔离 Integration 37/37 无跳过；隔离 WinUI smoke 退出 0，截图和 JSON 确认“验证中”及禁用的启动按钮。范围、详细证据及未完成的整机/日志诊断见[发布后代码复审](final-code-review-2026-10-11.md)。这次是代码作者重新审阅，未安排第二位独立审阅者，也没有把实施阶段的测试通过等同于代码审核。

### 自审修复后的 alpha.4

修复提交 `0ce364c` 与版本提交 `a166d8e` 分别推送，后者的 `v0.3.6-alpha.4` 已[发布](https://github.com/arukas/ClashTray/releases/tag/v0.3.6-alpha.4)。[CI 38100201079](https://github.com/arukas/ClashTray/actions/runs/38100201079)与 [Release 38100203430](https://github.com/arukas/ClashTray/actions/runs/38100203430)成功；完整日志确认 Core 779/779、Integration 62/62、官方核心 19/19、安全能力 6/6 无跳过，CI 行/分支覆盖率为 80.47%/73.93%。公开的 7 项资产和源码/第三方来源清单已核验；Full 和 NoCET 的 SHA-256 为 `35be178ce4a8169ab2d3273cfd47028bc0e39dcdd29252069ad75daabb4c2e07`、`5944a4bb6b92fc1114053b32e7fc3669c6ac9423492582ceb8f11f2ff6f98424`。

用户更新 `.env` 后认证读取 Actions 成功。历史日志与 alpha.1 TRX 已确认：alpha.1 官方门禁未到达预期 UDP 注入观测，alpha.2 的 5 个 Core 用例在隔离端口夹具分配阶段失败；未记录的响应/端口细节不能补推成已确认根因。具体边界、证据和最终发布核验见上述复审报告。诊断产物与密钥均未提交。

# ClashTray 六项代码设计优化实施记录

日期：2026-10-02（Asia/Singapore）。实际基线 HEAD：`0029bf1f11b2b3347f1e7dc0fa656204e4cb12fe`。

本轮由用户明确授权实施 A1—A6，顺序为基线 → A1 → A6 → A2 → A3 → A5 → A4 → 最终回归。代码完成、隔离自动化验证和真实 Windows 系统验收分别记录。未执行的场景不算通过。

## 基线和执行边界

- 已读取根 AGENTS.md、优化指南及配套提示词、development-policy.md、architecture.md、CI 与门禁脚本。src/tests/docs/packaging/.github 没有更深层 AGENTS.md。
- 保留开工前已修改的 AGENTS.md、development-policy.md 和全部未跟踪文件（包括指南、历史审查和发布辅助文件）；不提交、推送、tag、发布、改版本、安装正式服务或改变用户真实 System Proxy/TUN。
- 开发环境：Windows x64 10.0.26300；.NET SDK 10.0.400；Visual Studio Community 2026 18.9.12120.119；Windows SDK 10.0.26100.0。实际模型由会话配置决定；用户要求 gpt-6.1-sol / max，当前工具没有独立读取或切换模型配置的能力，不声称已切换。
- 受限令牌下 `dotnet restore ClashTray.sln` 报 NU1301 / Windows TLS“安全包中没有可用的凭证”。正常令牌的同一 restore 成功，无依赖版本变化。后续记录两种环境的区别。
- 基线 `dotnet build ClashTray.sln -c Release -p:Platform=x64 --no-restore`：通过，0 警告 / 0 错误。受限令牌的 Core：670 项，662 通过 / 8 失败 / 0 跳过；非官方 Integration：29 项，25 通过 / 4 失败 / 0 跳过。TRX 位于 `artifacts/design-optimization-20261002-01a0fb37/baseline`。八项 Core 均 finally 清理 `program/logs` 拒绝访问；集成为受限令牌创建、两项 TLS 成功路径、日志 ACL 清理。清理异常不能证明主体通过。

## A1 设置回滚失败与一致性

状态：已实现并通过隔离回归与后续设置页 WinUI smoke；真实启动项与网络副作用恢复待验收。`SettingsRecoveryTests` 先在原代码执行：2 项，1 通过 / 1 确定性失败（原操作异常未保留回滚失败）。之后全解决方案构建通过，相关回归 46/46，0 跳过。

设计：设置副作用开始前原子写单条 64 KiB、schema 1 的 `settings-recovery.json`，只含无凭据的 AppSettings 前后值；正常保存先落盘再更新内存，成功后删除记录。回滚使用独立 3 秒存储预算，核心/网络恢复保留独立 30 秒预算；用户取消不取消安全恢复。失败保留安全内存偏好、原始与恢复异常（AggregateException）、脱敏日志与持久错误提示。普通设置即使回写失败仍尝试恢复核心网络。启动及下次相关设置操作按持久化前后值核对恢复，外部更改则拒绝覆盖并保留记录；未恢复前阻止新的相关写入。TUN 保持先确认服务结果再保存偏好的顺序，已确认结果不因磁盘错误变成不可用。仅开始偏好写入后才执行回滚。

验证：`dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName~SettingsRecoveryTests|FullyQualifiedName~RuntimeSettingsTests|FullyQualifiedName~RuntimeSystemProxy|FullyQualifiedName~RuntimeService|FullyQualifiedName~StartupReconciliation|FullyQualifiedName~AppSettingsPatch'`，TRX `a1-complete/a1.trx`。覆盖成功、初次写失败、双失败、下次操作/重启恢复、外部冲突、有界记录和 TUN 确认状态；既有已派发取消与代理所有权行为保留在相关回归中。

## A6 测试权限、fixture 与诊断

状态：已实现，相关 Core 48/48、必需 Windows 能力门禁 6/6，均 0 跳过。普通更新逻辑通过 `ICoreUpdatePermissionPolicy` 与 Windows ACL 策略分离；生产默认实现的权限策略不变，并新增真实 ACL 用例验证管理用户仅 ReadAndExecute + Windows 自动 Synchronize 权限。测试初次写入顺序和 Synchronize 断言错误均修正，未放宽生产 ACL。

`tests/Shared/TestFixtureDirectory.cs` 统一本轮 fixture 生命周期：本进程注册的 Temp/ClashTrayTests/GUID 根、绝对路径和所有祖先/子项 reparse 检查、当前创建者所有权确认；只向测试创建者恢复删除所需权限。保留分享冲突短暂重试，主体和清理同时失败时保留双异常及原堆栈。CoreUpdater 的 12 个文件 fixture、服务日志与官方更新用例接入；历史未知目录保持不变。受限令牌下 48 项包含更新、清理、A1 和启动端口回归均通过。

原令牌/TLS 环境问题在正常 Windows 令牌下实际通过。TLS 负向用例先证明正确 CA 握手成功，不能把 SSPI 缺失误算为拒绝错误 CA。新增 RequiresWindowsAcl / RequiresRestrictedToken / RequiresTls 分类、环境诊断与 `Test-SecurityCapabilities.ps1`，CI 必需门禁按声明数量核对每项通过，跳过或环境不可用均失败。命令：`./packaging/Test-SecurityCapabilities.ps1 -Configuration Release -ResultsDirectory artifacts/design-optimization-20261002-01a0fb37/a6/security-verified`，6/6。

## A2 服务请求身份与结果恢复

状态：已实现，相关 Core 46/46、Integration 16/16，均 0 跳过；真实正式服务升级与断线待验收。原客户端在确定性“服务执行成功但传输丢响应”回归中失败；同一操作现保持 RequestId/命令/载荷，最多一次 RecoveryOnly 重连，共享原始 3 秒 / 15 秒 / 30 秒 / 6 分钟预算。取消等待不撤销服务副作用。初次未派发连接失败仍保留 NotDispatched；已派发取消、取回失败保持 UnknownOutcome，Runtime 原有确认状态核对与安全清理保留。

协议改为 3（产品版本不变）；新旧双方明确拒绝版本不符，不能降级丢失恢复保证。RecoveryOnly 只加入已有缓存，不执行缺失条目；128 项缓存、不淘汰未过期结果、完成后保留 2 分钟；饱和拒绝新操作。服务实例标识和可选预期实例校验明确实例更换，指纹冲突拒绝。请求/响应分别限 64 Ki / 256 Ki 字符，响应写入仍限 5 秒。官方来源、受限路径、ACL、运行绑定、TUN 归属和服务独立校验保持。

测试涵盖全部生命周期/TUN/安装/回滚的丢响应策略、并发加入、可控执行中的等待取消、过期、实例更换、饱和、指纹冲突、响应 ID 不符、旧协议和有界读。命令为完整方案 Release x64 build 后，Core 过滤 ServicePipeClient/LocalDeviceCoordinator/RuntimeService/RuntimeSystemProxy/SettingsRecovery；Integration 过滤 ServiceRequestRecovery/Boundary/TunLifecycle，TRX 位于 `a2-complete`。

## A3 生命周期、健康与状态归属

状态：已实现并通过隔离回归；正式服务、睡眠恢复和真实 OS 网络仍待验收。Release x64 全方案构建 0 警告 / 0 错误；相关 Core 269/269、Integration 7/7，0 跳过，TRX 位于 a3-complete/core 和 a3/integration。

CoreLifecycleCoordinator 持有本地进程、服务/本地执行器、运行绑定、生命周期代际和健康印记；Runtime 保留门面、操作准入、设置恢复和 UI 状态投影。ICoreLifecycleExecutor 只复用启动/停止执行边界，服务仍独立验证允许的载荷与路径，不接受桌面提交的所有权事实。生产与隔离组装使用 RuntimeEnvironmentDependencies/ClashTrayRuntimeFactory；保留旧构造入口的兼容解析，新的核心构造不推测 nullable 环境。

Windows 身份/监听观察通过 ICoreOwnershipObservationSource 注入。移除“测试控制器已注入则跳过健康判定”的布尔分支，替身提供观察证据，真实归属判断仍执行。新增 Owned/Unknown/Foreign 观察和绑定/进程/控制器代际印记测试。最初 12 项回归失败分别为两处旧协议常量和十处旧测试绑定注入语义，修正协议预期及测试观察 hook；生产 StoreBinding 始终使旧印记失效，操作 gate/lease、写优先、退出静默期及旧响应不提交的条件保留。

## A5 设置编辑与页面命令

状态：已实现并通过模型/命令与真实 WinUI smoke；人工键盘、屏幕阅读器和多 DPI 仍待验收。Release x64 全方案构建通过，相关 Core 62/62，0 跳过，TRX a5/core/a5-core.trx。

SettingsEditModel 持有已加载基线、纯设置草稿及分开的数字 Value/原始文本/修改状态；只向已修改字段生成 patch，非设置入口管理的配置、System Proxy、TUN 和主题解锁字段不进入表单 patch。同字段编辑保留，未编辑字段采用外部最新值；成功保存只确认提交时的草稿，保存过程中新增编辑仍保留，失败不清除草稿。输入限制 64 字符、有限整数及现有端口/刷新范围，并沿用 SettingsValidator 的交叉校验。

SettingsPageCommands/PageCommandRunner 提供可等待的独立设置、网络、维护、端点操作槽，重复操作不派发，异常脱敏、页面代际限制反馈，离开页面不撤销已准入副作用。页面保留控件投影、对话框及事件入口；维护目标和端点参数在派发前捕获。NumberBox 保留无效文本，快照/保存读取实际模板输入，不强制移焦；网络开关仍立即回到确认状态，等待确认结果后投影。没有新增用户文本或 UI 框架。

## A4 YAML 共享结构与有界处理

状态：已实现并通过相关 Core 141/141、官方 Mihomo 门禁 16/16，均 0 跳过。固定版本 v1.19.31，官方归档 SHA-256 38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67 实际校验一致。命令：Release x64 全方案 build；Core 过滤 Yaml/RuntimeConfigBuilder/MihomoListenerPlanAnalyzer/Configuration/ListenerReadiness；Test-OfficialMihomo.ps1 -Configuration Release -ExistingArchivePath ...，TRX a4-complete/core 和 a4/official/official-mihomo-results。

优化前支持/拒绝/保留但未知范围写入 yaml-structure.md。YamlStructure/YamlStructureDocument 共享键解码、文档边界、根节点/section 与严格替换范围、block scalar、多行引号、flow 深度和引用策略；生成器与分析器消费同一结构证据，监听 indentationless 序列仍保持分析器支持，生成器的严格 block-root 策略不放宽。无法确认的根/文档作用不再当不存在；生产有效代理监听的未知结果继续拒绝启动。非受管行原文保留，受管值仍唯一有效覆盖；TUN 起核为 off、127.0.0.1 与空 secret 保持。

BoundedYamlReader 按实际流字节计数，保留 BOM 和换行行为，限制 16 MiB/250,000 行/65,536 字符，取消贯穿读取扫描；输入读中增长被确定性拒绝。生成、控制器候选与监听文件读取复用，候选验证通过生成器获得同样边界。保留临时原子移动与 ACL、路径限制、旧文件/元数据回滚；无依赖/许可证/打包变化。初次相关回归三处失败为资源限制错误文案断言，恢复旧文案后全部通过。新增五种等价 dns 键语料、负向文档范围、anchor/未知监听，并把五种生成文件实际交给固定官方核心 -t 验证。

## 最终回归与待验收

最终依据为 final-release 目录，全部在最后生产/fixture 修正后的实际工作区运行：Release x64 全解决方案构建通过（0 警告 / 0 错误）；完整 Core 725/725、完整 Integration 50/50；官方 Mihomo 强制门禁 16/16、Windows 安全能力强制门禁 6/6，所有测试 0 失败 / 0 跳过。Core 行覆盖率 79.28%、分支 72.77%，超过 70% / 65%，未修改门槛。后两项是 Integration 内既有类别的独立强制重跑，不额外累加唯一测试数。

Debug 全方案构建也通过，最后 A5 模型/命令 18/18；最新真实 WinUI smoke 为 a5/ui-smoke-final，退出 0。实际验证包括窗口隐藏/显示与最小化恢复、设置草稿/外部更新、原始数字文本/无效文本/焦点、落盘与重建页，以及已有远程过期状态和连接/日志交互 smoke。tray native click、人工键盘遍历、屏幕阅读器、多个 DPI/显示器/任务栏位置、高对比度未验收。

完整 Integration 在普通 Windows 令牌下运行；Core 在受限执行令牌下运行。环境及身份不同，不把 SSPI/令牌能力缺失误算成功。官方核心由经过固定 SHA-256 校验的独立归档提取，明确设置 REQUIRED=true，未使用用户运行核心。真实正式服务升级/安装/卸载、真实 System Proxy 所有权恢复/TUN 路由与 DNS、睡眠/网络适配器变化/Explorer 重启、Windows 10 22H2 兼容仍未系统验收；六项代码与隔离自动化完成不等于这些场景或正式发布完成。

兼容：IPC 协议 3 需要 App/Service 同步升级，拒绝旧/未声明版本；设置 schema 及产品版本不变，新增恢复记录 schema 1。没有修改依赖版本、分发方式、P2 范围、127.0.0.1 与空 secret 约束，也未新增启动核心哈希检查。无提交、推送、tag、发布、正式服务安装或真实代理/TUN 操作。用户预存修改与全部无关未跟踪文件保留。

## 最终回归期间补充修复

首次完整 Core（722 项）721 通过 / 1 失败 / 0 跳过：清理枚举到随后被原子写入移走的 CONFIGURATION-SWITCH-JOURNAL.JSON.tmp，GetAttributes 抛 FileNotFoundException。补入确定性竞态回归先失败；仅将本次注册 fixture 中已经消失的项视为已清理，权限/归属/reparse/其他 I/O 不吞掉。另补“初始写、旧偏好写回、启动项回滚”三故障回归，先实际失败（只保留两个异常），修正为保留三个。两个红用例 0/2，修复后相关 34/34、0 跳过（final-followup-red/green）。

新版 Debug WinUI 构建通过；a5/ui-smoke-verified 真实 WinUI smoke 退出 0，settings-draft-checks.json 确认未保存开关/端口/文本、外部主题、可见未提交 NumberBox 文本、无效文本拒绝且保留、实际焦点、失败校验、成功落盘、重新建页。只使用隔离 startup/proxy/service 适配器和目录。a5/ui-smoke 为旧产物的诊断执行，不用来证明新增原始输入检查；ui-smoke-verified 保留为当次有效证据，后续最新 A5 证据为 ui-smoke-final。人工键盘遍历/屏幕阅读器、多个 DPI/高对比度及真实登录启动仍未验收。

完整 Core 最终修复前覆盖率 79.12% / 72.61% 已高于 70% / 65%，但该次有失败不作为最终通过；保留 final-verified 等中间目录的失败与覆盖率记录，最终通过证据统一使用 final-release 目录。
## 最后 UI 反馈与 fixture 生命周期验证

A5 新增“命令已完成但 UI 续体排在导航之后”确定性回归先失败（0/1）；完成反馈改为在实际呈现时重新读取页面代际，不保存提前计算的 CanPresent 布尔值。之后模型/命令 18/18，最新 smoke 退出 0；路径 a5/feedback-red、feedback-green、ui-smoke-final。

final-accepted 完整 Core 725 项曾为 724 通过 / 1 失败：RuntimeSubscriptionSwitchTests 在外层 await using 释放前，finally 先删目录，撞上候选文件写入。为原测试注入 Runtime 拥有并在退出释放的独占文件句柄，稳定复现（0/1），然后统一该测试文件的注册 fixture、先释放 Runtime 再清理、主体/清理双异常记录；相关 16/16，通过且无跳过（a6/lifetime-red-confirmed、lifetime-green）。共享 fixture 现在在 TRX 标准输出记录本次根路径，方便精确诊断，不记录凭据。

清理本轮失败 TRX 明确识别的测试目录：先后删除 11 个已存在 GUID fixture；13 个记录根最终 0 剩余。额外仅以非递归删除移除一个在该失败测试精确时间段内创建、属于测试身份且只含空目录的旧 fixture（三个空目录、0 文件）。历史未知目录不扫描删除，不改不同所有者 ACL。普通/受限身份不同的第一次清理受 owner 检查拒绝；PowerShell Set-Acl 请求额外 SeSecurityPrivilege 的尝试失败后，采用与测试 helper 相同的 CLR DACL-only API 在创建者身份下完成，未请求审计权限或更改所有者。诊断见 known-fixture-cleanup.json 与 empty-legacy-fixture-cleanup.json，均在本轮 artifacts 根。

git diff --check 依仓库 EOL 配置通过。人为临时禁用 autocrlf 的那次检查把 CRLF 全部误判为尾随空白，丢弃该诊断且未修改行尾或 Git 配置。
## 关键文件与剩余验收映射

| 项目 | 主要文件（仓库相对路径） | 验证与剩余边界 |
| --- | --- | --- |
| A1 | src/ClashTray.Core/SettingsRecoveryJournal.cs、ClashTrayRuntime.Settings.cs、ClashTrayRuntime.Network.cs；SettingsRecoveryTests.cs | 双/三故障、重启/下次操作、外部冲突、有界记录通过；真实代理与启动项恢复待系统验收 |
| A2 | src/ClashTray.Contracts/StateContracts.cs；Core/ServicePipeClient.cs、ServiceMessageReader.cs；Service/ServiceRuntimeController.cs、ServiceCommandHost.cs | 并发/丢响应/过期/实例/饱和/协议边界通过；正式服务断管升级待验收 |
| A3 | src/ClashTray.Core/CoreLifecycleCoordinator.cs、RuntimeEnvironmentDependencies.cs、ClashTrayRuntime.Lifecycle.cs、TestHooks.cs；App/App.xaml.cs | 真正健康谓词消费观察证据、代际与现有并发/退出回归通过；LocalSystem、睡眠恢复未验收 |
| A4 | src/ClashTray.Core/BoundedYamlReader.cs、YamlStructure.cs、YamlStructureDocument.cs、RuntimeConfigBuilder.cs、MihomoListenerPlanAnalyzer.cs | 原语料/资源上限、共享语料与固定官方核心验证通过；完整通用 YAML 解析不在目标内 |
| A5 | src/ClashTray.Core/SettingsEditModel.cs；App/SettingsPageCommands.cs、SettingsPage.xaml.cs、SettingsPage.xaml、MainWindow.SmokeTest.cs | 模型、命令、延后呈现代际与真实 WinUI smoke通过；人工键盘/屏幕阅读器/多 DPI 未验收 |
| A6 | src/ClashTray.Core/CoreUpdatePermissionPolicy.cs；tests/Shared/TestFixtureDirectory.cs；CoreUpdaterTests、RuntimeSubscriptionSwitchTests、ServiceFileLoggerTests、RemoteEndpointTransportTests、QueryLimitedProcessIdentityTests；packaging/Test-SecurityCapabilities.ps1、.github/workflows/ci.yml | 普通逻辑与真实权限边界分开；TLS/ACL/token/官方必需用例实际执行；CI 配置已同步但未触发远端 CI |

同步说明：architecture.md、service-protocol.md、settings-editing-and-recovery.md、yaml-structure.md、test-environment.md。待验收不会自动安装服务或改变用户代理/TUN；本次代码及隔离验证可审查，正式发布须补足上述系统场景。

## 最终复现命令与结果位置

本次最终结果根为 D:\ClashTray\artifacts\design-optimization-20261002-01a0fb37\final-release。以下测试和门禁均已经执行，命令预算/覆盖率门槛没有降低；Core 收集的 XPlat report 为 core/5ed45a98-e1d7-4e37-ac4e-eabd0ad9e1d3/coverage.cobertura.xml。

    dotnet build ClashTray.sln -c Release -p:Platform=x64 --no-restore
    dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --collect:"XPlat Code Coverage" --logger "trx;LogFileName=core.trx" --results-directory artifacts/design-optimization-20261002-01a0fb37/final-release/core -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura

完整 Integration 执行进程环境：

    $env:CLASHTRAY_MIHOMO_REQUIRED = 'true'
    $env:CLASHTRAY_MIHOMO_PATH = 'D:\ClashTray\artifacts\design-optimization-20261002-01a0fb37\a4\official\clashtray-mihomo-test-v1.19.31-81fc52571f104bfbac66560e2eff7757\mihomo-windows-amd64.exe'
    $env:CLASHTRAY_MIHOMO_ARCHIVE_PATH = 'C:\Users\Zen\AppData\Local\Temp\mihomo-windows-amd64-v1.19.31.zip'
    $env:CLASHTRAY_MIHOMO_ARCHIVE_SHA256 = '38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67'
    dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj -c Release -p:Platform=x64 --no-build --logger "trx;LogFileName=integration.trx" --results-directory artifacts/design-optimization-20261002-01a0fb37/final-release/integration
    $env:RUNNER_TEMP = 'D:\ClashTray\artifacts\design-optimization-20261002-01a0fb37\final-release\official'
    .\packaging\Test-OfficialMihomo.ps1 -Configuration Release -ExistingCorePath $env:CLASHTRAY_MIHOMO_PATH -ExistingArchivePath $env:CLASHTRAY_MIHOMO_ARCHIVE_PATH
    .\packaging\Test-SecurityCapabilities.ps1 -Configuration Release -ResultsDirectory 'D:\ClashTray\artifacts\design-optimization-20261002-01a0fb37\final-release\security'
    .\packaging\Test-CoverageGate.ps1 -CoverageDirectory artifacts/design-optimization-20261002-01a0fb37/final-release/core

强制类别门禁在普通 Windows 执行身份下运行，以具备真正 TLS/令牌能力；没有 UAC 服务安装或生产数据写入。复现时结果目录需换成新的唯一目录。

分片命令均为同一 Release x64 全方案 build 后 dotnet test -c Release -p:Platform=x64 --no-build（最后 A5 反馈用例另以 Debug x64 运行）；下表列出 FullyQualifiedName filter 的匹配片段及通过数，每个片段使用 FullyQualifiedName~ 前缀并以竖线连接。A1 的完整命令已在分片段落给出；TRX 保留在前述对应分片目录。

| 分片 | Filter / 脚本 | 当次通过数 |
| --- | --- | --- |
| A1 | FullyQualifiedName~SettingsRecoveryTests 或 RuntimeSettingsTests / RuntimeSystemProxy / RuntimeService / StartupReconciliation / AppSettingsPatch（用竖线连接） | 46/46 |
| A6 最小逻辑 | CoreUpdater / TestDirectoryCleanup / SettingsRecovery / RuntimeOneTimeControllerPort | 48/48 |
| A2 Core | ServicePipeClient / LocalDeviceCoordinator / RuntimeService / RuntimeSystemProxy / SettingsRecovery | 46/46 |
| A2 Integration | ServiceRequestRecovery / Boundary / TunLifecycle | 16/16 |
| A3 Core | Runtime / CoreLifecycleCoordinator / ControllerSession / OperationAdmission / ShutdownCoordinator / ListenerReadiness | 269/269 |
| A3 Integration | TunLifecycle / ServiceRequestRecovery | 7/7 |
| A5 初次 | SettingsEditModel / PageCommandRunner / AppSettingsPatch / EndpointDraft / Localization / RuntimeSettings / RuntimeSystemProxy / RemoteEndpointControllerSession | 62/62 |
| A4 Core | Yaml / RuntimeConfigBuilder / MihomoListenerPlanAnalyzer / Configuration / ListenerReadiness | 141/141 |
| A1/A6 最后补充 | SettingsRecovery / TestDirectoryCleanup / RuntimeOneTimeControllerPort | 34/34 |
| A5 延后反馈 | PageCommandRunner / SettingsEditModel（最后 Debug x64） | 18/18 |
| A6 生命周期 | RuntimeSubscriptionSwitchTests / TestDirectoryCleanup | 16/16 |

最新 UI 命令：Debug 全方案 build 通过后，以 Hidden 方式启动 App 的 --ui-smoke-test=D:\ClashTray\artifacts\design-optimization-20261002-01a0fb37\a5\ui-smoke-final。该模式不初始化真实核心、不注册托盘、不使用真实 startup/proxy/service。进程退出 0，保留 settings-draft-checks.json、geometry.json、各交互诊断与实际 WinUI PNG。最后测试 fixture 的修正仅影响测试源码/诊断，不改变该次生产 UI 产物。

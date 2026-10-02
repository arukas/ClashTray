# 设计复核后续修复与回归（2026-10-02）

本次实施范围仅 R1 → R3 → R2 → 最终回归，不重新实施 A1—A6。实际起点 HEAD 为 `0029bf1f11b2b3347f1e7dc0fa656204e4cb12fe`，上一轮修改仍在未提交工作区。保留无关修改、未跟踪文件及全部历史报告，不提交、推送、发布、安装服务或修改当前机器真实网络。

本轮独立证据根：`D:\ClashTray\artifacts\design-followup-20261002-01a0fbde`。initial-git-status.txt 与 initial-working-tree.patch 保存起点；不把历史 725/725 或临时 probe 视为本轮验收。用户指定执行配置为 gpt-6.1-sol / max；本报告不声称通过编辑文档切换模型。

交付状态：R1、R3、R2 的代码修复、正式失败回归、切片验收和最终自动化门禁已完成。Release x64 全方案 0 警告 / 0 错误；完整 Core 749/749、完整 Integration 57/57、官方 Mihomo 强制门禁 19/19、正常执行令牌下 Windows 安全能力门禁 6/6，全部 0 跳过。Core 行覆盖率 79.88%、分支 73.38%，通过原 70% / 65% 门槛。真实网络与正式服务的系统验收范围见文末，不能把自动化通过解释为这些场景已验收。

环境：Windows x64（10.0.26300）、.NET SDK 10.0.400、Visual Studio Community 2026 18.9.12120.119、Windows SDK 10.0.26100.0。仅根 AGENTS.md 适用；已读取复核 review.md、probe 源码与输出、历史实施报告、架构/设置恢复/服务协议/测试环境说明及 CI。起点 Release x64 全解决方案构建通过，0 警告 / 0 错误，证据 baseline-build.txt。

## R1：安全关闭与设置恢复

修复前：正式 `NetworkDisableRecoveryTests` 六例全部失败（0/6，0 跳过），proxy DisableCount 与 service DisableCount 均为 0。证据 `red/core/red.trx`、red-core.txt。六例与 R3 同一次 Core 命令执行，总计 0/7。后续细化写失败 fixture，使 Previous/Attempted 的网络偏好与持久值一致，直接到达注入的存储写失败。

为核对细化 fixture 的修复前失败，另在独立 `red-calibrated-r1` 目录以最终测试程序集和复核 probe 留存的修复前 Core.dll 重跑这六例；6/6 实际执行且均因关闭计数 0 失败，0 跳过。没有回退源码或覆盖生产/正常测试输出。`binary-provenance.json` 保存 SHA-256；旧 Core.dll 与 `artifacts/design-review-20261002/probe/bin/Release/net10.0/ClashTray.Core.dll` 相同，SHA-256 为 `0E87C71BE8D555D6589E75987A4A6DEDC7726E13B683DF98545854DB0327618B`。混用程序集产生“旧 Core 无 NetworkDisableIntent”的其他类型发现警告，已原样保留在 output.txt；目标六例确实发现、执行并留下断言，故仅作为写失败 fixture 的补充证据，不作为最终门禁。正式修复前红结果仍是 red/core 与 red/integration。

设计：关闭路径先在原操作锁下保留关闭意图，再执行 OS 所有权恢复或向当前运行绑定发出服务 DisableTun；实际 Off 只取 OS/服务确认。存储恢复后置且拥有独立 3 秒预算；失败明确报告实际已关闭、偏好未保存。`SettingsRecoveryJournal` 管理一个最多 1 KiB 的 off-only 伴随记录 settings-network-off.json，旧 journal 被占用时仍能写入关闭意图。仅包含两个关闭位，无凭据、无启用指令、无通用事务框架。内存关闭意图在保存失败时仍生效；成功显式启用才清除对应关闭位，后台、旧恢复及重启不能清除。不可读取恢复记录时自动启用被门禁拦截。

文件：`src/ClashTray.Core/ClashTrayRuntime.Network.cs`、`ClashTrayRuntime.NetworkDisable.cs`、`ClashTrayRuntime.Settings.cs`、`ClashTrayRuntime.cs`、`SettingsRecoveryJournal.cs`、`AppPaths.cs`，以及 `tests/ClashTray.Core.Tests/NetworkDisableRecoveryTests.cs`。独立故障、迟到恢复、后台/重启和代理归属回归已完成；服务过期绑定由官方子进程用例另行验证。

R1 切片构建 r1-acceptance-build.txt：0 警告 / 0 错误。相关 Core 63/63，0 失败 / 0 跳过，`r1/accepted/r1.trx`。包含 13 个新增独立场景（六故障关闭、两种存储失败后后台/新绑定/App 重启/迟到恢复与显式重新启用、两种真实关闭失败、两种启用门禁、实际代理管理器的外部所有权冲突），以及原代理所有权、事务恢复、服务、启动和设置回归。新绑定模拟核心重启后同一生产偏好协调路径，不等同于正式服务/TUN 的机器验收。

关闭意图文件使用原子写及原权限保护。若全部持久化通道同时不可写，仍执行关闭并在本次进程内保持关闭，但不能声称已持久保存或保证断电后仍可读取意图；存在待恢复/不可读记录的 App 重启会阻止自动启用。此物理存储限制不阻止实际安全关闭。

最终追加并通过两个独立回归：启用其中一个机制只清除它的关闭位；伴随意图文件自身被独占占用时，自动启用仍被阻止，而两个关闭都实际执行。读取元数据/文件错误不使用 File.Exists 误判为不存在。后台门禁只阻止新的启用，不自动撤销用户仍希望保留且已确认运行的另一个独立机制。最终完整 Core 包含 R1 新增 15 个回归；服务的过期 TUN 绑定还由固定官方子进程回归确认被拒绝（DisabledProbeCalls 不增加）。

## R3：设置写入协调

修复前正式 `PendingRecoveryThenImportAndSettingsSavePreserveNewSelection` 失败：导入后 selected=True、journal=True，下一次设置保存抛出“持久化设置已被其他入口更改”。证据 red/core/red.trx。普通设置、代理/TUN、配置提交/回滚/删除和启动恢复的 AppSettings 写入口已清点并协调；schema 1 兼容及失败反馈已单独验收。

决定：复用一份 SettingsRecoveryRecord，以 Previous≠Attempted 推导受影响字段；当前该字段等于 Previous 或 Attempted 才可恢复 Previous，不同的第三值仍冲突。未改变字段采用当前持久值，完成合并后再应用关闭意图。沿用 schema 1，无需转换新 schema 或丢弃旧记录。旧版本/超限/不可读错误仍报告且保留。

AppSettings 持久写入清单：普通 UpdateSettings、代理/TUN SaveSettingsForOperation 均在现有门禁后建立 journal；操作失败回滚与启动/下次恢复均采用相同字段合并；配置选择/配置事务回滚/启动配置 journal 恢复/删除统一进入 SaveConfigurationSelectionAsync（复用 RecoverPendingSettingsAsync），配置执行及删除在副作用前也核对恢复。配置回滚仅改 ActiveConfigurationId，不写回整个 PreviousSettings。共有五处低层 SaveAsync，均为已准入提交、恢复或这个统一配置入口。

修改：SettingsRecoveryJournal.cs、ClashTrayRuntime.Settings.cs、ClashTrayRuntime.Configuration.cs；ConfigurationSettingsRecoveryTests.cs；SettingsRecoveryTests 的“外部冲突”fixture 改为 light→dark 后外部 system，保留真实同字段冲突断言（旧 fixture 是主题这一无关字段，不再应被拒绝）。切片全方案 0 警告 / 0 错误；相关 Core 61/61，0 失败 / 0 跳过，`r3/accepted/r3.trx`。覆盖原导入复现、切换/删除/重载后保存及 App 重启、schema 1 无关字段保留、真实同字段冲突、配置提交失败回滚、双 journal 启动恢复与关闭意图、未知 schema 反馈。R1 回归同时保留通过。

## R2：有界清理准入

修复前正式 `CompletedStatusQueriesAtDefaultCapacityDoNotPreventCleanupExecution` 失败（0/1，0 跳过）：128 个状态成功，Stop/DisableTun 均 OperationBusy，执行计数均为 0。证据 red/integration/red.trx、red-integration.txt。状态观察、副作用保留和安全清理配额已分离，并通过隔离官方进程验证真实核心停止。RequestId 去重、指纹拒绝、RecoveryOnly 不重放、TTL 与服务实例未知结果语义均保留；未操作真实 TUN。

决定：同一 RequestId 结果表分三份配额，128 观察 / 128 普通副作用 / 16 清理，总数最多 272；不淘汰未过期结果，执行中不超时删除，完成保留两分钟。Stop 只处理进程管理器自有子进程；匹配当前服务运行绑定的 DisableTun 使用清理配额，无效目标不能消耗该保留位。已准入清理在原 30 秒/15 秒预算内等待操作锁；等待中普通副作用不能持续抢占，队列受结果配额约束。TUN 每个请求在 single-flight 前验证目标，锁内再次核对，底层执行共享原服务期限。原协议 3、RequestId 指纹、RecoveryOnly、ACL 和服务独立所有权校验保持。

修改：ServiceRuntimeController.cs、ServiceCleanupAdmissionTests.cs、OfficialMihomoServiceInteropTests.cs。R2 最终切片全方案构建 0 警告 / 0 错误；隔离 Integration 23/23（`r2/accepted/r2.trx`），0 失败 / 0 跳过，包含三个新增官方子进程回归。相关 Core 中间 62/62（r2/core/r2-core.trx），最终以完整 Core 结果为准。默认观察及普通副作用结果同时占满后，真实 DisableTun 确认链执行、探针计数增加、官方进程实际退出，原 Start/Disable/Stop 结果仍可取回；压力测试排队 Stop 在原 30 秒内取得执行；手动取消服务期限时返回可恢复 OperationTimedOut，未声称核心已停止。官方门禁中间 18/18；后追加的期限回归计入最终 19 项，不以中间结果代替最终门禁。

上限不是无限清理保证：若单独耗尽全部 16 个未过期清理 ID，新 ID 仍返回 OperationBusy，原 ID 可恢复；测试验证此有界行为与 TTL 后释放。已在执行的长事务也不能被安全地强行打断，清理可能在原预算内超时；超时不报成功。不为消除这些约束淘汰需保护结果、增加无限队列或绕过目标/权限。

## 最终门禁与未验收场景

以下路径相对本轮证据根，命令从 D:\ClashTray 执行。切片构建和最终构建均是完整 ClashTray.sln，不只构建被修改项目。

| 验证 | 实际结果 | 证据 |
| --- | --- | --- |
| 修复前 Core 正式复现 | 0/7，7 预期失败，0 跳过 | red/core/red.trx、red-core.txt |
| 修复前 Service 正式复现 | 0/1，1 预期失败，0 跳过 | red/integration/red.trx、red-integration.txt |
| R1 切片 | 全方案 0 警告 / 0 错误；Core 63/63，0 失败 / 0 跳过 | r1-acceptance-build.txt、r1-accepted.txt、r1/accepted/r1.trx |
| R3 切片 | 全方案 0 警告 / 0 错误；Core 61/61，0 失败 / 0 跳过 | r3-acceptance-build.txt、r3-accepted.txt、r3/accepted/r3.trx |
| R2 最终切片 | 全方案 0 警告 / 0 错误；Integration 23/23，0 失败 / 0 跳过 | r2-final-slice-build-verified.txt、r2-accepted.txt、r2/accepted/r2.trx |
| 最终 Release x64 全方案 | 0 警告 / 0 错误 | final-build.txt |
| 最终完整 Core | 749/749，0 失败 / 0 跳过 | final-core.txt、final/core/core.trx |
| 最终完整 Integration（正常执行令牌） | 57/57，0 失败 / 0 跳过 | final-integration.txt、final/integration-normal-token/integration.trx |
| 固定官方 Mihomo 强制门禁 | 19/19，0 失败 / 0 跳过 | final-official.txt、final/official/official-mihomo-results/official-mihomo.trx |
| Windows 安全能力（受限令牌首次运行） | 2/6，4 环境失败，0 跳过；未算通过 | final-security.txt、final/security/security.trx |
| Windows 安全能力（正常执行令牌） | 6/6，0 失败 / 0 跳过 | final-security-normal-token.txt、final/security-normal-token/security.trx |
| 既有覆盖率门禁 | 行 79.88% ≥ 70%；分支 73.38% ≥ 65%，通过 | final-coverage-gate.txt、final/core/fba3936b-0965-4919-b696-079738f81b99/coverage.cobertura.xml |
| 工作区检查 | git diff --check 通过；初始 85 个修改/未跟踪路径仍存在；32 个无关已跟踪文件的初始 diff 与最终完全一致 | diff-check.txt、preserved-initial-paths.json、unrelated-tracked-preservation.json、final-git-status.txt |

官方和安全能力测试已包含在完整 Integration 57 项中，独立门禁的数量不是另外 25 个唯一测试。最终 Core 比历史基线增加 24 项（R1 15、R3 9）；Integration 新增 7 项（配额 4、官方进程 3）。`results-summary.json` 保存从各正式 TRX 读取的计数，`key-cleanup-proofs.txt` 保存最终 Integration 的关键标准输出：PID 24244 的官方子进程 HasExited=True、DisableTun 确认探针 132 次、原 Start/Disable/Stop 结果可恢复；持续普通请求压力下排队 Stop 实际完成用时 00:00:00.4786260，低于原 30 秒期限。此耗时是一次实际运行的观察值，正确性依靠可控任务及执行状态断言，不依赖固定 sleep 或概率竞争。

受限令牌首次安全门禁失败包含 CreateRestrictedToken 的 Win32 87、SSPI 无凭据及由此产生的 TLS 握手错误。经自动审批允许，以正常 Windows 执行令牌运行原有脚本及完整 Integration，6/6 和 57/57 通过。原失败 TRX 保留；没有关闭证书验证、放宽 ACL、降低断言或把跳过算通过。TLS/令牌正常环境已通过，正式 LocalSystem 服务及真实路由/DNS仍属于下面的未验收范围。

最终实际命令（`$followupRoot` 仅缩写独立结果目录）：

```powershell
$followupRoot = 'D:\ClashTray\artifacts\design-followup-20261002-01a0fbde'
dotnet build ClashTray.sln -c Release -p:Platform=x64 --no-restore

dotnet test tests/ClashTray.Core.Tests/ClashTray.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --collect:'XPlat Code Coverage' --logger 'trx;LogFileName=core.trx' --results-directory "$followupRoot\final\core" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura

$env:RUNNER_TEMP = "$followupRoot\final\official"
.\packaging\Test-OfficialMihomo.ps1 -Configuration Release -ExistingArchivePath 'C:\Users\Zen\AppData\Local\Temp\mihomo-windows-amd64-v1.19.31.zip'

$env:CLASHTRAY_MIHOMO_REQUIRED = 'true'
$env:CLASHTRAY_MIHOMO_PATH = "$followupRoot\final\official\clashtray-mihomo-test-v1.19.31-7f64a1584f1c43dcb2962428738b0e24\mihomo-windows-amd64.exe"
$env:CLASHTRAY_MIHOMO_ARCHIVE_PATH = 'C:\Users\Zen\AppData\Local\Temp\mihomo-windows-amd64-v1.19.31.zip'
$env:CLASHTRAY_MIHOMO_ARCHIVE_SHA256 = '38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67'
dotnet test tests/ClashTray.IntegrationTests/ClashTray.IntegrationTests.csproj -c Release -p:Platform=x64 --no-build --logger 'trx;LogFileName=integration.trx' --results-directory "$followupRoot\final\integration-normal-token"

.\packaging\Test-SecurityCapabilities.ps1 -Configuration Release -ResultsDirectory "$followupRoot\final\security"
.\packaging\Test-SecurityCapabilities.ps1 -Configuration Release -ResultsDirectory "$followupRoot\final\security-normal-token"
.\packaging\Test-CoverageGate.ps1 -CoverageDirectory "$followupRoot\final\core"
git diff --check
```

完整 Integration 和第二次安全脚本由正常执行令牌运行；第一次安全脚本由工具的受限令牌运行。官方脚本核验仓库固定 v1.19.31 归档 SHA-256、x64 PE 和核心版本，仅展开到本轮隔离目录。没有安装系统软件或正式服务，没有新增已安装核心的启动哈希检查。

切片命令均使用 `dotnet test <项目> -c Release -p:Platform=x64 --no-build --filter <过滤器> --logger 'trx;LogFileName=<名称>.trx' --results-directory <独立目录>`。实际过滤器按 TRX 的测试类/方法列出如下，切片数量记录当时测试程序集，最后追加的 R1 两例已进入最终 Core：

| 阶段 | 项目 / 过滤器 | 名称 / 独立目录 |
| --- | --- | --- |
| 修复前 R1/R3 | Core.Tests：`FullyQualifiedName~PendingRecoveryCannotPreventConfirmedNetworkDisable\|FullyQualifiedName~PendingRecoveryThenImportAndSettingsSavePreserveNewSelection` | red / red/core |
| 修复前 R2 | IntegrationTests：`FullyQualifiedName~CompletedStatusQueriesAtDefaultCapacityDoNotPreventCleanupExecution` | red / red/integration |
| R1 | Core.Tests：`FullyQualifiedName~ClashTray.Core.Tests.NetworkDisableRecoveryTests\|FullyQualifiedName~ClashTray.Core.Tests.SettingsRecoveryTests\|FullyQualifiedName~ClashTray.Core.Tests.RuntimeSystemProxyTests\|FullyQualifiedName~ClashTray.Core.Tests.RuntimeSystemProxyAddressTests\|FullyQualifiedName~ClashTray.Core.Tests.SystemProxyOwnershipPolicyTests\|FullyQualifiedName~ClashTray.Core.Tests.SystemProxyTransactionRecoveryTests\|FullyQualifiedName~ClashTray.Core.Tests.RuntimeServiceReconciliationTests` | r1 / r1/accepted |
| R3 | Core.Tests：`FullyQualifiedName~ClashTray.Core.Tests.ConfigurationSettingsRecoveryTests\|FullyQualifiedName~ClashTray.Core.Tests.SettingsRecoveryTests\|FullyQualifiedName~ClashTray.Core.Tests.RuntimeSettingsTests\|FullyQualifiedName~ClashTray.Core.Tests.RuntimeSubscriptionSwitchTests\|FullyQualifiedName~ClashTray.Core.Tests.ConfigurationSwitchCoordinatorTests\|FullyQualifiedName~ClashTray.Core.Tests.ConfigurationSwitchJournalTests\|FullyQualifiedName~ClashTray.Core.Tests.NetworkDisableRecoveryTests` | r3 / r3/accepted |
| R2 | IntegrationTests：`FullyQualifiedName~ClashTray.IntegrationTests.ServiceCleanupAdmissionTests\|FullyQualifiedName~ClashTray.IntegrationTests.ServiceRequestRecoveryTests\|FullyQualifiedName~ClashTray.IntegrationTests.BoundaryTests\|FullyQualifiedName~ClashTray.IntegrationTests.TunLifecycleTests\|FullyQualifiedName~ClashTray.IntegrationTests.OfficialMihomoServiceInteropTests` | r2 / r2/accepted |

R1 校准补证命令：`dotnet vstest <证据根>\red-calibrated-r1\ClashTray.Core.Tests.dll --TestCaseFilter:FullyQualifiedName~PendingRecoveryCannotPreventConfirmedNetworkDisable --logger:'trx;LogFileName=calibrated-red.trx' --ResultsDirectory:<证据根>\red-calibrated-r1\results`。

兼容性：AppSettings 及主恢复记录仍是 schema 1，旧记录受控合并且保留真正同字段冲突；关闭意图是同一恢复组件的两个关闭位，成功显式启用才清除。IPC 不增加字段、不提升协议 3、无新能力协商，现有协议 3 客户端与服务可通信；使用旧服务二进制时仍保留旧服务的容量行为，交付后的正式更新需同时采用修复后的 Service。Windows x64 / .NET 10 / WinUI 3、官方核心、受限服务 IPC、127.0.0.1 与空 secret 约束保持。未修改产品/依赖版本或分发方式，未继续 Runtime/YAML/UI 架构扩展；CI 阈值未降低，新增正式测试已纳入既有类别发现及计数门禁。架构、设置恢复、服务协议和测试环境说明已同步，历史验证报告未改写。

未验收：真实 System Proxy 的 OS 写入与恢复、真实 TUN On→Off 及路由/DNS、正式 LocalSystem 服务运行/重启/升级、隔离系统的安装升级卸载和睡眠/网络变化。本轮官方子进程配置始终 TUN Off，验证了服务确认路径和真实进程退出，不把已经 Off 的确认当成真实 TUN 关闭。核心新绑定与 App 重启的意图恢复通过隔离 Runtime 验证，正式服务跨进程崩溃及机器断电未验收。本轮没有重新做 WinUI 人工操作、屏幕阅读器、DPI、Windows 10 验收；相关历史记录不能代替本轮系统验收。用户当前真实代理/TUN及正式服务均未操作。

仍可能阻止关闭的条件须明确：外部代理所有权变化、过期/身份未确认的 TUN 绑定继续保守拒绝；清理自身的 16 个未过期独立 ID 被耗尽时新 ID 会 OperationBusy，正在执行的长操作也可能让清理在原期限内超时。全部持久化通道失败时，本次进程能保持关闭，但不能保证没有任何关闭记录的断电恢复。本次已复现的“设置恢复单独阻止安全关闭”“应用自己的配置写入形成永久冲突”“普通查询耗尽清理准入”均在正式回归中消除；不承诺在这些安全/资源/存储边界外无条件关闭。

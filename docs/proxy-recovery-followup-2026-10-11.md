# System Proxy 复审问题修复与验证（2026-10-11）

本次落实 `0.3.6-six-fixes-review-2026-10-11.md` 中的 R1–R4，并在最终自审补充相邻的 R5，以及推送后 CI 暴露的安全测试夹具问题，按问题分别提交。工作从 `a39539d` 开始，不新增功能。正式版发布仍暂停，本次只提交并推送代码。

## R1：本地启动取消后的代理恢复

本地核心启动期间，应用程序覆盖配置可能已经提交 System Proxy。随后调用方取消时，原路径只停止核心，遗留指向已停止核心的代理。

取消处理现在在停止本次本地核心后，使用独立的 10 秒清理令牌调用核心丢失恢复路径；向调用方保留原来的取消异常，同时保留 System Proxy 启用偏好。恢复仍遵守原有代理所有权判断。

新增 `OfficialMihomoStartupCancellationTests.LocalStartupCancellationAfterProxyCommitRestoresOriginalProxy`：使用固定版本的官方 Mihomo 子进程与生产 `SystemProxyManager`，在代理提交通知时取消启动，验证核心停止、运行绑定清除、完整原始代理状态恢复、备份与所有权文件删除以及启用偏好保留。注册表使用内存实现，TUN 关闭，数据和端口均为独立测试工件。

验证结果：完整 Release/x64 解决方案构建 0 警告、0 错误；新增官方 Mihomo 回归 1/1；停止恢复、一次性控制端口和 System Proxy 相关单元测试 30/30，均无跳过。

本地证据目录：`artifacts/proxy-recovery-followup-20261011/R1-514c0cd2f4fb408a9f0a55c0ea6490b4/`。

## R2：启用提交与崩溃恢复的版本顺序

原路径在最终存活检查之后才增加代理所有权版本。核心在检查刚返回 Running 后退出时，恢复任务记录旧版本、等待操作锁；启用操作随后更新版本，恢复任务因此被当成过期任务丢弃。

代理写入成功后现在先更新所有权版本，再读取最终存活状态。存活读取之前发生的退出由当场清理处理；读取之后发生的退出记录已提交版本，可以正常执行排队恢复。生命周期、控制器、进程和用户意图版本检查仍保留。

新增内部测试观察点及 `CoreLossAfterEnableLivenessCheckStillRevokesCommittedProxy`，在读取 Running 后触发生产核心丢失事件，等待真实排队恢复完成。修复前测试失败（撤销次数期望 1、实际 0）；修复后通过，并验证原启用偏好保留、绑定失效、核心 Failed、代理 Off。

验证结果：完整 Release/x64 构建 0 警告、0 错误；相关回归 9/9，无跳过。修复前证据目录为 `artifacts/proxy-recovery-followup-20261011/R2-red-aef297f903fd41b485de448241d72e7b/`；修复后为 `artifacts/proxy-recovery-followup-20261011/R2-be51d1f9c8c74471a53dca763b35dbe1/`。

## R3：不清空未完成恢复的错误提示

核心丢失清理现在返回明确结果：只有代理已确认 Off 才返回成功。异常仍写入脱敏日志；正常返回 RestoreRequired 等非 Off 状态也明确显示恢复提示。启用后的收尾以及启用前无可用监听端口的分支，都会保留未完成恢复的错误，继续保留启用偏好。

新增四个数据用例，覆盖抛 IOException / 正常返回 RestoreRequired，以及核心在启用之前 / 写入之后失效的组合。验证恢复状态、提示、用户偏好和异常日志。R1 的官方 Mihomo 回归同时再次通过。

验证结果：完整 Release/x64 构建 0 警告、0 错误；相关单元回归 41/41；官方启动取消回归 1/1，均无跳过。证据目录为 `artifacts/proxy-recovery-followup-20261011/R3-1ae54adb4dbc4c24a677afbff4ddc8b0/`。

## R4：无法解析的现存备份保持 RestoreRequired

关闭代理时，仅捕获备份反序列化产生的 JsonException，设置 RestoreRequired 并保留备份与所有权内容，不执行注册表写入或成功变更通知。文件读取失败、取消及注册表异常继续走既有错误路径，不作无差别吞异常处理。JSON null 与真正不存在备份的既有语义保持。

新增三个用例：非法 JSON、错误顶层类型 `[]`、空文件。验证状态、注册表不变、恢复写入与通知均为 0、两个恢复文件内容保持原样。

验证结果：完整 Release/x64 构建 0 警告、0 错误；备份恢复、所有权及核心丢失回归 27/27，无跳过。证据目录为 `artifacts/proxy-recovery-followup-20261011/R4-8cb754a4797a4cd0847221a2d1c2c921/`。

## R1–R4 整体验证

锁定依赖还原成功；完整 Release/x64 与 Debug/x64 构建均为 0 警告、0 错误。最终构建通过命令行指定已提交的 `VersionSuffix=alpha.4`，保留工作区原有的正式版版本号准备改动，避免将其纳入本次提交。

- Core 全量测试：794/794，0 失败、0 跳过；Integration 全量测试：63/63，0 失败、0 跳过。
- 固定官方 Mihomo v1.19.31 门禁：20/20；Windows ACL、受限令牌、TLS 安全能力门禁：6/6。两者均属于 Integration 63 项，不重复相加。
- Core 覆盖率：行 80.85%，分支 74.34%，超过既有 70% / 65% 门禁。
- R2 的确定性交错测试已确认原版本失败、修复版本通过。其余原问题也有上一轮隔离复现和本轮正式回归证据。

完整证据目录：`artifacts/proxy-recovery-followup-20261011/final-b1b11ad632694b7a91c4218435a3dff8/`，包含构建日志、官方与安全门禁 TRX、全量 TRX 和覆盖率文件。

测试使用独立目录、随机回环端口、内存代理注册表、TUN 关闭的测试核心及隔离服务工件。现有安装和真实网络环境未执行安装、升级、卸载、服务重启、TUN 操作或人工 UI 验收；这些限制仍按此前约定保留，不计作通过。正式版发布继续暂停。GitHub CI 结果在推送后的交付说明中记录。

## R5：已提交的启用在异常收尾时不作废恢复

最终自审发现，R2 修正了成功收尾的顺序，但异常收尾仍会无条件增加所有权版本。代理已提交、最终存活读取已返回 Running、核心随后退出并排队恢复时，如果清理意图文件失败，异常分支会再次更新版本，丢弃这次恢复。

新增 `EnableFinalizationFailureDoesNotDiscardQueuedCoreLossRecovery`：在内部观察点锁定独立目录中的意图文件，并触发生产核心丢失事件，导致收尾 File.Delete 抛 IOException。R1–R4 版本上，调用确实失败，恢复任务完成后撤销次数仍为 0，断言失败。证据目录为 `artifacts/proxy-recovery-followup-20261011/R5-red-9d91c59ff3654a2c89ce270c05be6982/`。

启用现在记录是否已完成所有权提交；成功提交后发生的收尾异常不会再次增加版本。未提交的启用失败仍保留既有版本失效处理。调用异常与设置偏好回退语义保持，排队恢复可以正常撤销已写入且已失去核心的代理。

五项修复最终整体验证：锁定还原成功；完整 Release/x64、Debug/x64 构建均为 0 警告、0 错误；Core 795/795、Integration 63/63，均无失败、无跳过；官方 Mihomo 门禁 20/20、安全能力门禁 6/6。Core 行覆盖率 80.86%、分支覆盖率 74.36%，通过原有门禁。

新鲜完整证据目录：`artifacts/proxy-recovery-followup-20261011/final-405093364f754b3980ff16b2fb8b427a/`。R5 新增回归在完整 Core 套件中通过，确认文件占用引发的原 IOException 继续返回、偏好继续回退、排队清理执行一次且代理 Off。正式版和机器验收限制仍如上所述。

## CI：安全测试监听子进程的启动等待与诊断

第一次推送 `733c113` 的 [GitHub CI](https://github.com/arukas/ClashTray/actions/runs/38116732200) 中，完整构建与官方 Mihomo 门禁通过，安全能力门禁 5/6。失败用例 `QueryOnlyProcessDaclStillConfirmsImageCreationTimeAndListenerOwner` 在读取 PowerShell 监听子进程端口的 10 秒等待阶段超时，尚未执行 ACL / 受限令牌断言，随后全量测试步骤被跳过。本次不将该次 CI 计为通过。

夹具现在提供有界的 30 秒启动预算，同时为读取和独立等待设置取消期限；并行读取标准错误，失败时报告子进程编号、退出状态及错误输出。子进程仍由 finally 清理，原有 ACL、身份、监听所有者和受限令牌断言保持，六项安全能力门禁及禁止跳过规则保持。

本地验证：完整 Release/x64 构建 0 警告、0 错误；安全能力测试 6/6；最终全部 Integration 63/63，0 失败、0 跳过。新鲜证据分别为 `artifacts/proxy-recovery-followup-20261011/CI-fixture-0f3a57eb387b4d23bc859dc62f118ab4/` 和 `artifacts/proxy-recovery-followup-20261011/CI-fixture-final-f52a0d7626f24ae88689694ad1e19a20/`。本项只修改测试夹具，生产代码仍是已通过 Core 795/795、行 80.86% / 分支 74.36% 覆盖率门禁的五项修复版本。最终远端 CI 状态见交付说明。

## 最终远端 CI 与后续发布授权

`64e970a73870ccaa7339b3740e057218ae6305c3` 的 [GitHub CI 38117201873](https://github.com/arukas/ClashTray/actions/runs/38117201873) 全部通过：Release/x64 构建 0 警告、0 错误；Core 795/795、Integration 63/63、官方 Mihomo 20/20、安全能力 6/6，均无失败或跳过；Core 行覆盖率 80.85%、分支覆盖率 74.39%，超过原有门禁。

本报告前面的暂停说明是修补提交阶段的历史决定。维护者后续明确要求按当前代码发布正式版，发布准备和仍未完成的机器验收见 [0.3.6 正式版验证记录](0.3.6-stable-readiness-2026-10-11.md)。

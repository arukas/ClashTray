# 测试环境与 fixture 边界

普通逻辑测试用隔离路径、可控任务和观察证据。CoreUpdater 的文件事务通过窄权限策略测试；生产默认 Windows ACL 策略保持不变，并由真实权限用例单独验证。不得为了让测试清理通过而放宽 ProgramData 权限。

TestFixtureDirectory 只注册本测试进程创建的 Temp/ClashTrayTests/GUID 根。清理校验绝对根、祖先和子项 reparse、当前创建者所有权，且仅在该根内恢复创建者删除所需 ACL。拒绝未知旧目录/真实用户数据；不递归处理未知 reparse 目标。临时文件在枚举后被原子移走只视为已清理；权限、归属、reparse 和其他 I/O 错误仍失败。文件分享冲突保留 2 秒短重试，主体和清理同时失败保留双异常及原堆栈。官方核心 fixture 也复用该生命周期。测试历史遗留目录不自动清理。

| 类别 | 能力与门禁 |
| --- | --- |
| RequiresWindowsAcl | 真实 ACL 读取/保护及仅 fixture 内的权限恢复 |
| RequiresRestrictedToken | CreateRestrictedToken 与 PROCESS_QUERY_LIMITED_INFORMATION 身份查询 |
| RequiresTls | Windows SSPI、真实 TLS 与证书校验；错误 CA 用例先证实正确 CA 成功 |
| RequiresOfficialMihomo | 仓库固定官方 x64 核心与匹配归档，真实验证/回环进程测试 |

受限执行令牌可能缺少 SSPI 凭据或令牌操作能力，必须记录环境不可用而非算成产品拒绝或通过。Integration AssemblyInitialize 只输出 OS、架构、runtime、令牌管理员/模拟状态等能力诊断，不采集凭据。TLS 不关闭证书验证，也不自动降级。

完整验收执行 Test-SecurityCapabilities.ps1（声明数量核对、每项 Passed，不能跳过）及 Test-OfficialMihomo.ps1。完整 Integration 显式设置 CLASHTRAY_MIHOMO_REQUIRED=true、CLASHTRAY_MIHOMO_PATH 和 CLASHTRAY_MIHOMO_ARCHIVE_PATH，归档必须匹配固定 SHA-256。CI 运行同样强制门禁并保存 TRX。覆盖率至少 Core 行 70%、分支 65%。每次结果目录独立，不用历史覆盖率或重写历史失败。

真实 UI、正式 LocalSystem 服务、实际代理/TUN、睡眠恢复、安装升级卸载、Windows 10、DPI/高对比度验收必须另行记录；普通用户隔离子进程与回环测试不是这些系统场景的替代。

资源释放须先于目录清理：尤其 await using 声明在 try 外时，普通 finally 先执行，不能在其中先删目录。RuntimeSubscriptionSwitchTests 使用真实独占文件句柄确定性验证这个顺序，主体/清理接入共享生命周期。共享 fixture 根在 TRX 标准输出记录，便于定位本次失败，无须扫描删除未知旧目录。

受限与普通 Windows 执行身份可能不同。清理必须以创建者 SID 验证，不能为方便而改变所有者或放宽真实数据权限。CLR FileSystemAclExtensions.SetAccessControl 只持久化修改的 DACL；不要把 PowerShell Set-Acl 申请 SACL 所需 SeSecurityPrivilege 当成清理前置条件。

2026-10-02 R1/R3/R2 后续回归使用独立根 `artifacts/design-followup-20261002-01a0fbde`，修复前红用例、各切片、最终 TRX 与覆盖率均保留。NetworkDisableRecoveryTests 使用隔离代理/服务、实际独占文件句柄和受控存储故障验证关闭及意图；ConfigurationSettingsRecoveryTests 验证配置与设置 journal 的组合。ServiceCleanupAdmissionTests 用可控任务/时钟验证配额、并发、指纹、取消和过期；OfficialMihomoServiceInteropTests 的新增清理用例运行固定官方子进程，真实确认 TUN 已经 Off（从未启用）及进程退出、普通压力下排队和可控期限结束。没有固定 sleep 制造竞争，也没有启动真实 TUN。

本轮受限令牌安全门禁 2/6（4 项环境失败：CreateRestrictedToken Win32 87、SSPI 无凭据），原始失败 TRX 保留在 final/security；普通执行令牌独立重跑 final/security-normal-token 6/6。不通过降低断言、跳过或关闭证书验证处理环境不足。CI 仍按所有 RequiresOfficialMihomo / 安全类别声明计数、每项 Passed，并执行 70% / 65% 覆盖率门禁；新增测试自动纳入，无需改 CI 阈值或依赖版本。

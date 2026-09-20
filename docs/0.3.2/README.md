# ClashTray 0.3.2 工程质量专项规划

> 文档状态：全部专项已实施（S1-S8），发布候选验证中
> 目标版本：0.3.2
> 功能基线：0.3.1
> 平台范围：Windows 10 22H2 / Windows 11，x64
> 最后更新：2026-09-21

> **范围声明：** 0.3.2 不加任何新产品功能。它只做工程质量收口：把 0.3.1 并发整改审查与 0.3.1 发布前排查中确认存在、但当时为控制回归风险而有意不动的结构性问题逐项修掉。Wi-Fi/SSID、ARM64、历史流量分析等仍不在范围内。

## 1. 来源

本清单的两个来源：

1. **0.3.1 并发整改审查遗留**：`docs/operation-state-concurrency-remediation-agent-vibe-2026-09-18.md` 驱动的审查共确认 14 项问题，其中 12 项已在 0.3.1 alpha.5 修复，剩余的结构性大改（锁协议、锁粒度、上帝类等）因回归风险被推后到本专项。
2. **0.3.1 发布前排查遗留**：Mini 安装器前置检测排查中确认的检测口径与版本漂移问题（检测本身经探针验证无误报，见第 4 节"已关闭项"）。

## 2. 专项清单

### S1. 操作锁令牌化：消除 `operationLockHeld` bool 协议

- **位置**：`src/ClashTray.Core/ClashTrayRuntime.cs`（60 处引用）。
- **问题**：锁所有权通过 bool 参数/返回值在调用链上手工传递，靠人脑配对获取与释放。任何新增路径漏传或传错即构成重入死锁或提前释放，且编译器无法发现。
- **方案**：引入 `AsyncOperationLock` 与 `OperationLockScope : IDisposable`（或 `IAsyncDisposable`），所有持锁路径改为 `await using`/`using` scope；所有权即词法作用域，删除全部 bool 出参。
- **验收**：`operationLockHeld` 零引用；现有全部测试通过；新增"持锁路径抛异常时锁一定释放"与"嵌套获取按冲突矩阵行为"的单元测试。

### S2. 全局操作锁按冲突矩阵拆车道

- **位置**：`_operationLock` 共 76 处引用（`ClashTrayRuntime.cs` 62 处、`MihomoProcessManager.cs` 10 处、`ConfigurationSwitchCoordinator.cs` 4 处）。
- **问题**：所有操作经同一把锁串行化，互不冲突的操作（如状态轮询与节点切换）也互相阻塞，锁持有时间长，UI 卡顿面大。
- **方案**：定义书面的操作冲突矩阵（核心生命周期 / System Proxy / TUN / 配置切换 / 只读查询），按车道拆分锁；TUN 与核心重启保持互斥（AGENTS.md 明确要求序列化）；所有锁获取必须有界超时。
- **验收**：冲突矩阵落入 `docs/architecture.md`；并发测试覆盖每对车道的允许/禁止组合；全锁路径超时测试。

### S3. 拆解 `ClashTrayRuntime` 上帝类（5545 行）

- **位置**：`src/ClashTray.Core/ClashTrayRuntime.cs`。
- **问题**：核心生命周期、配置与订阅、代理状态、TUN 协调、指标聚合、日志缓冲、设置持久化全部挤在一个类里，改动任何一处都要在全类上下文中推理，审查与测试成本高。
- **方案**：按职责拆为多个聚合组件，`ClashTrayRuntime` 退化为门面；拆分顺序与 S1/S2 协调（先锁令牌化，再拆类，避免在同一文件上叠加两种大改）。
- **验收**：App 层公共 API 面不变；每个拆分单元可独立单测；单文件行数阈值进入评审清单。

### S4. Service 结构化落盘日志

- **位置**：`src/ClashTray.Service/`（全项目 Trace 仅 5 处：Program.cs、ServiceRuntimeController.cs、TunTransactionCoordinator.cs）。
- **问题**：服务侧故障（TUN 恢复失败、管道请求拒绝、核心看门狗动作）没有持久记录，0.3.1 的 TUN 恢复兜底只能靠 Trace 调试监听，现场不可观测。
- **方案**：引入 `Microsoft.Extensions.Logging` 与一个有界滚动文件提供器，落盘到 `%PROGRAMDATA%\ClashTray\logs\service-*.log`（目录 ACL 受限：Administrators/SYSTEM 写，Users 只读）；服务启停、命令白名单拒绝、TUN 事务各阶段、恢复动作全部留痕；订阅 URL、代理凭据等敏感字段脱敏。
- **验收**：新增"服务事件 → 日志条目"集成测试；日志目录 ACL 测试；日志体积有界性测试。

### S5. SmokeTest 移出 Release 编译

- **位置**：`src/ClashTray.App/MainWindow.SmokeTest.cs`（873 行）。
- **问题**：UI 冒烟测试代码被编进 Release 产物，增大体积并扩大攻击面。
- **方案**：`#if DEBUG` 条件编译，或移入独立测试程序集（需先确认它是否承担对真实打包产物的探针职责）。
- **验收**：Release 产物中不存在 SmokeTest 类型；Debug 冒烟流程行为不变。

### S6. 收敛 CA1031 系统性豁免

- **位置**：`.editorconfig` 6 处分组豁免 + `src/ClashTray.App/ClashTray.App.csproj` 的 `NoWarn`。
- **问题**：catch-all（`catch (Exception)`）被系统性豁免，吞异常写法无法被编译器拦截，0.3.1 审查中的多个"静默 catch"正是这一豁免的直接后果。
- **方案**：逐一审计现有 catch-all：明确保留的加 `SuppressMessage` 理由注释；Core/Service 恢复 CA1031 为 warning；App UI 层保留有限豁免（用户可见错误路径）。
- **验收**：Core/Service 零 CA1031 警告；保留项逐条有理由。

### S7. Mini 前置检测版本中央化

- **位置**：`packaging/ClashTray.iss` 的 `RequiredWinAppRuntimeMajorMinor = '2.4'`、`packaging/Build-EXE.ps1` 的 `$miniDotNetMajorVersion = '10'`，以及真实来源 `Directory.Packages.props`（`Microsoft.WindowsAppSDK 2.4.0`）与 `Directory.Build.props`（`net10.0`）。
- **问题**：.NET 大版本或 Windows App SDK 升级时需要手工同步三处，漂移后 Mini 检测会要求与构建实际不符的运行时。
- **方案**：`Build-EXE.ps1` 从 `Directory.Packages.props` 解析 WASDK 次版本、从 App 项目 TargetFramework 解析 .NET 大版本，全部经 `/D` 传入 .iss；.iss 不再持有任何字面版本。
- **验收**：临时把 WASDK 版本改到更高次版本后，构建产物的检测下限自动跟随（可用探针构建验证）。

### S8. Mini 检测的用户身份口径（OTS 提权场景）

- **位置**：`packaging/ClashTray.iss` 的 `GetCurrentUserSid` + `HasWindowsAppRuntime`。
- **问题**：标准用户使用管理员凭据提权（over-the-shoulder）运行安装器时，`whoami /user` 返回的是管理员账户 SID，而 Windows App Runtime 按用户注册——检查查到的是管理员的注册状态，不是实际使用者的。可能误过（管理员装了、用户没装，运行时才崩）或误杀（用户装了、管理员没装，拒绝安装）。
- **方案**：优先取活动会话用户（WTS）而非提权账户；或同时校验 staged 状态作为兜底；至少在文档与提示中明确该场景的支持口径。
- **验收**：标准用户 + OTS 提权 VM 场景手动验收；支持矩阵写入 `docs/setup.md`。

## 3. 实施顺序与门禁

1. 顺序建议：S1 → S2 → S3（同文件大改串行），S4/S5/S6 可并行，S7/S8 随时。
2. 每个专项一个或多个聚焦 commit，禁止与功能变更混合。
3. 门禁：完整解决方案 Debug/Release 零警告；全部单元与集成测试通过；不触碰真实 TUN、真实服务与真实代理状态（沿用 0.3.1 红线）。
4. 每项完成后更新本文档状态与 `docs/releases/0.3.2.md`。

## 4. 已关闭项（不列入 0.3.2）

- **Mini 检测"误报"排查**：经 Inno 探针端到端验证，检测机制在真实提权上下文下行为正确（本机 PROBE-PASS）；真实问题是缺少运行时时的死胡同提示，已在 0.3.1 修复（`916aab2` 补充官方下载地址与按用户注册说明，`95105cb` 将 dotnet host 探测固定到 64 位 Program Files）。
- **行尾统一**：工作树已统一 CRLF，`core.autocrlf=true` 保证仓库层一致，无需仓库变更。
- **锁内日志快照分配的双锁重构**：会引入快照乱序回归风险，收益微小，明确放弃。

## 5. 实施状态（2026-09-21）

| 专项 | 状态 | Alpha | 说明 |
| --- | --- | --- | --- |
| S1 操作锁令牌化 | 完成 | alpha.1–alpha.2 | `OperationGate`/`OperationLease` 令牌化，`operationLockHeld` 零引用 |
| S2 锁冲突矩阵车道 | 完成 | alpha.3 | 冲突矩阵落 `docs/architecture.md`，车道并发与超时测试齐全 |
| S3 拆上帝类 | 完成（按修正口径） | alpha.4–alpha.8 | 拆出 5 个协调器（远程刷新、日志管线、代理操作、数据摄取、端点目录），`ClashTrayRuntime` 5411 → 3656 行；App 公共 API 面不变，每单元独立可测（共 27 个新测试） |
| S4 Service 落盘日志 | 完成 | alpha.11 | `RollingFileLoggerProvider` + ACL 收紧 + 全链路留痕；4 个新集成测试 |
| S5 SmokeTest 移出 Release | 完成 | alpha.9 | `#if DEBUG` + csproj 条件排除；Release 二进制零 SmokeTest 痕迹 |
| S6 收敛 CA1031 | 完成 | alpha.10 | Core/Service 五处分组豁免移除，27 处逐方法 `SuppressMessage` 附理由；另修复 App 分组豁免 glob 失效与 Service 被掩盖的 7 处 catch-all |
| S7 Mini 版本中央化 | 完成 | alpha.12 | `Get-MiniPrerequisiteVersions.ps1` 单一解析口 + Inno `#error` 强制 `/D`；漂移探针与 Mini 端到端构建通过 |
| S8 OTS 提权口径 | 完成（VM 验收待人工） | alpha.13 | 活动会话用户解析 + 两处调用点切换 + `docs/setup.md` 支持矩阵；OTS 真实 VM 场景列入发布前人工验收 |

S3 的修正口径（记录备查）：轮询循环、核心健康三元组与生命周期/配置切换/设置/TUN/系统代理编排保留在 `ClashTrayRuntime`。原因是轮询切片需注入约 18 个编排依赖（`_usingServiceCore`、`AdoptServiceTunState`、`SetController`、代理撤销、健康三元组等），拆出只是搬迁耦合而非消解耦合，且触及"核心丢失→撤销系统代理"的安全关键路径，收益/风险比不划算；既有 5 个协调器已满足"每单元独立可测、公共 API 面不变"的验收实质。

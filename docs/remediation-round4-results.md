# ClashTray 第四轮定向整改结果

日期：2026-09-24
范围：按 `docs/review-round3-followup-2026-09-24.md` 的 N1–N5 实施。没有重复整改已解决的 F1–F4；没有修改产品版本、技术栈、controller 空 secret、端点能力、锁所有权、恢复记录或 TUN 安全边界。

## 结果概览

| 项目 | 实现 | 自动化验证 | 隔离 UI 验证 | 真实系统验收 |
| --- | --- | --- | --- | --- |
| N1 YAML 文档/节点范围 | 完成 | Core 语料与官方 Mihomo 有效配置读取通过 | 不适用 | 未执行 |
| N2 有界线性扫描 | 完成 | 资源/取消/失败保留用例通过；Release 分配基准完成 | 不适用 | 未执行 |
| N3 官方 Mihomo 门禁 | 完成 | 官方 v1.19.31 归档校验通过；4/4 官方测试通过 | 不适用 | GitHub hosted workflow 未触发 |
| N4 有界异常定位 | 完成 | Core 全套及脱敏、栈帧、Aggregate 上限用例通过 | 不适用 | 未执行 |
| N5 列表 smoke/基准 | 完成 | Release 行重排基准完成 | Debug 无核心隔离 smoke 通过 | 未执行 |

## N1：统一 YAML 文档与节点范围

- `src/ClashTray.Core/RuntimeConfigBuilder.cs` 以 `YamlDocumentScope` 表示单文档范围，以 `YamlNodeRange` 表示字段节点范围；受管根字段和 TUN 直接子字段都按节点范围替换，跨行引号与块值的 continuation 不再残留。
- 当前支持范围明确为单个根 block mapping、根级零缩进、可选 `---` 开始标记和可选末尾 `...` 结束标记。受管字段在 `...` 前注入；结束标记后的注释保留。
- 多文档、整体缩进的根 mapping、结束标记后的非注释内容，以及受管 anchor/不平衡多行 flow 语法会在替换目标前明确拒绝。拒绝和取消均保留源文件与最后成功目标。
- `tests/ClashTray.Core.Tests/RuntimeConfigBuilderYamlCorpusTests.cs` 覆盖显式开始/结束标记、多文档、整体缩进、重复受管键、多行根字段、TUN block/引号 stack、邻接字段及注释。之前新增的 4 个重点回归先失败，修改后通过；本轮又加了重复根键与注释用例。
- `tests/ClashTray.IntegrationTests/OfficialMihomoInteropTests.cs` 新增隔离语义回归：用 `...` 输入生成配置，启动受管的固定核心，查询 loopback `/configs` 实际返回的 `mixed-port`、`allow-lan`、`tun.enable` 与 `tun.stack`。核心启动配置保持 TUN 关闭，源文件未变；这验证的是核心生效值，不只检查输出文本或 `-t`。

## N2：跨行引号扫描资源与取消

- 跨行引号值现在按源行/字符单次扫描，不再逐行整串复制或从头重扫；节点结束范围被根字段与 TUN 子字段共同使用。
- Builder 限额：输入最多 16 MiB、250,000 行、每行最多 64 Ki 字符；flow 集合深度最多 128。大扫描在 64 行/4,096 字符间隔检查取消。超限、未闭合引号、取消和失败路径不会替换已有输出。
- 新增 `tests/ClashTray.Core.Tests/RuntimeConfigBuilderResourceTests.cs`：覆盖字节/行/单行限额、预取消、长跨行扫描中取消、未闭合值和源文件/最后成功目标保留。
- Release 10.0.12 x64 基准通过 `tests/ClashTray.PerformanceHarness` 重复测量。旧结果来自 round-three Release audit；两组输入略有不同，按近似规模比较：

| 行数 | 旧输入字节 / 累计分配 | 当前输入字节 / 分配中位数 | 当前最大分配 | 当前耗时中位数 / P95 | 分配减少约值 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 256 | 13,172 / 3,417,176 B | 13,418 / 218,184 B | 220,688 B | 1.20 / 1.50 ms | 15.7× |
| 1,024 | 52,340 / 52,114,200 B | 53,354 / 785,168 B | 785,632 B | 1.49 / 1.86 ms | 66.4× |
| 4,096 | 209,012 / 825,140,136 B | 213,098 / 3,054,320 B | 3,054,992 B | 2.27 / 3.15 ms | 270× |

旧分配在每增加 4× 行数时增长约 15.3×/15.8×；当前增长约 3.6×/3.9×，与输入规模接近线性。另测 32 行、196,842 字节长行组合：当前分配中位数 2,011,912 B、最大 2,042,352 B，耗时中位数/P95 为 2.07/2.31 ms。累计分配不是同时驻留内存；耗时是本机基准，不是跨机器承诺。

## N3：固定版本官方 Mihomo CI/Release 门禁

- 新增 `packaging/Test-OfficialMihomo.ps1`，从 `packaging/mihomo-release.json` 取固定版本和 SHA-256；默认路径只下载 MetaCubeX 官方对应 ZIP，在测试前校验归档 SHA、归档中的唯一非空核心、PE x64 架构和核心报告版本，然后显式设置 `CLASHTRAY_MIHOMO_PATH` 及强制模式。
- 强制模式缺路径/核心、错误路径、零个测试、未发现的测试、跳过或任一非 Passed 结果都会失败。脚本把源码中声明的 `RequiresOfficialMihomo` 类别数与 TRX 实跑数相核对，不固定为“永远三项”。
- `.github/workflows/ci.yml` 与 `.github/workflows/release.yml` 都在常规测试前运行该门禁并上传独立 TRX。`GITHUB_ENV` 将强制核心路径传给后续完整测试。
- 本机默认下载路径已实际运行：归档 `mihomo-windows-amd64-v1.19.31.zip` SHA-256 为 `38b2420799d9e7cde77ec1a19c7150dd17ca77f7fb82d9f62cb8763a307eee67`，与 manifest 一致；提取出的 Windows x64 核心报告 `v1.19.31`。官方类别 4/4 通过、0 跳过。最终完整 Integration 回归也使用此已验证归档内的核心和强制模式。
- 工作流文件已修改并做本地检查，**GitHub hosted CI/Release workflow 本轮未触发，不能报告远端通过**。

## N4：有限异常栈帧与应用版本

- `src/ClashTray.Core/BoundedDiagnosticWriter.cs` 在原 16 KiB/条、3 个各 1 MiB 文件限额内增加版本、最多 8 个异常详情和每个异常最多 6 个方法位置；不输出 PDB 文件路径。
- 类型、消息、版本、方法位置分别限制为 160、512、64、256 字符，并将实际 UTF-8 字节预算用于格式化。`AggregateException` 只排队尚有预算的子异常，不先复制完整子异常集合。原有 URL、Authorization、token 脱敏与 writer 失败时返回 `false` 的行为保留。
- `tests/ClashTray.Core.Tests/BoundedDiagnosticWriterTests.cs` 增加不同抛出位置及 10,000 子异常 Aggregate 上限验证；完整 Core 套件通过。

## N5：Connections/Logs 隔离 UI smoke 与性能证据

- `src/ClashTray.App/MainWindow.SmokeTest.cs` 扩展既有 `--ui-smoke-test=<目录>` Debug 路径，使用合成快照和隔离存储，不初始化真实核心、服务、代理或 TUN。Connections 测 400 行的选择详情、newest/upload/download 排序、搜索及选择保留、generation 切换清空选择并重建行、滚动到末行；Logs 测 500 行上限、level/source/search 过滤、generation 重建、滚动和旧行淘汰。
- `src/ClashTray.App/LogsPage.xaml.cs` 与 `MainWindow.xaml.cs` 让日志行身份包含 controller/generation 与 sequence；相同序号换 generation 时重建旧行，避免跨端点复用。Connections 页已有 generation identity，本轮覆盖其行为。
- Logs 当前没有单行选择和排序控件，故 smoke 不增加产品控制；选择/排序在 Connections 验证，日志验证现有搜索、等级、来源过滤、滚动与淘汰。
- 隔离 smoke 退出码 0。400 行连接滚动范围 20,665；500 行日志滚动范围 19,770。同步快照更新本机实测：Connections 初次 5.31 ms、generation 更新 6.41 ms；Logs 初次 18.67 ms、generation 更新 9.37 ms、500 行淘汰更新 7.22 ms。过滤交互计时包含有意等待 UI 稳定，不作为算法基准。
- 新增 Release `StableRowReconciler` 基准：每种场景 40 次，1,000/2,000 行下覆盖稳定更新、完全逆序、部分逆序、头插入、淘汰和过滤。2,000 行逆序 P50/P95 为 9.33/9.85 ms；部分逆序 4.76/5.08 ms；头淘汰 0.56/0.63 ms。逆序确实产生 79,960 次 collection events，但 P95 仍低于本轮采用的 16.7 ms 单帧参考线；稳定更新 P95 为 0.44 ms、0 events。基于该测量，本轮保留现有重排算法，没有引入额外 diff 算法。此基准量的是 Core reconciler，不包括 WinUI 布局/渲染；真机布局仍需实测。

## 最终验证

- 完整解决方案 Release x64 build：通过，0 warnings、0 errors。
- 最终 Core Release：411 passed、0 failed、0 skipped。
- 最终 Integration Release：29 passed、0 failed、0 skipped；环境强制指定已验证官方归档核心。
- 官方 Mihomo 强制类别门禁：4 passed、0 failed、0 skipped；归档 SHA 校验通过。
- Release 性能 harness：完成 YAML 分配/增长与 Connections/Logs 共用列表重排基准。
- Debug 隔离 UI smoke：退出码 0，综合既有 smoke 与本轮列表交互检查。

首次最终全套回归中，既有 `RecoveryStopsOnlyExactPidStartTimeAndExecutableMatch` 用例一次观察到恢复目标身份变化，生产逻辑按现有安全策略保留进程并清理陈旧记录，未放宽 PID/启动时间/映像校验。该用例单独复跑通过，之后最终完整 Core 复跑为 411/411；本轮未修改进程锁所有权或恢复逻辑。若后续 CI 再现，应单独稳定该测试子进程夹具。

## 未完成验收与限制

- 本轮没有安装/升级/卸载 Windows Service，没有操作真实 System Proxy/TUN，也未执行睡眠恢复、网络切换或真实网卡/路由回滚验收；这些真实系统场景仍需独立 Windows 验收。
- GitHub CI/Release hosted workflow 未触发。门禁脚本、归档校验和本地官方类别均已验证，但托管 runner 的最终结果未知。
- Builder 有意拒绝多文档、整体缩进根映射、受管 anchor 与不平衡/多行 flow 语法；这是本轮明确的安全支持边界。
- UI smoke 是当前桌面环境的隔离渲染，未替代多显示器、所有任务栏位置、系统高对比度和多 DPI 的人工验收。
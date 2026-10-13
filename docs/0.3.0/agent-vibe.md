# ClashTray 0.3.0 Agent Vibe

> 这是给编码 Agent 的执行稿，不是新的产品需求。
> 它本身不授权实施；只有用户明确说“开始开发 / 按此文档实现 0.3.0”等指令后才进入编码。
> 正式依据是仓库根目录 AGENTS.md 与本目录的产品需求、技术设计、交付计划。

> **0.3.0 范围冻结：** 本版本不做 Wi-Fi/SSID 配置切换、SSID 读取、网络变化监听、网络规则编辑或位置权限处理。相关章节若保留，只能作为后续版本参考；Agent 不得为了补齐旧计划而重新接入生产网络上下文源。

## 先进入正确状态

你正在维护一个真实的 Windows 托盘代理客户端，不是在拼一个演示 UI。

你的目标是交付一个可以放心升级的 ClashTray 0.3.0：

- 0.2.0 的本机路径仍然稳。
- 手动配置切换可验证、可提交、可回滚；SSID 自动切换顺延到后续版本。
- 远程 Mihomo 好用，但永远碰不到本机特权边界。
- 中文和 English 完整，不再把用户文本散落在代码里。
- 成功路径舒服，失败路径也不会让用户断网。

保持这个判断标准：如果一个改动编译通过，但失败时可能留下死代理、重启风暴、凭据泄漏或跨目标误操作，它就没有完成。

## 开工前必读顺序

1. /AGENTS.md
2. docs/0.3.0/README.md
3. docs/0.3.0/product-requirements.md
4. docs/0.3.0/technical-design.md
5. docs/0.3.0/delivery-plan.md
6. 与当前切片直接相关的既有 architecture、release 和测试文件

冲突时优先级：

1. 用户最新明确指令。
2. 根 AGENTS.md。
3. 0.3.0 正式产品需求。
4. 0.3.0 技术设计与交付计划。
5. 本 Agent Vibe。

实现与编码工作使用仓库要求的 gpt-5.6-luna、reasoning effort max。

## 绝对边界

这些不是建议，是硬约束：

- 不把 Remote 当成另一台“本机”。
- 不让远程命令进入 Service、TUN、System Proxy、本机配置、本机核心启停或更新。
- 不靠隐藏按钮实现安全；Core 命令入口必须拒绝。
- 不把 secret 放进 URL、日志、异常、备份、测试快照或 UI 状态对象。
- 不提供 Ignore TLS、Accept Any Certificate 或 HTTPS 自动降级。
- REST 和 WebSocket 必须从同一个 EndpointTransportPolicy 创建。
- 不在网络事件回调里直接重启核心；0.3.0 不创建网络事件源。
- 不先保存 ActiveConfigurationId 再祈祷重启成功。
- 不乐观更新开关；显示 OS、Service、Mihomo 已确认的状态。
- 不一次性重写整个 ClashTrayRuntime。
- 不顺手加入便携版、ARM64、历史流量、全局热键、第三语言、账号或遥测。
- 不为赶日期删掉迁移、回滚、负向 TLS 或真实 Windows 验收。
- 不提交真实订阅、真实 SSID、secret、测试私钥、用户日志或构建产物。
- 未经用户要求不要自行 commit、push、建 PR 或发布。

## 开发节奏

每一个切片都按这个循环：

1. 读现有实现和测试，找出它当前真正保证了什么。
2. 先补能锁住现有行为的特征测试。
3. 做最小结构变更，让旧路径继续通过。
4. 加一个完整纵向能力，包括状态、取消、超时、回滚、错误和 UI。
5. 运行相关测试，再运行完整 solution 测试。
6. 如果涉及 Windows UI、代理、TUN、Service 或安装器，做真实环境验收并记录未验证项；不要为 0.3.0 启用 Wi-Fi/SSID 验收路径。
7. 检查 git diff，只保留当前工作项；不要覆盖用户已有修改。
8. 更新与行为直接相关的文档。

不要积攒一大批“稍后一起修”的编译错误或静态页面。任何时候都让本机主路径处于可运行状态。

## 实施顺序

### 0. 保护现场

- 检查 git status、当前分支、SDK、Windows SDK、WinUI workload 和 Inno Setup。
- 记录 dotnet restore、build、test 基线。
- 当前规划基线是 101 项测试通过，但必须在自己的工作树重新验证。
- 如果工作树不干净，识别哪些是用户修改；绕开它们，不 reset，不 checkout 丢弃。
- 先读 ClashTrayRuntime、MihomoApiClient、StateContracts、AppPaths、MainWindow、设置存储和现有集成测试。

完成信号：你能解释当前 core start/stop、配置切换、System Proxy/TUN 恢复和 controller generation 的调用链。

### 1. 先收口版本、存储和双语

- Directory.Build.props 成为版本真源。
- 移除 UI、Build-EXE、安装器链路里会漂移的硬编码版本。
- 为设置和新 store 建 versioned DTO，不让领域 record 的构造顺序成为磁盘协议。
- 建立 zh-CN / en-US Resources.resw。
- XAML 用 x:Uid，代码文本用 ResourceLoader；Core 只产生 ErrorCode + 参数。
- 资源测试检查键、占位符、空值和关键硬编码文本。
- 语言改变不触碰 core/service/network。

完成信号：两个语言资源在 unpackaged Debug、Publish 和安装包里都能加载；0.2.0 fixture 无损迁移。

### 2. 把“本机设备”和“controller 会话”拆开

先建立类型，再搬职责：

- LocalDeviceSnapshot：Service、local core、config、System Proxy、TUN。
- ControllerSessionSnapshot：一个明确 endpoint 的 Mihomo 数据。
- AppSnapshot：把两者组合给 UI。
- EndpointId、EndpointKind、SessionGeneration。
- EndpointCommandPolicy：按能力 allow-list。
- LocalDeviceCommand 与 TargetCommand 是不同类型、不同入口。

先把现有 loopback controller 包装成 Local EndpointSession。此时不要急着做远程 UI。

完成信号：现有本机功能行为不变；构造 Remote kind 的负向测试无法触发任何本机命令。

### 3. 建唯一的配置切换事务

把 SetActiveConfigurationAsync 从“写设置 + 重启”升级成真正事务：

- 同一个 operation lock。
- 验证 candidate。
- 写 journal 和 checkpoint。
- core 停止时只提交配置，不启动。
- core 运行时先让 owned System Proxy 安全，再提升 runtime 配置并重启。
- 检查新 generation、REST health、配置身份、TUN。
- 健康后恢复 System Proxy，最后提交 ActiveConfigurationId。
- 失败自动回旧配置。
- 回旧配置也失败时，代理必须安全关闭，TUN 必须是已确认安全状态或明确 Failed。
- App 在任何阶段崩溃，下一次启动都能幂等恢复。

取消只发生在安全点。已经开始交换 runtime 文件时，必须完成 commit 或 rollback。

完成信号：成功、预检失败、启动失败且回滚成功、双重失败、进程中断恢复五类测试都能证明最终网络状态。

### 4. SSID 自动切换顺延

0.3.0 不实现 SSID 自动切换，也不创建 `WindowsNetworkContextSource`。不读取 SSID、不监听网络变化、不保存网络规则、不申请或引导位置权限；`INetworkContextSource`、`NetworkRuleStore`、`NetworkSwitchPolicyEngine` 和事件管线只能作为后续版本的设计/测试预留。

如果未来重新立项，必须从权限、隐私、latest-wins、防抖、冷却、手动覆盖、回滚和真实 Windows 验收重新建立证据；本版本的隐藏 UI 和合成 smoke 不构成这些能力的交付证明。

### 5. 统一 transport，再开放远程

在增加远程地址之前，先让本机 REST 和 WebSocket 也走统一工厂：

- EndpointUriNormalizer。
- EndpointTransportPolicy。
- EndpointTransportFactory。
- HttpClient + ClientWebSocket 同源认证和证书判断。
- UseProxy=false，避免 controller 流量绕回当前系统代理。
- 每个请求带 endpoint + generation。
- 响应提交前再检查一次。

然后增加：

- EndpointStore，只有非秘密元数据。
- EndpointSecretStore，DPAPI CurrentUser。
- EndpointCertificateStore，逐端点 CA，不含私钥。
- EndpointSessionManager，只维持当前活动目标的实时流。
- 1/2/5/10/30 秒有抖动退避。
- HTTPS system trust。
- HTTPS custom CA，仍检查 hostname、validity、usage 和完整 chain。
- HTTP explicit exception，持续警告，永不自动降级。

端点测试必须只读。删除活动端点要先取消 session，再删除 secret/CA，目标回到 local；本机网络一动不动。

完成信号：REST 与 WSS 跑过完全相同的正负 TLS 矩阵；远程能力拒绝测试证明没有 Service IPC。

### 6. 最后做 UI 和发行硬化

- 目标身份始终可见，Remote 和 HTTP warning 不只靠颜色。
- 本机 controls 不跟着 target selector 漂移。
- 远程离线数据有陈旧时间，不能冒充 Connected。
- 长名称、中文、English、键盘、Narrator、高对比度、200% DPI 都检查。
- 日志洪水、大 groups、大 connections、慢 endpoint、half-open WebSocket 都有上限。
- Full / NoCET / Mini 使用同版本、同资源、同数据格式。
- 从 0.2.0 真升级；不要只测干净安装。

完成信号：交付计划的发布门禁全部有证据，停止发布项为零。

## 写代码时的判断法

遇到模糊点，按这组问题判断：

1. 这是本机设备动作，还是目标 controller 动作？
2. 谁确认了当前状态：UI 猜测、Windows、Service，还是 Mihomo？
3. 操作中途取消或进程崩溃，机器会停在哪里？
4. 这个响应还属于当前 endpoint 和 generation 吗？
5. REST 与 WSS 是否真的用了同一份安全策略？
6. 错误文本里可能带 secret、SSID、URL query 或用户路径吗？
7. 新 store 坏掉后，本机代理还能启动和恢复吗？
8. 这项工作是 0.3.0 必需，还是悄悄扩到了 P2-B/P2-C？

如果答案不清楚，先写一个失败测试或画出状态转换，再动生产代码。

## 推荐的代码形状

偏好：

- 小而明确的 immutable request/result。
- typed error，不吞异常。
- async + CancellationToken 贯穿文件、HTTP、WebSocket、IPC。
- operation ID、endpoint ID、generation 明确传递。
- Core 里的纯策略，App 里的 Windows/UI adapter。
- 单一职责的 coordinator，不建立没有第二实现或测试价值的空接口。
- 集中管理 timeout、limit 和 retry。
- 原子存储，候选文件和提交点清楚。

警惕：

- 一个 bool 同时表达“用户想要”和“系统确认开启”。
- 一个 IsRemote 在几十个 if 中充当安全边界。
- 一个通用 Execute(url, action) 能同时打本机和远程。
- 任意证书 callback。
- async void 网络操作。
- UI event handler 里直接写设置、启停 core 或调用 Service。
- catch Exception 后只返回 false。
- 为了过测试而延长无限 timeout 或取消大小上限。

## 验证命令基线

按实际环境调整路径，但至少执行：

~~~powershell
dotnet restore ClashTray.sln
dotnet build ClashTray.sln --configuration Debug --property:Platform=x64
dotnet test ClashTray.sln --configuration Debug --property:Platform=x64
dotnet build ClashTray.sln --configuration Release --property:Platform=x64
dotnet test ClashTray.sln --configuration Release --property:Platform=x64
~~~

涉及发行时再构建 Full、NoCET、Mini。构建安装器会使用网络下载固定第三方资产或需要 Inno Setup 时，先遵循环境授权要求。不要因为本机缺少真实 Wi-Fi、TUN、安装器或 Windows 10 环境就把“未验证”写成“通过”。

## 每个切片的交付回报

向用户汇报时先给结果，再给证据：

1. 这次交付了什么用户可见结果。
2. 哪些关键文件改变，为什么。
3. 跑了哪些 build/test，精确结果。
4. 做了哪些真实 Windows 验收。
5. 哪些内容没有验证，以及原因。
6. 当前已知限制和下一个安全切片。

不要用“应该能工作”代替结果，不要用“编译成功”代替代理、权限、迁移和回滚验证。

## 什么时候停下来问用户

只有缺失决定会实质改变以下事项时才暂停：

- 远程安全模型或证书信任范围。
- 数据丢失、向下兼容或卸载语义。
- 发行范围、平台或 0.3.0 功能边界。
- 需要安装系统软件、证书、驱动或改变机器级设置。

普通实现细节按正式文档中最安全、最小的解释继续，不把每个命名和文件布局都变成用户阻塞点。

## 排期吃紧时

先保住：

1. 本机回归。
2. 配置切换事务。
3. 数据迁移。
4. TLS、secret 和能力隔离。
5. 真实 Windows 与升级验收。
6. 0.3.0 范围和发布说明与实际代码一致。

如果仍然不够，整个 Remote Endpoint 切片移动到 0.3.1。不要交付“能连上，但证书、WebSocket、回滚和权限以后再补”的版本。

## 终局画面

0.3.0 完成时，用户从 0.2.0 覆盖升级：

- 原代理和设置都在。
- 什么新功能都不会擅自开启。
- 0.3.0 不出现 SSID 自动切换入口，也不读取或监听 Wi-Fi/SSID；该能力顺延到后续版本。
- 添加远程端点时默认走 HTTPS，secret 受保护，错误证书在 REST/WSS 都被拒绝。
- 看远程节点时，本机 System Proxy、TUN 和 Service 仍明确属于这台电脑。
- 任何断线、睡眠、崩溃、坏配置或坏存储都不会让 UI 假装成功，更不会悄悄留下断网状态。
- 中文和 English 都完整。
- 三个 x64 安装包、版本和发布清单完全一致。

这才是可发布的 0.3.0。

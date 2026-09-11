# ClashTray 迭代计划

这份计划把 ClashTray 按可验证的纵向切片推进，每轮都先保留可运行路径，再扩大范围。P0/P1 完成后才考虑 P2。

## 已完成的基础切片

- 托盘图标、真实托盘矩形定位、紧凑面板、单实例和显式退出。
- Mihomo 独立进程、loopback controller、配置校验、启动/停止/重启和崩溃恢复。
- 配置与订阅存储、模式/节点切换、延迟、System Proxy 所有权和恢复。
- Provider、规则、连接、流量、内存和有界日志页面。
- Windows Service、受限命名管道、TUN 操作、安装/升级/卸载回滚。

## 本轮分发切片

- 用 Windows 原生 README 说明日常路径、体积、运行时和故障边界。
- 保持 ClashTray MIT，补齐 Mihomo、WinUI 和 ClashBar 的致谢与第三方说明。
- 把发布拆成 Full、NoCET、Mini 三种 x64 变体，所有安装器生成 SHA-256 sidecar，并随 Full/NoCET 提供 Mihomo GPLv3 来源元数据；Mini 安装前检查 .NET 10 Desktop Runtime 和 Windows App Runtime 2.4+。
- 增加 CI 与 tag / 手动发布 workflow，发布前自动运行完整构建和测试。
- 收紧 `.gitignore`，忽略核心二进制、构建输出、订阅/日志、压缩包、签名材料和运行数据。

## 下一轮验证顺序

1. 在干净 Windows 10 22H2 和 Windows 11 x64 上安装 Full、NoCET、Mini；为 Mini 分别验证运行时已安装和缺失的提示。
2. 验证缺失核心、核心崩溃、控制器端口冲突、无效 YAML 和订阅失败的恢复提示。
3. 验证 System Proxy 竞争写入、TUN 失败回滚、睡眠/恢复、网络适配器变化和服务重启。
4. 验证多显示器、四边任务栏、高 DPI、深浅色、高对比度、Explorer 重启和托盘重建。
5. 验证升级和卸载的代理恢复、服务清理、保留/删除数据选择。
6. 再考虑可选签名、MSIX 正式渠道和 ARM64；这些不会阻塞当前 x64 EXE 发布路径。

每次继续开发都从最前面尚未通过的验证项开始，避免在静态页面或未验证的发布脚本上继续堆功能。

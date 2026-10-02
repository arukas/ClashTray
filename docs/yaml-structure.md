# YAML 结构与资源边界

2026-10-02 优化前以当前 RuntimeConfigBuilderYamlCorpusTests、YamlMappingKeyReaderTests、MihomoListenerPlanAnalyzerTests 和官方互操作测试为基线。生成器保真复制非受管行；结构层识别范围，不对整个配置反序列化再输出。不引入解析库、依赖或许可证变化。

| 语法 | 运行配置生成 | 监听证据 |
| --- | --- | --- |
| 单个 block mapping、可选 --- / ...、注释 | 支持；受管值位于文档结束前 | 同一文档边界；无效/多文档不能声称完整 |
| 普通/单引号/双引号键；双引号的 JSON、x、U 转义 | 共享键解码；单引号反斜线保留字面意义 | 采用同一解码结果 |
| 受管重复键、block scalar、多行引号、单行嵌套 flow | 整个受管节点替换；TUN 只改直接字段；深度限 128 | 不可识别的监听值报告 incomplete，不能当不存在 |
| 非受管 anchors、aliases、merge keys、block scalars、flow | 原文保留 | DNS/listeners、有效代理字段或根 merge 引入无法确认的语法时 incomplete |
| 受管节点定义 anchor；TUN anchor/alias | 明确拒绝，防止替换破坏引用 | 无法确认不能形成安全启动证据 |
| 根 flow mapping、整体缩进根、复杂/标签/anchor 键、多文档、结束后有效内容 | 明确拒绝，旧运行文件保留 | incomplete；不猜测为没有监听 |
| DNS/custom listeners 的 block 写法 | 原文保留 | 固定地址、支持类型可确认；inline/alias/未知类型仍 incomplete |

限制保持 16 MiB 实际读取字节、250,000 行、单行 65,536 字符、flow 深度 128。读取按实际流计数而非仅依赖读前/读后 FileInfo，输入增长不能绕过上限。BOM 识别沿用 .NET ReadAllLines；取消贯穿读取、扫描及写入。节点替换仍采用临时文件、原 ACL 保护和原子移动，受限路径/候选验证/失败后的旧文件与元数据恢复不变。官方 Mihomo 是最终语义校验，不把共享结构层当作完整 YAML 解析器。

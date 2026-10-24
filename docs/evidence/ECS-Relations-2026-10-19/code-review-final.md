# ECS 关系：最终双轴审阅

审阅基点：`80f446207d2968cf0c0a0636b0c1429e44f8fe37`。首次审阅覆盖基点至 `93ea3a91fb00da64a38ea3b95f42f8a2856f2115`；行为、生成和访问修复增量复核至 `5b6c96e8ed4f78e4c2a617c345e49623a19d5c91`，随后逐项复核分区任务修饰符、空间描述持有、普通组件组结论及几何空分区压缩。最终C#源码为 `c0effa290eee225c296f779a85b9d79109d789a5`。首次结果见 [初次审阅](code-review-initial.md)。

## Standards

| 发现 | 最终实现与验证 |
| --- | --- |
| 输入依赖检查记录调用入口，证据覆盖到实际输入读取较弱 | 谓词实际读取 Body.Bounds 后记录事件，写者在写回调内记录事件；最后事件为写者。相关行为检查通过。 |
| 元数据单项 Fact 同时覆盖多种行为 | 按登记存储、普通与生成 Job、无向端点和自身边策略划分，GeneratedMetadataTests 共五项。五项通过。 |
| 五个生成器重复拼接泛型签名 | RelationTypeSyntax 统一类型参数、可见性和约束的格式规则，具体模板保持各自数据与行为。 |
| 三种启用 API 由模式切换共用动作 | WorldEnableChangesRefreshMaintainedMembers、RowEnableChangesRefreshMaintainedMembers、ChunkEnableChangesRefreshMaintainedMembers 各自执行动作，每项 Enabled/Disabled 两行，断言空集→字面实体对→空集。六行通过。 |
| QueryRow/QueryChunkView 与 Components 重复检查组件存在 | 组件存在检查集中 Components；Chunk 保留行边界及声明权限。Core 对应启用写入检查通过。 |

Standards 修复复核结束：2026-10-19T23:42:36Z。

最后两行增量 Standards 复核结束：2026-10-20T00:02:28Z。CountJob、FillJob 的本地来源直接参与泛型调用；串行和并行继续逐分区形成 TJob local，查询借用及返回前 join 保持原结构。[实际原生函数](focused-native-r42/native-PartitionData.asm)确认72B任务副本保留，后续64B来源副本消去。

空间描述集中持有增量对应 `73f7e089990e65cf4fc5517593e31aaeee03298a`，Standards复核结束于2026-10-20T00:22:17Z。只读数据对象持有Source、Filter，公开getter继续按值返回，具体泛型与查询读取声明保持；默认相等按数据引用身份比较，记录于[接口调整表](../../ECS-Relations-API-Changes-2026-10-19.md)。该源码输入保持1项、NativeAOT发布与源确认均通过。

普通组件组结论直接表达对应 `bf4348ce600cd423090d1c1430596fc60ea97a2e`。Standards基于精确两行diff和此前源码上下文进行消息复核，通过；root逐行源读取结束于00:38:13.5649923Z。该类型的组结论为Unknown，叶子继续求值，结果通过当前调用的out参数交付，数据持有结构保持。实际Native确认该常量进入遍历，见[r44证据](focused-native-r44/)。

共享分区几何压缩对应 `c0effa290eee225c296f779a85b9d79109d789a5`。Standards基于精确diff和既有源码上下文进行消息复核，通过；root逐行源读取结束于00:53:06.2645976Z。overlapCount表达有效分区数量；同一Span持有原序分区，写位置始终≤读位置。筛选使用原遍历入口的几何条件，零分区沿现有完成依赖与Count清零路径处理。既有五项保持关闭。

## Spec

| 发现 | 最终实现与验证 |
| --- | --- |
| Enabled/Disabled 变化需要更新维持空间成员的实体变更记录 | 三个公开入口统一进入 Components.WriteEnabled；实际位变化调用既有 RecordTableChange。实体变更记录与观察读取共用锁，克隆在源锁内复制。原有组件值版本保持自身用途。三种入口的六行行为均验证维持成员随启用变化更新。 |
| 三项性能实测超过批准阈值 | r41完成对应大规模及关联场景回归；最终c0源码的稀疏/均匀并行均等价，median/p95分别为0.7474747474747475/0.7210204081632653和0.7973345588235294/0.7976262473540974。按[逐case最新实际记录](focused-native-r45/latest-case-coverage.json)汇总108/108等价、126/126批准通过，同函数波动独立列示。 |

启用变化修复 Spec 复核结束：2026-10-19T23:38:48Z。

分区压缩增量的Spec消息复核通过：原遍历立即跳过的几何空节点对在调度前剔除，其余分区保持原序、互斥覆盖和唯一输出；G/H共用同一分区算法与布局。复核认可既有四对结果检查、实际AOT、稀疏与均匀两项等价及性能反馈作为该增量的最小回归范围。此前启用成员修复保持闭合。

## 实际检查与产物

- `81722effa0b94a66a23069a90e9b13ce`：16项相关检查通过，覆盖实体变更、元数据和实际输入访问。
- `d22151786b8a4cf8a1e501e300729ff3`：5b6c源码的7项增量检查通过；实际 NativeAOT 发布及命令行源确认均 Exit0。
- `1ac977d0d3d4467e99b77379b05c78b7`：5b6c源码六个原生函数各选中一次，Default 组件列直接按有效行号取引用，Mutual 候选按引用遍历，分区任务仅读取共享数据引用及分区编号。见 [原生核对片段](focused-native-r41/native-confirmation-excerpts.txt)。
- `45457c0a0ea343458c8de1c83d1ba0f8`：最终c0源码的四对串并行行为1项通过，实际NativeAOT发布及源码确认均Exit0；两场反馈严格及批准口径均通过。见[本轮原始表](focused-native-r45/approved-summary.json)。

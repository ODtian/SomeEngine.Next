# 双轴审阅：初次发现

固定基线：`80f446207d2968cf0c0a0636b0c1429e44f8fe37`。审阅提交：`93ea3a91fb00da64a38ea3b95f42f8a2856f2115`，包含393a9之后的三个文件调整。两名独立读者分别负责Standards和Spec。Standards的host读取结束于2026-10-19T23:26:33Z，Spec结束于23:26:37Z；后续补充读取使用远端固定提交。

## Standards

1. **P2，行为检查证明。** `SpatialParallelTests.CountAndFillKeepTheirComponentInputUntilEveryPartitionCompletes` 记录计数开始后排队writer，但`PausingContacts.Matches`总返回true；原树保存的范围使原有字面结果在提前写入时也可能成立。按`Behavior-Checks.md`的显式同步与事件顺序规则，原检查应直接记录最后读取与writer进入的先后。
2. **P3，行为检查粒度。** `GeneratedMetadataTests.cs`中的同一Fact包含已存边端点角色、自边策略和另一类型`KnownInvalidLink`的首次schema验证。按每个测试只表达一条行为的规则，三项已有行为各自命名。
3. **P3，判断性Duplicated Code / Shotgun Surgery。** RelationGenerator、RelationEdgeGenerator、RelationImplementationGenerator、BitmapRelationGenerator、SpatialRelationGenerator各自复制TypeParameters、IsPublic和AppendConstraints。同一可见性与泛型约束规则可集中在窄辅助，具体生成模板由原生成器持有。

## Spec

1. **P1，Enabled/Disabled成员维护。** #231要求沿用Enabled、Disabled成员选择。`CollisionMaintenance`按实体表变化与组件版本更新；`Chunk.WriteEnabled`只改mask。触发用例为Geometry集合中的B初始被Enabled条件筛掉，启用后再次维护应加入B并产生字面(A,B)，原增量入口继续保留旧树。修复沿既有成员变化事实完成，并保留原组件值版本语义。
2. **既有性能完成项。** #243要求各项原精度median、p95比均≤1.01。r39已存的三条余项为threads-small-sparse-parallel、finite-large-mask8-mutual和default-large-dynamic90，完整绝对ticks及原比值见[性能记录](../../ECS-Relations-Performance-2026-10-19.md#输出与并行前缀改动反馈r39)。93ea3的后续改动反馈按其实际产物与测量记录。

Standards共3项，其中2项来自项目行为检查规则、1项为判断性smell；最高优先级P2。Spec确认1项新功能问题、1组既有性能完成项；最高优先级P1。

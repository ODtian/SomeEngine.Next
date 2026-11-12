# ECS 重构规范

分析时间：2026-10-22 22:50 +08:00。状态：D算法与协议方案；业务选择见[[Execution]]，固定源码计数与设计预算分别列示。

分析方法：从全部保留能力出发，固定主数据持有者、公开入口、逐步算法、首次/扩容/维护/完成时点。逐项领先EasyEcs、Friflo、Arch、DefaultEcs的门槛与共同输入由[验收定义](Acceptance.md)持有。S-E/S-O/S-W/S-B表示相应源码依据，D/E*表示设计与能力适配。

开始实现先读[实现接口与批次](Implementation.md)，进入目录先读[AGENTS与渐进式索引](Navigation.md)，随后按下表选择本次专题。

## 阅读顺序

| 顺序 | 页面 | 该页持有的事实 |
|---|---|---|
| 1 | [执行与引用寿命](Execution.md) | 跨功能顺序、结构范围、一次定位、迁移/释放/恢复组合 |
| 2 | [数据、依赖与接口](Data-and-Dependencies.md) | 唯一持有者、32 B列项、具体接口、目录和生成器 |
| 3 | [操作计数](Cost-Model.md) | 向量、复制/清零、用户计算、公共内核与容量规则 |
| 4 | [存储与查询](Storage-and-Queries.md) | 身份/值定位、默认类型绑定与显式缓存、查询缓存及遍历 |
| 5 | [首次构造与增长](Construction.md) | 类型位图、表/边/查询首次、过滤内核和容量维护 |
| 6 | [批量与复制](Bulk-and-Copy.md) | 批次创建/修改/销毁、填洞、Clone/Prefab与引用修正 |
| 7 | [变化与事实寿命](Changes.md) | Added/Written、摘要、Removed旧值、销毁序列与观察基线 |

## 能力专题

| 页面 | 数据与执行范围 | 首次、维护与完成 |
|---|---|---|
| [Buffer与资源](Containers.md) | 私有缓冲数据、只读/可写视图、行内缓冲及资源值 | 池、溢出、独立复制、引用寿命与实际释放 |
| [Shared](Shared.md) | 唯一主值、归并、逐行共享编号与成员 | 首次主值/成员建立、增长、编号寿命与恢复 |
| [值索引、排序、分组](Indexes.md) | 一次GetKey、组号反查、紧密成员、GroupBy | hash组首次/增长/回收、有序组号范围 |
| [Sparse](Sparse.md) | 分页cell、紧密值、实际附着链 | 新页、销毁整链、混合查询与恢复 |
| [关系与Hierarchy](Relations.md) | Parent/Children原生组件、主端点、Applied和当前/延迟目标索引 | 最终集合校验、即时/延迟维护、普通销毁及恢复 |
| [CommandBuffer](Commands.md) | 20 B内部头、typed载荷、producer及批次创建结果 | 顺序提交、持久Resolve、临时字段修正、失败前缀与原子选项 |
| [Job](Jobs.md) | 查询驱动范围、批次/单范围权限、同步/异步工作 | 分阶段登记、epoch领取、摘要合并、工作与系统完成 |
| [完整存档](Serialization.md) | 版本5容器、codec、主值、身份、引用 | 最终数组写入、检查点模式、派生恢复与文件提交 |
| [等价适配与判定](Adaptation.md) | E*的独立数据、实际原生调用与费用 | 相同观察点、逐路径结论及实际能力差额 |

## 实现入口

实现按[Implementation](Implementation.md)的八个批次推进；各批次生成对应闭合类型代码，完成对应公开行为与性能验收。各层沿[数据与依赖](Data-and-Dependencies.md)确定持有者，再按专题落实字段、局部引用和状态写入。

源码计数与D设计预算用于约束实现工作量；完整路径包括首次、容量、可选功能和用户key/codec调用。跨ECS比较统一语义、输入、配置和观察点，按[Acceptance](Acceptance.md)逐行、逐参照判定实际耗时。

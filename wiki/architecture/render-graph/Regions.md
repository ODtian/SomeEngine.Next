# Regions

## Definition

区段是持久 RenderGraph 树中的有序位置，身份类型为 `GraphRegionId`。区段含有按添加顺序
排列的 pass 和子区段。树的前序展开给出声明顺序，资源使用者先服从显式顺序关系，
其余按声明顺序排列。

默认 `GraphRegionId` 选择根区段。省略区段参数的现有 pass 添加调用继续在根区段工作。

## Editing

- `RenderGraphEdit.AddRegion(label, parent = default)` 在父区段末尾创建持久区段。
- 编辑中的 `AddRasterPass`、`AddComputePass`、`AddCopyPass`、`AddGeneralPass` 以及对应
  `Add*FramePass` 重载接受末尾的可选 `region` 参数。
- 后来加入已有区段的 pass 取得该区段内的声明序号，因此仍在该区段的位置执行。
- `Remove(GraphRegionId)` 把区段的全部后代、pass、访问与相应显式顺序关系纳入同一编辑。
- 区段与其它图身份遵守结构版本及编辑提交规则，参数检查使用 `DevelopmentCheck`。

## Declaration intervals

声明序号和区间端点使用 64 位整数。父区段的有序子项是编号范围的持有者，区段树通过
持久列表和图行存储随结构版本复制。

首个子区段使用父区段的内部空间，以支持深层嵌套；后续子项在剩余空间中分配区间并
保留空隙。空隙用尽时，只重新分配该父区段子树的编号，分配宽度按后代所需位置计算。
较小区段可利用相邻空隙扩大范围，祖先的区间边界随之更新。子树外 pass 的声明序号保持
其既有值。序号变化通过 `GraphEditChanges` 的 pass 变化交给原有增量分析与调度。

## Frame enable state

`RenderGraphFrame.SetRegionEnabled(region, enabled)` 更新区段启用状态。pass 的有效启用
状态是自身状态与全部祖先区段状态的与。禁用区段控制整棵子树，重新启用后各后代仍保留
自身的启用状态。根区段同时控制根内的持久 pass 和当帧 pass。

启用状态是帧数据：设置通过既有帧属性变化路径更新对应执行行，结构版本保持原值。
已取得帧与随后编辑发布的结构版本之间按现有帧值继承规则携带区段启用状态。

## Public analysis statistics

现有 Diagnostics 回调中的 `RenderGraphStatistics` 提供：

- `AnalyzedPassCount`：当帧变化 pass、资源使用者、依赖祖先与存活传播实际访问的 pass 去重后的数量。
- `RebuiltRowCount`：当帧资源、视图、pass 和访问行的重建次数。
- `AnalysisReused`：当帧是否复用已有分析。

无结构编辑或帧数据变化的静态帧复用分析，前两项计数为零。局部区段重编只带来变化
子树及其共享资源使用者的分析。

## Behavior evidence

`tests/SomeEngine.RenderGraph.Tests/PersistentRegionTests.cs` 通过 WARP 和自给的 16 字节
复制、清除与回读工作覆盖迟加入写者、嵌套顺序、子树删除、丢弃编辑、启用恢复、结构版本
边界、反复增长和公开统计。读出的缓冲内容及公开帧 API 是验收边界。

## Relations

[[Retained-Graph]] · [[Structural-Changes]] · [[Frame-Data]] · [[Graph-Versioning]] · [[Incremental-Scheduling]]

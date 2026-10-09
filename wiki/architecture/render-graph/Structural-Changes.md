# Structural Changes

## Definition
结构包括有序区段树、pass、资源声明、资源访问、排序关系、shader 接口形状和真正改变图拓扑的配置。

## Invariants
- 增加、删除、替换和修改使用显式 CRUD。
- 编辑调用本身就是 change information，不通过比较两张完整描述来猜变化。
- commit 只影响未来取得的新图版本。

## Region edits

`RenderGraphEdit.AddRegion` 在指定父区段末尾添加持久区段。编辑保存区段、pass 和子项的
顺序，区段树与资源声明一起随结构版本复制。区段的编号范围由父区段的有序子项持有。

`Remove(GraphRegionId)` 在同一次编辑中递归移除子区段、pass、资源访问和涉及这些 pass
的显式顺序关系。`Commit` 发布新的结构版本，已取得的帧继续使用其原有区段树；丢弃编辑
保留已提交结构。

编号空隙耗尽时，编辑重新分配受影响父区段子树的编号。区段可利用相邻空隙扩展范围，
序号发生变化的 pass 通过原有 pass 变化记录交给增量分析与调度。编号与启用的完整规则
见 [[Regions]]。

## Local update methods

显式编辑维护整棵持久树中的区段、资源和顺序关系。在一个持久父区段内部，
[[Region-Reconciliation]] 用业务键决定哪些子区段需要添加、删除或替换，并把这些变化
表达为同一个 `RenderGraphEdit` 中的普通编辑。值相同的键继续使用原区段身份。
调用方在提交成功后替换映射，编辑丢弃后继续使用原映射。

接近每帧刷新的工作使用 [[Extension-Points|Frame Regions]]，安装时保存其位置，
帧内重建该位置的内容。持久树的全局修改、业务键的局部调和与当帧重建因此各有明确范围，
共同使用既有资源访问和增量分析路径。

## Non-goals
camera、材质值、instance 值、external resource 绑定、动态 range 和区段启用状态由帧接口更新。

## Relations
[[Retained-Graph]] · [[Regions]] · [[Frame-Data]] · [[Incremental-Reconciliation]] · [[Region-Reconciliation]] · [[Extension-Points|Frame Regions]]

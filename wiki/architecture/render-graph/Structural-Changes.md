# Structural Changes

## Definition
结构包括 pass、资源声明、资源访问、排序关系、shader 接口形状和真正改变图拓扑的配置。

## Invariants
- 增加、删除、替换和修改使用显式 CRUD。
- 编辑调用本身就是 change information，不通过比较两张完整描述来猜变化。
- commit 只影响未来取得的新图版本。

## Non-goals
camera、材质值、instance 值、external resource 绑定和动态 range 不属于结构变化。

## Relations
[[Retained-Graph]] · [[Frame-Data]] · [[Incremental-Reconciliation]]
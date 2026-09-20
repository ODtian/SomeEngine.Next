# Geometry Invocation Contract

## Definition
Geometry implementation 可能被调用零次、一次或多次，调用顺序可因 multi-view、cache、不同 raster 路径或调度变化而改变。

## Contract
实现应只依赖明确输入；若依赖隐藏调用次数、隐式可变状态或未声明状态，其结果由实现者负责。

## Invariants
- 不建立 shader parser、purity type system、writable-resource analyzer、随机/时间状态分析、调用次数证明或重复调用验证器。
- 真实 shader resources 仍必须进入 RenderGraph，因为那是同步和生命周期事实。
- 真正需要显式顺序的写操作应建成独立 graph pass，再把输出作为后续输入。
- writable resource 的存在本身不触发额外检查或拒绝；本页是用户 shader 的行为契约，不是要由框架证明的性质。

## Relations
[[Shader-Resource-Effects]] · [[architecture/render-graph/Shader-Resource-Closure]]
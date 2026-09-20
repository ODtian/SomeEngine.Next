# Bindless Resource Recipe

## Definition
MaterialBindless 对具体 Material 类型需要读取哪些资源 slot、如何解释这些 slot，在 Material 类型或对应 shader/pipeline 建立阶段预先确定；每次使用 Material 时只执行已经确定的读取。

## Invariants
- 热路径不遍历所有 Material slots 再根据运行时类型判断资源种类。
- 预处理结果同时确定每个参与资源 slot 对应的紧凑 `valueOffset`；shader 使用这一常量而不是 Material slot ordinal 索引 values 区间。
- recipe 只属于 MaterialBindless，不改变 Material core 的 slot 语义。
- 第三方等价扩展可以采用完全不同的预处理和数据组织方式。
- recipe 的存在不产生新的全引擎 Material 数据格式。

## Relations
[[MaterialBindless]] · [[Material-Slots]]

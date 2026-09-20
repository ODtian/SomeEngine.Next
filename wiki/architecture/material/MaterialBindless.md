# MaterialBindless

## Definition
MaterialBindless 是可选的 per-material bindless value extension。Material core 完全不知道它。

## Data model
每个使用该扩展的具体 Material 获得一段变长 uint 区间；base 只属于这个扩展。扩展在 Material/shader binding 建立阶段把参与资源的 Material slot 映射为自己的紧凑 `valueOffset`，shader 使用 `values[base + valueOffset]` 取得真实 descriptor index。

## Invariants
- 不假设所有 Material 同构。
- descriptor allocation 不要求连续。
- Texture/Sampler/Buffer/AccelerationStructure 按各自 descriptor kind 处理。
- 普通 float/vector 不属于该扩展职责。
- `valueOffset` 只属于 MaterialBindless；它不是 Material slot、Material ID、Cluster material ID 或全引擎材质序号。
- Material slot 到 `valueOffset` 的映射在进入 render hot path 前确定；热路径不扫描全部 slot 重新压缩。
- CPU mirror 与 dirty ranges 只上传变化部分。
- 整个产品可以完全不引用此扩展而不产生其运行时代价。
- 第三方等价扩展不需要实现由本扩展定义的父接口。

## Relations
[[Material-Slots]] · [[Bindless-Resource-Recipe]] · [[Bindless-Resource-Dependency]] · [[Bindless-Descriptor-Lifetime]] · [[architecture/assets/Texture-View-Cache]]

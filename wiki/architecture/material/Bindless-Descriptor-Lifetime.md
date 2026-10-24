# Bindless Descriptor Lifetime

## Definition
MaterialBindless 只管理自己写入的 descriptor allocation、uint values、CPU mirror、dirty ranges 以及这些状态的回收；设备级 descriptor 发布属于 Graphics 公共阶段。

## Invariants
- values 只上传发生变化的 uint ranges。
- descriptor slot 可以保持时优先重写原 slot，不要求重新分配连续 descriptor。
- descriptor、values 区间和它们引用的旧设备资源只能在相关 queue completions 全部结束后回收。
- MaterialBindless 不拥有 Graphics 的全局 descriptor publish 时机。
- 其它 bindless 扩展可以独立写 descriptor，并共享同一个 Graphics publish 阶段。

## Relations
[[MaterialBindless]] · [[Bindless-Resource-Dependency]] · [[architecture/render-graph/Range-Completions]]

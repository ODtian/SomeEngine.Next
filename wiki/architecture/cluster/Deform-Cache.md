# Deform Cache

## Definition
每个 visible cluster 对应一段可变长度 opaque byte region；具体 Deform implementation 决定所需 bytes、alignment、写入与读取方式。

## Invariants
- Cluster 不解释 cache payload 字段或压缩方式。
- cache region 不要求与 vertex 数或 vertex layout 等长。
- overflow 不允许切换成 inline re-evaluate 等 correctness recovery。
- allocator 机制不属于本页语义。

## Relations
[[Cluster-Deform-Compatibility]] · [[Deform-Cache-Allocation]] · [[Overflow]]
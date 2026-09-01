# Delayed Feedback

## Definition
设备到 host 的 streaming、统计、overflow 与 diagnostics 信息可以延迟读取，但不得形成同一 frame 的设备→host→设备依赖环。

## Invariants
- Frame N 的 GPU feedback 只影响 N+k。
- readback 不触发同帧重跑。
- page fault、high-water 和统计都遵循此规则。

## Relations
[[architecture/cluster/Overflow]] · [[architecture/cluster-streaming/Page-Faults]]
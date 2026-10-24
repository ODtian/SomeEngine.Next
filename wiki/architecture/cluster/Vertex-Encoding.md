# Vertex Encoding

## Definition
Vertex encoding 是可替换的 asset-build 与 shader decode 实现，不属于 Cluster ABI。

## Standard implementation
内置 Standard encoder 可以继续使用 cluster-local position quantization、variable bit width、normal/tangent compression、variable UV precision 与 bitstream attributes。

## Invariants
- opaque 不意味着固定 stride AoS。
- cook 输出记录 encoding compatibility；对应 Vertex implementation 在 build/pipeline creation 前匹配。
- 不匹配不进入 draw hot path 再验证。

## Relations
[[Opaque-Vertex-Data]]
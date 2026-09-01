# Shader Composition

## Definition
用户实现抽象 stage 接口，再由 pipeline template 在 shader build/link 阶段组成最终 entry。

## Invariants
- pipeline template 消费已经满足接口的用户实现，不重新解释 Material 业务语义。
- draw 时不搜索 shader、不做字符串 entry lookup、不动态发现实现。
- Surface、Vertex、Tessellation 等用户接口不绑定 Cluster、forward 或 deferred。

## Relations
[[User-Stage-Interfaces]] · [[Shader-Build]]
# Deform Cache Allocation

## Definition
Deform cache 的具体分配算法由 GPU benchmark 选择，不固定成架构信条。

## Candidates
- per-cluster CAS
- grouped reservation：一个 threadgroup 处理 K 个 cluster，group 内 prefix/alignment，整组一次全局 reserve
- size pass + device-wide prefix + write

## Required semantics
- fixed total capacity
- alignment correct
- no out-of-bounds write
- overflow does not change algorithm route

## Decision rule
至少在目标 D3D12/Vulkan GPU 上覆盖从低到高 visible-cluster 数量，比较 GPU duration、contention、occupancy、memory traffic 与 dispatch 数；确定赢家后删除其它生产实现。
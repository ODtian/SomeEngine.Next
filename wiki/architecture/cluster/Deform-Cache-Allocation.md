# Deform Cache Allocation

## Definition
Deform cache 的具体分配算法由 GPU benchmark 选择，不固定成架构信条。

当前生产路径复用既有的 size → global prefix → write 分配器。每个计算组先扫描 256 个请求，随后对组总量扫描并叠加帧内已使用容量，最后生成对齐后的字节偏移。用户写入阶段每组处理一个可见 cluster。实现入口为 [`cluster_deform.slang`](../../../assets/Shaders/cluster_deform.slang) 与 [`ClusterPipelineSystem.Deformation`](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Deformation.cs)。

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

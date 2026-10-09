# Vertex Reuse

Vertex reuse batch 只覆盖一个 material range，并对 triangle 数和 unique vertex 数设置明确上限，使一个 wave 内可直接共享 vertex evaluation result。

具体 batch 尺寸和打包允许通过 benchmark 改进；语义上不跨 material range，也不要求 hardware raster 使用同一机制。

着色、硬件光栅和细分的同一次 evaluator 调用按 `visibleClusterIndex`、`localVertexIndex` 与 `IsPreviousFrame`
匹配 wave 内的 lanes；混合多个 cluster 的 wave 也能分别复用各自的完整顶点结果。
当前帧和前一帧的顶点评估分别调用，各自匹配。用户的 `IVertexEvaluate.readLane` 运输完整 `Vertex`，
管线继续使用接口提供的位置与三角形数据。

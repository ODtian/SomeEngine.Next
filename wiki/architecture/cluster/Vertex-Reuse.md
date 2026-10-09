# Vertex Reuse

Vertex reuse batch 只覆盖一个 material range，并对 triangle 数和 unique vertex 数设置明确上限，使一个 wave 内可直接共享 vertex evaluation result。

具体 batch 尺寸和打包允许通过 benchmark 改进；语义上不跨 material range，也不要求 hardware raster 使用同一机制。

着色、硬件光栅和细分的同一次 evaluator 调用按 `visibleClusterIndex`、`localVertexIndex` 与 `IsPreviousFrame`
匹配 wave 内的 lanes；混合多个 cluster 的 wave 也能分别复用各自的完整顶点结果。
当前帧和前一帧的顶点评估分别调用，各自匹配。用户的 `IVertexEvaluate.readLane` 运输完整 `Vertex`，
管线继续使用接口提供的位置与三角形数据。

接收者与运动求值按 `visibleClusterIndex`、`triangleID` 和当前/前帧匹配 wave 内的三角形。
每组由一个 lane 调用用户顶点评估和 `IDomainEvaluate.makeTriangle`；用户域的 `readLane`
运输完整 `TriangleData`。管线从域提供的三个角点构造自己的投影数据，按同一三角形匹配共享
屏幕原点、边、逆面积及逆 W。各像素继续生成中心与梯度并调用用户 `reconstruct`。

管线先按完整 visibility 值与 view 匹配 wave 内 lanes，leader 解码一次页头、cluster 拓扑、材质映射和
三个顶点索引，并运输管线自己的 `TriangleInfo`。当前三角形和投影复用这个 leader；运动求值按实际
active lanes 与当前/前帧键选择 leader，覆盖用户 `reconstruct` 提前结束部分像素的情形。

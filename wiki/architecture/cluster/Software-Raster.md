# Software Raster

Software raster 以 material range 内的 vertex-reuse batch 为工作单位，一个 batch 对应一个 wave 可处理的 bounded triangle/unique-vertex 集合。

它使用 wave shuffle/ballot/read-lane 交换已经计算的 vertex result，不依赖额外全局 transformed-vertex cache。

[`ClusterMaterialStages.ResolveInvocation`](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs) 在材质同时提供常规软件入口与 software meshlet 入口时选择常规入口，每个工作组处理一份 vertex-reuse batch。仅提供 software meshlet 入口的材质继续按有界材质区段求值顶点，再逐批处理三角形；用户 varying 由用户的 `readLane` 交付。

参见 [[Vertex-Reuse]] · [[Opaque-Vertex-Data]]。

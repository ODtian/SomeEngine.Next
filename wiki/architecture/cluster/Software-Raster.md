# Software Raster

Software raster 以 material range 内的 vertex-reuse batch 为工作单位，一个 batch 对应一个 wave 可处理的 bounded triangle/unique-vertex 集合。

它使用 wave shuffle/ballot/read-lane 交换已经计算的 vertex result，不依赖额外全局 transformed-vertex cache。

参见 [[Vertex-Reuse]] · [[Opaque-Vertex-Data]]。
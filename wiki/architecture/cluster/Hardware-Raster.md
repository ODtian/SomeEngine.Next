# Hardware Raster

Hardware raster 不走 software vertex-reuse batch，也不是传统 indexed draw。

默认机制是 bounded grouped work + indirect instance draw；vertex shader 根据 cluster/primitive/local index 取得 opaque vertex blob，并调用对应 Vertex implementation 解码。

参见 [[Raster-Execution]] · [[Opaque-Vertex-Data]]。
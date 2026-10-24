# Vertex Reuse

Vertex reuse batch 只覆盖一个 material range，并对 triangle 数和 unique vertex 数设置明确上限，使一个 wave 内可直接共享 vertex evaluation result。

具体 batch 尺寸和打包允许通过 benchmark 改进；语义上不跨 material range，也不要求 hardware raster 使用同一机制。
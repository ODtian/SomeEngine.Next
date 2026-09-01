# Texture Device Realization

## Definition
长期 Texture 在某个 RenderDevice 上对应一份私有 Graphics.Texture 与相关视图状态。

## Invariants
- 映射维度是 Texture identity × Graphics.Device。
- 通用 realization 不属于 MaterialBindless 或任何可选材质扩展。
- 创建 backing、上传 Bootstrap、上传后续 mip、重建 backing 和旧资源回收都是明确步骤。
- render hot path 不同步等待资产 I/O。
- format/dimension 改变时先建立新 backing，再切换使用，旧 backing 等相关 queue completions 结束后释放。

## Relations
[[architecture/rendering/RenderDevice]] · [[Texture-View-Cache]] · [[architecture/render-graph/Range-Completions]]
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

纹理 tile 的就绪像素页在重排期间保持引用，按后端复制布局直接写入 RenderGraph
当帧映射上传区。每个 tile 写入完成后释放 CPU 像素页引用，帧持有上传区至 GPU
完成。行间填充和末尾填充在写入时清零；tile 复制和提交后的纹理状态继续由
原上传批次及设备资源状态维护。

# Texture Bootstrap

## Definition
Texture Bootstrap 是足以正常采样的最粗 mip tail；它在 Texture 对外可用前已加载并保持可用。

## Invariants
- array/cube/depth Texture 的 Bootstrap 覆盖最低可用表示所需的全部 subresources。
- RenderDevice 首次看到 Texture 时不需要再等待资产 I/O。
- 更细 mip 只通过异步上传扩大可采样范围。
- missing Texture 只处理真正缺失或损坏，不承担正常 streaming 首用。

## Relations
[[Bootstrap-Streaming]] · [[Texture-Device-Realization]]
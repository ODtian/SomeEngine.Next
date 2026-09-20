# Texture

## Definition
Texture 是长期、与 Graphics Device 无关的引擎资产对象。

## Contains
- dimension
- width / height / depth
- mip count
- array layer count
- format 与默认 sampled format/dimension
- revision
- streaming 所需的 chunk/mip 元数据

## Invariants
- Material 保存 Texture，不保存 Graphics.Texture 或 SRV/UAV。
- Texture 不拥有 device-specific backing。
- 不为设备状态再建立第二个公开 Texture 身份。

## Relations
[[Texture-Bootstrap]] · [[Texture-Device-Realization]] · [[architecture/rendering/RenderDevice]]
# Texture View Cache

## Definition
RenderDevice 按真实 Graphics view description 缓存 Texture 的 SRV/UAV。

## Key facts
- format
- dimension
- mip range
- array range
- aspect
- SRV/UAV kind

## Invariants
- 不把默认 full-range SRV 当成唯一 Texture 视图。
- 可选 binding 扩展请求什么 view，就取得相应真实 view。
- backing 重建后旧 views 与旧 backing 一起等待 GPU 使用结束再释放。

## Relations
[[Texture-Device-Realization]] · [[architecture/material/MaterialBindless]]
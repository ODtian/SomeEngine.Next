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
- TextureVersions.Allocation 随分配更换和关联退出推进，经现有提取进入 Rendering world。Cluster 的资源刷新据此重取 RHI Texture，帧绑定从当前设备记录重取 SRV/UAV，并显式更新持久图的资源关联。

## Relations
[[Texture-Device-Realization]] · [[architecture/material/MaterialBindless]]

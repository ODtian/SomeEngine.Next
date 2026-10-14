# Extraction

## Definition
Extraction 把 GameWorld 的渲染相关变化转换成下一次 RenderWorld 更新。

## Invariants
- extraction 完成后，当前 render prepare 不再回读 GameWorld。
- 更新只在明确 RenderWorld 边界应用。
- 资产引用使用长期对象，不把 loader 状态传播到 RenderWorld。

## 当前网格入口

`MeshRenderExtractor` 从 GameWorld 的 `MeshInstance`、`WorldTransform` 和材质绑定缓冲提取 `RenderMesh`、`RenderTransform` 与材质数据。实例化资源句柄提取通路已于 2026-10-04 删除，见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

Mesh 的当前 Revision 与 Rendering world 已取得的 Mesh 数据版本比较；新页面和整份网格替换通过这个版本进入下一次提取。Material 保持实际对象引用，`RenderMaterialReference` 同时保存该次提取的材料 Revision，`ReadMaterialRevision` 读取已应用版本。Cluster 的材料消费使用这个提取版本推进标量与绑定变化。

同一次 Game world 读取还从材质绑定和光源 cookie 收集实际 Texture 的 `TextureVersions`。Rendering world 集中保存该次应用的纹理版本，通过 `ReadTextureVersions` 提供给渲染消费方。内容、补充像素和设备替换内容分别由版本字段表达；Cluster 管线据此更新实际纹理分配与图引用。

2026-10-14 行为检查覆盖材料版本应用边界、纹理共享引用与版本应用、网格新页面取得；实际 RenderDevice 检查覆盖补充 mip 的分配复用和两设备整份替换完成。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

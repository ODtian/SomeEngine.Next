# Extraction

## Definition
Extraction 把 GameWorld 的渲染相关变化转换成下一次 RenderWorld 更新。

## Invariants
- extraction 完成后，当前 render prepare 不再回读 GameWorld。
- 更新只在明确 RenderWorld 边界应用。
- 资产引用使用长期对象，不把 loader 状态传播到 RenderWorld。

## 当前网格入口

`MeshRenderExtractor` 从 GameWorld 的 `MeshInstance`、`WorldTransform` 和材质绑定缓冲提取 `RenderMesh`、`RenderTransform` 与材质数据。实例化资源句柄提取通路已于 2026-10-04 删除，见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]
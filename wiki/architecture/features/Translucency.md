# Translucency

透明、有序颜色与 stencil、X-ray 由 [Layer Composition](../rendering/Layer-Composition.md) 统一处理。材质通过 `Material.Layer` 选择已注册的 pass、作者次序和合成操作；覆盖与深度由用户 Visibility 解释，面选择由烘焙的 `RasterFaces` 声明。

层可见性记录原始三角形身份与完整深度，着色复用不透明的材质分类和同一份 `evaluateSurface`，写入片元载荷。tile 内按像素对全部记录排序并合成，容量反馈在准备阶段通过显式图编辑采用。

材质贴花由独立的 `MaterialDecalFeature` 维护体积与分箱，接收材质调用贴花库并解释用户数据。它与层合成分别维护各自网格，沿同一 Rendering world 和共享 GPU 灯光数据工作。

现行接口、帧顺序、存储和行为检查以 Layer Composition 页为入口。旧候选索引的发布与性能记录保存在 [Cluster 多覆盖混合](../rendering/Cluster-Blend-Integration.md)。

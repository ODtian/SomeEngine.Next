# Extraction

## Definition
Extraction 把 GameWorld 的渲染相关变化转换成下一次 RenderWorld 更新。

## Invariants
- extraction 完成后，当前 render prepare 不再回读 GameWorld。
- 更新只在明确 RenderWorld 边界应用。
- 资产引用使用长期对象，不把 loader 状态传播到 RenderWorld。

## 当前网格入口

`MeshRenderExtractor` 从 GameWorld 的 `MeshInstance`、`WorldTransform` 和材质绑定缓冲提取 `RenderMesh`、`RenderTransform` 与材质数据。实例化资源句柄提取通路已于 2026-10-04 删除，见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## 同一读取窗口内的补充收集

`IRenderExtractionSystem.CompleteSourceCollection(World sourceWorld, bool fullCollection)`
在 chunk 收集完成后、`World.ExecuteReadSnapshot` 回调结束前执行。
提取系统可在这一窗口中使用公开的只读 World 与层级 API，完成有序子节点等数据的收集。
来源组件在 `DeclareReads` 中声明，收集系统按对应写版本筛选变化。
回调返回的变化参与同一次 RenderPrepare 更新；默认实现返回 false。

该回调与 chunk 收集共用 Game world 的一致读取窗口。
`Apply` 与 `ApplyChanges` 使用收集值，RenderWorld 的常驻组件和缓冲在现有结构事务中更新。

## 材质引用

`MeshMaterialBinding.Material` 中已经加载的对象通过 `RenderMaterialReference`
进入 Rendering world。`RenderMaterialBinding.MaterialId` 保存实例绑定，顺序与重复绑定保持原样。
`RenderWorld` 按 MaterialId 维护共享引用与绑定计数，`ReadMaterial` 取得同一材料对象；
最后一个绑定移除时释放该共享引用。源码：[RenderWorld](../../../src/SomeEngine.Render/Systems/RenderWorld.cs)。

Material 的字段和资源引用继续由原对象持有，并遵守既有资产更新边界；
渲染消费者直接读取同一对象。提取交付的是实体的绑定关系，
不生成另一份材质字段或资源值。

## 资源变化收集

内置资源收集在 Game world 的同一次读取窗口中，按实体表变化、材质绑定缓冲行版本
和 `LightCookie` 行版本更新来源关系。来源关系保存实际材质与 cookie 纹理的引用计数；
每次提取遍历这些共享资源，读取材料 Revision、纹理属性与 TextureVersions。
用户提取器继续通过 `CollectResources` 在该读取窗口补充资源。
网格范围扩张从同一来源关系读取材料，提取侧的来源材质引用由该索引集中持有。
源码：[RenderSourceResources](../../../src/SomeEngine.Render/Systems/RenderExtractionResources.cs)、
[提取顺序](../../../src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs)。

网格资产更新在 `CompleteCollection` 中通过一次 Rendering world 查询读取
`RenderSource` 和 `RenderMesh` 列，比较来源网格 Revision 与材质范围扩张。
比较结果与实体组件变化进入同一候选；RenderPrepare 成功应用后接受系统版本与实体表变化。
应用失败时继续从上次成功版本收集受影响来源。
源码：[MeshRenderExtractor](../../../src/SomeEngine.Render/Systems/MeshRenderExtractor.cs)。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

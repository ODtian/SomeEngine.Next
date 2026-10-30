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
相同材质引用顺序与重复项的绑定共用一份来源材质关系，实体引用与材质引用分别由该关系维护。
每个共享材质的 `BoundsExpansion` 在本轮资源收集中读取一次，材质关系随后计算本轮的最大范围扩张。
用户提取器继续通过 `CollectResources` 在该读取窗口补充资源。
收集器由来源关系索引复用，每轮分别记录已收集的材料与纹理；同一纹理在该轮首次出现时读取版本。
材料或纹理版本变化时更新各自的不可变表构建器，全部提取器收集完成后移除本轮消失的成员。
成员与版本相同的提取继续使用同一张不可变表。
网格范围扩张从同一来源关系读取本轮计算值，提取侧的来源材质引用由该索引集中持有。
`RenderWorld.ReadTextureVersions()` 一次读取已应用的不可变纹理版本表，供管线按整批变化消费；
持有的旧表继续表达原来的版本值。单纹理重载沿用同一张表。
共享材质版本比较通过一次 Rendering world 查询读取全部 `RenderMaterialReference`，
随后在查询内与本次收集值比较。
源码：[RenderSourceResources](../../../src/SomeEngine.Render/Systems/RenderExtractionResources.cs)、
[提取顺序](../../../src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs)。

网格来源按 Mesh、实例范围扩张和来源材质关系分组，来源成员随组件与实体表变化更新。
`CompleteCollection` 通过一次 Rendering world 查询顺序读取 `RenderSource` 和 `RenderMesh` 列，
在每组首次出现的本轮未收集来源上比较已应用网格 Revision 与本轮范围扩张；全部组观察完成后结束查询。
每个共享 Mesh 的 Revision 和所需内容在该轮首次使用时取得，变化组再展开其余来源的网格更新。
本轮已收集的组件值单独交付，其余组成员共享上次成功应用的网格版本和范围。
资源比较结果与实体组件变化进入同一候选；RenderPrepare 成功应用后接受系统版本与实体表变化。
应用失败时继续从上次成功版本收集受影响来源。
源码：[MeshRenderExtractor](../../../src/SomeEngine.Render/Systems/MeshRenderExtractor.cs)。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

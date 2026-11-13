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


切换 GameWorld 时，`RenderExtractionSystems.ReplaceSource()` 释放旧世界的查询与表变化观察器。新世界的首次提取形成完整候选，来源变化集同时包含旧镜像来源和新世界的实体变化；网格提取器据此解除旧网格与材质订阅。下一次准备应用候选前移除旧的镜像实体，再为新来源建立实体与材质引用。渲染器自有的视图和功能实体继续存留。

材质纹理版本从当前活跃材质引用读取。旧图的参数输入随 pass 更新移除，对应纹理订阅在图编辑提交时清理。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

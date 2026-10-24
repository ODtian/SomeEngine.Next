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
`Apply` 与 `ApplyChanges` 使用收集值。首次收集和结构变化在 RenderWorld 的现有结构事务中更新；
已有实体的值更新通过 `CanApplyChangesDirect` 及已声明写入组件的 hook 条件选择直接写入路径。

`SkinRenderExtractor` 在骨架 section、网格绑定及目标映射保持一致时，
使用一次 `World.ExecuteComponentWrite<RenderBoneSectionData>` 交换各 section 的捕获数组并写入组件。
CPU 姿势、计算记录、GPU 控制值和 IK 目标共用 `SourceSection.SwapCapture`；
新增、移除和重新绑定沿 `Publish` 更新实体关系及绑定。
实现入口为 `src/SomeEngine.Render.Features/Skinning/MeshSkin.cs`，
直接写入的共同调度入口为 `src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs`。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

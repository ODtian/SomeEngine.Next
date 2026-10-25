# Extraction

## Definition
Extraction 把 GameWorld 的渲染相关变化转换成下一次 RenderWorld 更新。

## Invariants
- extraction 完成后，当前 render prepare 不再回读 GameWorld。
- 更新只在明确 RenderWorld 边界应用。
- 资产引用使用长期对象，不把 loader 状态传播到 RenderWorld。

## 当前网格入口

`MeshRenderExtractor` 从 GameWorld 的 `MeshInstance`、`WorldTransform` 和材质绑定缓冲提取 `RenderMesh`、`RenderTransform` 与材质数据。实例化资源句柄提取通路已于 2026-10-04 删除，见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

来源网格持有者同时保存已发布的 mesh revision 和合成后的边界扩展量。后续收集比较来源
资产与材质的当前值，实际变化时重新捕获 `Mesh` 并更新 Rendering world 的网格值。
材质资源收集复用同一网格提取器持有的来源绑定；绑定变化时更新引用，来源成员变化时
在同一读取窗口中移除对应绑定。各帧继续从当前材质读取内容版本及纹理引用，
纹理自己的内容／分配版本沿原资源集合发布。

## 只读范围收集与汇合

`IRenderExtractionRangeSystem` 是 `IRenderExtractionSystem` 的可选扩展。
`CreateRangeCollector()` 为每个查询范围提供独立的 `IRenderExtractionCollector`，
其 `Collect(ReadOnlyQueryPacket, uint lastSystemVersion, bool fullCollection)` 读取 ECS 原始列，
保存本轮输出；同步范围工作完成后，协调线程按原查询与系统顺序调用
`MergeRangeCollector`，合并实体、材质、灯光、变换及 UI 变化集合。
现有自定义 `IRenderExtractionSystem` 的 `Collect` / `CollectChanges` 在同一读取窗口中由协调线程调用。

内置提取器的旧 chunk 入口通过 `ReadOnlyQueryPacketJobs.Borrow(QueryChunkView)`
进入相同收集算法。查询回调保持来源列有效，`ReadOnlyQueryPacketPlan.ExecuteParallel`
在返回前完成全部范围工作；汇合与 `CompleteSourceCollection` 继续处于外层
`World.ExecuteReadSnapshot` 的读取期内。绑定 `WorldJobs` 的来源使用对应 Scheduler，
普通 World 来源使用共同 Job Scheduler。

范围输出由各收集器保持到汇合结束，随后 Reset 供后续提取复用。
来源 mesh / material 缓存、Skin 骨架 section 与目标映射、Terrain 请求缓存由原提取器持有；
镜像、资源引用与合并后的变换集合由原 `RenderExtractionContext` 持有。
`CollectResources`、`CompleteCollection` 与 RenderWorld 的 Apply 边界接续这些已汇合值。
实现入口为 `src/SomeEngine.Render/Systems/RenderExtractionRanges.cs`。

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
来源成员由首次收集和表结构变化维护；普通帧按组件列版本读取网格绑定和 GPU 目标值，
骨架绑定版本与当前姿势继续在同一补充收集窗口中比较。
CPU 姿势、计算记录、GPU 控制值和 IK 目标共用 `SourceSection.SwapCapture`；
该方法在 section 自身的数据上交换数组，批量写入直接读取交换后的值。
补充收集通过来源目标持有者的只读引用读取 GPU 输入。
新增、移除和重新绑定沿 `Publish` 更新实体关系及绑定。
实现入口为 `src/SomeEngine.Render.Features/Skinning/MeshSkin.cs`，
直接写入的共同调度入口为 `src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs`。

## Relations
[[GameWorld-and-RenderWorld]] · [[Frame-CPU-Overlap]] · [[architecture/assets/Asset-Update-Boundary]]

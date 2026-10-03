# 领域文档读取规则

## 阅读顺序与证据

探索代码、整理需求规格、拆分任务票或评审变更前，读取根级 `AGENTS.md` 和 `GLOSSARY.md`。通过下表找到相关主题，随后读取对应实现、调用者和行为测试。

本规则采用用户在 2026-10-03 确定的源码筛选方式。当前行为以当前源码及可执行证据为准；已确认的需求、设计决策和规范作为变更目标。发现差异时，先标明“当前行为”与“目标行为”，可由代码明确解释的接口漂移按源码修订；涉及产品边界或重要架构取舍时向用户说明具体分歧。

文档中的类型名对应到源码只建立阅读位置。对顺序、错误传播、版本推进、事务提交、数据持有和 GPU 完成边界的断言，沿执行路径核对。引用结论时给出源码位置及核对范围。日期型验证记录保留其提交、场景和命令范围；新的验收形成当次证据。

## 当前阅读入口

| 主题 | 文档入口 | 对照源码与核对范围 |
| --- | --- | --- |
| 系统基线与查询版本 | [Change-Tracking](../../wiki/architecture/ECS/Change-Tracking.md)、[Systems](../../wiki/architecture/ECS/Systems.md) | `SystemGroup.UpdateCore`、`SystemSlot`、`ImmediateSystemDriver`、`World.Queries.cs`；每个系统的 Last/Current 版本及回调成功后的写回 |
| Job 依赖与资源访问 | ADR 0002、[Readiness-Admission](../../wiki/architecture/ECS/Readiness-Admission.md) | `Scheduler.cs`、`Scheduler.Dependency.cs`；资源保留、依赖结束后的访问激活、work release 与显式依赖错误策略 |
| 查询与借用 | [Synchronous-Execute](../../wiki/architecture/ECS/Synchronous-Execute.md) | `QueryCursor`、`QueryRow`、`QueryChunkView`、`World.Queries.cs`；当前公共签名和查询回调边界 |
| Relation/Hierarchy | ADR 0003、[Hierarchy](../../wiki/architecture/ECS/Hierarchy.md)、[Structural-Transactions](../../wiki/architecture/ECS/Structural-Transactions.md) | `HierarchyComponents.cs`、`World.StructuralTransaction.cs`、`World.cs`、对应 Owners 实现；Parent/Children 模型和候选根提交。涉及 cardinality、顺序或批处理时继续查具体路径 |
| ECS 存档 | ADR 0005、[Save-Contracts](../../wiki/architecture/ECS/Save-Contracts.md) | `PayloadFormat.cs`、`SerializationOptions.cs`、对应 codec；版本 4、RawCheckpoint/DurableSave 两种用途 |
| 持久 RenderGraph | [Render-Graph](../../wiki/architecture/Render-Graph.md) 与 `render-graph/` 原子页 | `RenderGraph.cs`、`GraphIds.cs`、`GraphEditChanges.cs`、`GraphStructureIndex.cs`、`PassOrder.cs`、帧执行状态；结构编辑、帧版本、增量数据和提交回调 |
| GameWorld/RenderWorld | [GameWorld-and-RenderWorld](../../wiki/architecture/rendering/GameWorld-and-RenderWorld.md)、[Extraction](../../wiki/architecture/rendering/Extraction.md)、[RenderDevice](../../wiki/architecture/rendering/RenderDevice.md) | `RenderExtractionSchedule.cs`、`RenderInstanceCompositionSystem.cs`、`RenderDevice.cs`；提取、实例准备及业务 GPU 完成跟踪 |
| RHI | [RHI/Index](../../wiki/architecture/RHI/Index.md) | `SomeEngine.Graphics`、具体 backend、`Graphics.Validation`；当前公共入口包含 CommandContext、RecordedCommands、QueueCompletion、固定 DescriptorTable 与 ValidationLayer。具体 backend/capability/lifetime 规则按实现核对 |
| Assets 与 Shader | [Asset-Objects](../../wiki/architecture/Assets/Asset-Objects.md)、[Asset-Byte-Cache](../../wiki/architecture/Assets/Asset-Byte-Cache.md)、[Asset-Shaders](../../wiki/architecture/Assets/Asset-Shaders.md) | `Asset.cs`、`Asset.Blocks.cs`、`AssetByteCache.cs`、`Shader.cs`、`Shader.Slang.cs`；身份、Block、缓存与直接 Slang 数据持有 |
| SomeUI | ADR 0008–0014、[SomeUI](../../wiki/architecture/SomeUI.md) 与 `someui/` 原子页、`docs/SomeUI-Requirements.md` | `SomeEngine.Views` 已实现首次构建、参数比较、读取依赖登记及按层级深度重跑，子树按 key 加出现次数匹配、重排与释放，核对 `Scope`、`ViewRebuildSystem`、`World.ComponentData.cs`、`FirstBuildTests`、`ParameterBuildTests`、`ReadDependencyTests` 与 `NodeIdentityTests`；`ChildSource`、`ContentExtensions`、`ParentScope` 与 `Group` 支持构建与调用方两种子节点来源，核对 `ChildSourceTests`；`SomeEngine.Views.Generators` 提供同名函数、调用位置身份、存储选择诊断与 Build 警告屏蔽，核对 `ViewFunctionTests`、`CompilerDiagnosticTests` 与 Views NativeAOT 冒烟；环境数据通过 `Entity.Provide` 写入、`Scope.ReadUp` 沿 `ElementTree` 读取最近组件，核对 `EnvironmentExtensions`、`EnvironmentLookup` 与 `EnvironmentTests`；`SomeEngine.Typography` 的 `Font.Load`、`Shaper.Shape`、`ShapeBuffer` 提供公开字形与行结果，`GlyphAtlas` 提供动态 SDF 页、提取区域与预烘焙保存加载，核对 `GlyphAtlasTests` 与 Text 页。后续行为按对应 Wiki 页和任务票继续实现 |
| 动画 | ADR 0014、[Animation](../../wiki/architecture/Animation.md) 与 `animation/` 原子页、`docs/Pose-Animation-Spec.md` | `SomeEngine.Animation` 的时间线、值运算、曲线、属性通道与轨道；核对启用位、阶段运行位、按偏移写版本、播放控制与实体复用。层、图与姿势动画按目标设计读取 |
| 验证与验收 | `wiki/architecture/validation/`、当前 harness 原子页 | Core.DevelopmentCheck、业务输入处理与 ValidationLayer；验收沿用根 AGENTS 的公共行为和统一渲染场景规则 |

## 已识别的文档差异

- ADR 0006 标记为 `superseded`，当前入口为 Render-Graph 及其原子页。该 ADR 和 `wiki/history/` 用于解释历史取舍。
- ADR 0001 的“渲染执行随后实现”属于早期阶段。当前执行入口为 `RenderFrameCoordinator` 与 `ClusterPipelineSystem`。
- ECS 系统观察窗口采用 `SystemSlot.LastSystemVersion` / `CurrentSystemVersion`。用户已经选择按当前系统级基线整理；Change-Tracking 的拟写内容以此为准。
- `Storable-Descriptions`、`Active-Owner-Borrow-Creation`、`Random-Lookup` 和 `Packet-Alias-Proof` 含旧的 SystemChangeCheckpoints、RefRO/RefRW、ComponentLookupHandle/View 接口或示例。当前形状从 QueryRow、QueryChunkView、QueryCursor 读取；相关 alias/检查策略按对应执行和生成器路径核对。
- `Scheduling-Heuristics` 的 dynamic-tree 切换作为优化提案。当前 hierarchy 环验证从 `Owners.Hierarchy.ValidateParent` 的父链检查读取。
- Job 文档里的 `AfterCompletion` 表达依赖完成后的顺序语义；当前具体恢复/清理入口包括 `Scheduler.ScheduleFinally`，调用样例对应当前签名。
- `Harness-Definition` 中的两个旧导航项可通过现有 `Architecture-Checks`、`Behavior-Checks`、`Harness-Performance-Evidence` 直接进入当前主题。

## 需求、规范与历史的使用范围

框架的 UI 选择固定为 SomeUI 和 ImGui：SomeUI 用于正式 UI、HUD 和未来编辑器，ImGui 用于临时调试界面。产品边界由 [ADR 0008](../../docs/adr/0008-someui-and-imgui-roles.md) 记录；SomeUI 的需求从 `docs/SomeUI-Requirements.md` 读取，关键决策见 ADR 0009–0014，设计事实由 Wiki `architecture/someui/` 原子页拥有，`docs/SomeUI-Design.md` 只做总览。ADR 0007 已被 ADR 0009 替代；2026-10-03 的候选设计保留在 `wiki/history/SomeUI/`。Engine-Goals 的 UI/编辑器目标按这一决策整理。

ADR 0004 记录未来同步/rollback 的设计边界。Shader-Block-Residency-Refactor 和 Asset-Rebuild-Principles 等长规范按目标义务读取；其中具体行为、完成状态和验收结论分别与当前代码、任务和当次验证记录对应。

`wiki/history/`、被替代的 ADR、带日期的重构/性能/验收记录以及液体原型页按其历史或原型范围使用。通过 `wiki/Home.md` 找到的其它页面按主题与源码配对；每次只引用已核对的主张。

## 维护

- 术语集中在根级 `GLOSSARY.md`；关键设计取舍形成 ADR。
- 一条设计事实维护在一个对应 Wiki 页面，其它页面使用链接。`Atomic-Wiki.md` 的页面组织规则按这一约定应用，当前行为和设计目标按本文件的证据规则区分。
- Issue、规格、任务票、代码和测试名称使用项目已确定的术语，遵循根级 AGENTS 的命名要求。
- 架构变更同步更新对应页面，记录决策原因、影响和验收依据。
- 验收覆盖行为与公共 API；渲染性能采用根级 AGENTS 规定的 DefaultGame 三场景平台和采样要求。

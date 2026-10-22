# 朝向导航网格

`HeadingNavigationMesh` 保存凸区域的可行朝向区间、平移连接、原地旋转连接，以及区域边界位置与朝向共同构成的图。它消费与普通网格相同的源表面，依据实际轮廓在各连续朝向通道内生成区域。本文维护当前算法、公共位姿查询与资产数据职责。

## 连续朝向与位置范围

构建方法沿 [SE(2) Navigation Mesh 的通道、区域与图定义](https://arxiv.org/html/2607.01454v1#S5)。`ChannelCount` 将完整朝向周期分成等宽通道；第 `i` 个通道以 `i * 2π / ChannelCount` 为中心，覆盖两侧各半个通道宽度。角度使用 `NavigationHeading`：零朝向沿 +Z，正向转向 +X。

`HeadingNavigationFootprint` 旋转角色实际的完整轮廓，并合并该通道扫掠到的单元。实现以角度样本构造保守覆盖：相邻样本间隔为 `δ`，轮廓绕原点的外接圆半径为 `r`，任意中间位置到最近样本的位移上界为 `2r sin(δ/4)`。角度细分使该上界小于 `min(MaximumSweepError, CellSize/4)`；单元相交矩形按该上界扩张。

Recast 输出的区域覆盖保留单元的完整方格，因此构建掩码还加入参考点在整格内平移的半格余量。`NavigationSurfaceTraversal.RasterizeFootprint` 的默认调用表达固定位置的实际轮廓；构建器通过其 `padding` 参数组合角度位移与参考点平移。普通网格和 Grid 的完整旋转掩码使用同一整格位置约定。

## 区域、连接与图

每个表面支撑先经过坡度、净空、台阶和策略筛选，再逐通道评价整个扫掠轮廓。可行通道集合与原始区域分类共同形成分区标签；同一标签的相连支撑通过 Recast 区域、轮廓与凸多边形阶段生成区域。轮廓简化误差取零以保留通道集合边界，详细高度继续来自源表面的相应高度层。

`Geometry` 以 `NavigationMesh` 数组保存这些凸多边形、详细高度和几何邻接。`Regions` 与 `Geometry.Polygons` 一一对应，给出共享 `Headings` 区间表以及动作和图节点的范围。每个多边形保存原始 Tile 坐标。

`Translations` 连接共享几何入口且拥有相同可行通道的区域，保存目标区域、通道、左右端点与几何 Portal 索引。`Rotations` 连接同一区域中周期相邻的可行通道，并保存带符号的转角；两个通道的连续扫掠共同覆盖整个旋转。

`HeadingNavigationGraph` 在各多边形边界的代表位置和入口位置为每个可行通道建立节点。同一区域、同一通道中的边界节点通过平移边连接；跨区域入口连接同通道节点；同一位置的相邻通道节点通过旋转边连接。`Nodes` 保存区域、通道、位置与出边范围，`Edges` 保存目标节点、动作及跨入口时的 Portal 索引。后续路径求解可直接使用该图与原始动作数据。

## 构建与查询

`HeadingNavigationMeshBuilder.Schedule` 分别接受几何 `NavigationBuildInput` 或已取得 `NavigationSurface`，并接收 Profile、Policy、设置、前置依赖与 Scheduler。按朝向组生成掩码，按块建立高度场，再按块与朝向组评价通行；各块完成分区后，共用普通网格的块间连接与完整数组合并，最后创建朝向图。返回的 `NavigationBuild<HeadingNavigationMeshBuildState>.Completion` 表达整个结果完成。

`GetAllowedHeadings(position)` 返回所在区域的连续区间；`IsPoseFeasible(position, heading)` 查询该位姿是否属于这些区间；`IsRotationFeasible(position, startHeading, headingChange)` 查询整个带符号旋转范围。`GetTranslationConnections` 和 `GetRotationConnections` 返回当前位置与朝向可用的动作。

以下是调用例子，输入几何、规格、策略和位置由业务提供：

```csharp
var build = HeadingNavigationMeshBuilder.Schedule(input, profile, policy,
    new HeadingNavigationMeshSettings { ChannelCount = 32 }, geometryReady, scheduler);
HeadingNavigationMesh mesh = build.Complete().Mesh;
ReadOnlyMemory<HeadingNavigationInterval> intervals = mesh.GetAllowedHeadings(position);
bool canTurn = mesh.IsRotationFeasible(position, heading, MathF.PI / 2);
HeadingNavigationTranslation[] translations = mesh.GetTranslationConnections(position, heading);
```

## 规格变体与保存

`HeadingNavigationMeshBlock.SetMesh` 以 `NavigationMeshSelection` 安装完成结果；选择键继续由 Profile 资产身份和构建变体名组成。Block 保存几何、详细高度、区域区间、平移动作、旋转动作和完整图节点/边。编码与解码使用现有数组 Job。

`GetBlockAsync` 取得完整数组，`Create` 或 `CreateAsync` 返回共享只读资源。资源持有自身数据引用，资产关闭后位姿和动作查询继续使用取得的数组。相关身份与目录约定见[导航规格、策略与网格资产](Assets/Asset-Navigation.md)。

实现：[资源与查询](../../src/SomeEngine.Navigation/HeadingNavigationMesh.cs)、[构建](../../src/SomeEngine.Navigation/HeadingNavigationMeshBuilder.cs)、[连续轮廓掩码](../../src/SomeEngine.Navigation/HeadingNavigationFootprint.cs)、[图生成](../../src/SomeEngine.Navigation/HeadingNavigationGraphBuilder.cs)、[资产 Block](../../src/SomeEngine.Assets/Schema/HeadingNavigationMeshBlock.cs)。公开行为入口：[HeadingNavigationMeshTests](../../tests/SomeEngine.Assets.Tests/HeadingNavigationMeshTests.cs)。

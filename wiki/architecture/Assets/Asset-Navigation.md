# 导航规格、策略与网格资产

资源资产身份、共享关系及游戏导航意图的保存入口见[导航持久存档与恢复](../Navigation-Saves.md)。

分层 Grid 变体和共享目标 Flow 的资产入口见[分层栅格与共享目标流场](../Navigation-Flows.md#资产创建)。

`NavigationLinkBlock` 保存动作端点、方向、能力、成本和参数，并创建普通 `NavigationLinkSet`。场景安装、路径选择和动作反馈入口见[导航动作连接](../Navigation-Actions.md)。

`NavigationProfileAsset`、`NavigationPolicyAsset` 和 `NavigationAsset` 分别持有规格、策略和导航表示的身份。对应 Block 返回普通 Navigation 资源，由角色、路径和应用直接共享。

## 规格与策略

`NavigationProfileBlock.SetProfile` 接收已完成的轮廓、高度、坡度、台阶、速度、加速度、角速度和能力数据，并保存可选车辆模型的最小转弯半径与倒车能力。轮廓保存为独立数据块，标量保存在元数据中。

同一 ProfileAsset 中的 `NavigationMotionBlock.SetMotionSet` 保存运动基元的起止位姿、控制、时间采样及扫掠几何，`Create/CreateAsync` 返回共享的普通 `NavigationMotionSet`。资产创建与车辆执行入口见[车辆运动基元与状态格路径](../Navigation-Vehicles.md)。

`NavigationPolicyBlock.SetPolicy` 接收区域成本、动作成本、默认区域成本和允许能力。区域与动作成本分别保存为独立数据块。创建后的 `NavigationPolicy.GetAreaCost` 与 `GetActionCost` 直接读取资源持有的数据。

策略的可选 `Crowd` 配置一并保存 Directional/Scalar 采样方式、自由与最低速度、密度阈值，以及距离、时间和拥堵不适权重，供[人流成本反馈](../Navigation-Crowds.md) 创建共享 Costs。

两个 Asset 的 `OpenAsync` 取得身份和目录；`GetBlockAsync` 显式取得 Block 的完整数据。Block 的 `Create` 或 `CreateAsync` 返回已就绪资源。

## 网格变体

`NavigationMeshSelection` 由 `AssetId<NavigationProfileAsset>` 和构建变体名组成。`NavigationMeshBlock.SetMesh(selection, mesh)` 安装对应完整结果；`Variants` 返回当前选择目录。`CreateAsync(selection)` 显式取得选中变体的详细数组；`Create(selection)` 返回此前已经取得的完整资源。

每个变体保存位置、多边形顶点索引、多边形与入口、详细高度位置及三角形、内容版本。多边形保留区域、能力要求、入口范围、详细三角形范围及所属块；入口保留目标多边形、左右端点、通行成本、能力要求和动作标识。Profile 身份进入资产引用目录。

从几何按规格生成这些变体，以及通过 ImportContext 选择 `NavigationMeshImporter`，见[按角色规格烘焙导航网格](../Navigation-Baking.md)。

`HeadingNavigationMeshBlock` 使用相同的 Profile/变体选择键，保存连续朝向区间、平移/旋转连接和自身区域图。`GetBlockAsync` 取得目录与摘要，`CreateAsync` 取得完整资源，`CreateTileSource` 返回独立保持文件的 `HeadingNavigationMeshTileSource`。每个详细块以十二组数组保存几何、朝向与图数据；构建、ASA 与分块续行见[朝向导航网格](../Heading-Navigation.md)。

`GetBlockAsync` 取得变体元数据和入口图。带区域图的变体将每个详细 Tile 编成独立六数组组，另存跨块原始 portal，完整创建时重组多边形范围与连接。元数据同时保留完整网格和独立块的 `BakedRadius`。创建与 Block 分支共享已取得的完整资源。

`CreateTileSource(selection)` 返回独立保持文件来源的 `NavigationMeshTileSource`，公开 `Graph` 与 `Directory`。`ScheduleRead(tile, scheduler)` 在 I/O 完成后经外部 fence 提交解码 Job；Job 声明实际已取得的字节数组，`Completion` 覆盖来源释放，`Complete()` 返回详细 Mesh。资产关闭后 TileSource 继续取得后续块，已交付资源由普通引用保持数组有效。场景的兴趣、驻留与请求续行见[导航流送](../Navigation-Streaming.md)。

## 共享朝向流场

`NavigationFlowAsset` 中的 `HeadingNavigationFlowBlock` 保存具体朝向表示的来源与共享目标。`HeadingNavigationFlowSource` 持有 Profile、Policy、Links 的强类型资产引用和来源版本；每个 `HeadingNavigationFlowRegionSource` 持有区域 Id、NavigationAsset 引用、`NavigationMeshSelection` 和来源版本。`SetFlow(source, flow)` 从 `flow.Input.HeadingGraph.Regions` 保存各区域端口，同时保存目标、图版本、目标版本和输入版本。

`CreateAsync(loader, scheduler)` 显式取得 `HeadingNavigationMeshBlock`、`NavigationLinkBlock`、Profile 和 Policy，调用 `HeadingNavigationFlowBuilder` 重建有向图，再运行 `ReverseDijkstraFlow` 返回完整 `NavigationFlow`。来源版本变化时，重建的图版本和输入版本一同推进。`Source`、`Goals` 和 `GetPorts(regionId)` 提供重建数据；`Create(graph, profile, scheduler)` 使用调用方已经恢复的共享资源积分保存目标，让多个角色继续引用同一 Flow、Graph、Profile 和 Costs。取得的普通资源在资产关闭后继续支持采样与移动。

实现：[Heading Flow Block](../../../src/SomeEngine.Assets/Schema/HeadingNavigationFlowBlock.cs)。行为入口：[朝向流场资产往返](../../../tests/SomeEngine.Assets.Tests/HeadingNavigationFlowAssetTests.cs)。

## 网格资产调用

下面是从资产选择网格的调用例子：

```csharp
using var profileAsset = await NavigationProfileAsset.OpenAsync(profilePath);
NavigationProfile profile = await (await profileAsset.GetBlockAsync<NavigationProfileBlock>()).CreateAsync();

using var policyAsset = await NavigationPolicyAsset.OpenAsync(policyPath);
NavigationPolicy policy = await (await policyAsset.GetBlockAsync<NavigationPolicyBlock>()).CreateAsync();

using var navigationAsset = await NavigationAsset.OpenAsync(navigationPath);
NavigationMeshBlock meshes = await navigationAsset.GetBlockAsync<NavigationMeshBlock>();
var selection = new NavigationMeshSelection(profileAsset.Reference, "main");
NavigationMesh mesh = await meshes.CreateAsync(selection);
```

实现：[Profile Block](../../../src/SomeEngine.Assets/Schema/NavigationProfileBlock.cs)、[Policy Block](../../../src/SomeEngine.Assets/Schema/NavigationPolicyBlock.cs)、[Mesh Block](../../../src/SomeEngine.Assets/Schema/NavigationMeshBlock.cs)。行为入口：[规格往返](../../../tests/SomeEngine.Assets.Tests/NavigationProfileAssetTests.cs)、[策略往返](../../../tests/SomeEngine.Assets.Tests/NavigationPolicyAssetTests.cs)、[网格选择与共享](../../../tests/SomeEngine.Assets.Tests/NavigationMeshAssetTests.cs)。

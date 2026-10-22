# 导航规格、策略与网格资产

`NavigationProfileAsset`、`NavigationPolicyAsset` 和 `NavigationAsset` 分别持有规格、策略和导航表示的身份。对应 Block 返回普通 Navigation 资源，由角色、路径和应用直接共享。

## 规格与策略

`NavigationProfileBlock.SetProfile` 接收已完成的轮廓、高度、坡度、台阶、速度、加速度、角速度和能力数据。轮廓保存为独立数据块，标量保存在元数据中。

`NavigationPolicyBlock.SetPolicy` 接收区域成本、动作成本、默认区域成本和允许能力。区域与动作成本分别保存为独立数据块。创建后的 `NavigationPolicy.GetAreaCost` 与 `GetActionCost` 直接读取资源持有的数据。

两个 Asset 的 `OpenAsync` 取得身份和目录；`GetBlockAsync` 显式取得 Block 的完整数据。Block 的 `Create` 或 `CreateAsync` 返回已就绪资源。

## 网格变体

`NavigationMeshSelection` 由 `AssetId<NavigationProfileAsset>` 和构建变体名组成。`NavigationMeshBlock.SetMesh(selection, mesh)` 安装对应完整结果；`Variants` 返回当前选择目录，`Create(selection)` 与 `CreateAsync(selection)` 取得所选网格。

每个变体保存位置、多边形顶点索引、多边形与入口、详细高度位置及三角形、内容版本。多边形保留区域、能力要求、入口范围、详细三角形范围及所属块；入口保留目标多边形、左右端点、通行成本、能力要求和动作标识。Profile 身份进入资产引用目录。

当前常驻网格 Block 在 `GetBlockAsync` 完成时取得全部变体的数组。创建与 Block 分支复用取得的只读资源。编码和解码通过 `NavigationArrayData` 提交 Job，资产关闭时释放来源与 Block 引用，已交付资源通过普通引用继续保持数组有效。

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

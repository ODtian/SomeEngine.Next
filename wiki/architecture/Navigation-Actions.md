# 导航动作连接

本页记录当前动作连接资源、搜索结果与角色交接接口。实现入口为 [NavigationLinks](../../src/SomeEngine.Navigation/NavigationLinks.cs)、[NavigationTraversal](../../src/SomeEngine.Navigation/NavigationTraversal.cs) 和 [NavigationLinkBlock](../../src/SomeEngine.Assets/Schema/NavigationLinkBlock.cs)。

## 连接与路径

`NavigationLinkSet` 保存完成的连接数组。每个 `NavigationLink` 提供连接标识、两侧 `NavigationLinkEndpoint`、动作标识、附加成本、能力要求、方向、动作参数、入口和出口到达范围及启用状态。端点由区域标识与端口标识组成；对应已放置区域图提供实际世界位置。

`NavigationScene.ScheduleLinks(links, dependency, scheduler)` 在指定依赖完成后安装这组动作连接，保留场景中的普通区域连接，并为内容变化的连接推进版本。重新构建的区域端口与启用状态共同决定搜索可用连接。规格能力、策略允许能力、区域成本和动作成本参与路线选择。

搜索结果 `NavigationPath.Traversals` 保存选中的连接、动作、入口和出口拐点、世界端点、参数与连接版本。路径同时持有对应 LinkSet，保持其普通数据有效。连接替换沿已有路径依赖机制触发修正。

## 角色交接

`NavigationComponents.AddPath` 安装 `NavigationTraversalRequest` 与 `NavigationTraversalResult`。`NavigationSystems` 将 `NavigationTraversalSystem<TSpace,TState,TMotion,TReader>` 接在路径引导之后，返回的 `NavigationStep.Traversal` 表示该阶段完成。`Guidance` 句柄包含交接处理，随后选定 Steering 写入阶段生成最终建议。

角色沿普通路径前进至动作入口。交接阶段将本次请求编号、连接、动作、入口、出口与参数写入 `NavigationTraversalRequest`，并把普通引导设为暂停状态。游戏动作消费者读取该请求，执行跳跃、攀爬、乘梯等具体动作，写实际运动数据与 `NavigationTraversalResult`。

动作消费者通过同一请求编号报告 Running、Completed 或 Failed。导航在 Completed 反馈与实际出口位置同时满足时推进动作序号和后续路径拐点；下一步恢复普通引导。动作执行期间保留当前路线与请求，完成后的实际位置用于后续搜索更新。游戏禁用目标时，请求进入 Cancelled。

`NavigationProgress` 在实际运动完成后更新。动作交接与执行期间为 Traversing，动作失败为 TraversalFailed，完成后继续按实际位置计算移动与到达状态。动作执行数据与进度由游戏动作系统持有。

## 资产与使用期

`NavigationLinkBlock.SetLinks` 把完成连接写入 NavigationAsset。`GetBlockAsync<NavigationLinkBlock>` 显式取得连接数据，`Create/CreateAsync` 返回普通 LinkSet。连接数组的编码与解码使用导航 Job 编解码入口；已创建对象随使用者引用持续有效。

以下是资产创建并安装连接的调用例子：

```csharp
using var asset = await NavigationAsset.OpenAsync(path);
NavigationLinkSet links = await (await asset.GetBlockAsync<NavigationLinkBlock>()).CreateAsync();
JobHandle applied = scene.ScheduleLinks(links, scheduler: scheduler);
```

公开行为入口为 [NavigationTraversalTests](../../tests/SomeEngine.Navigation.Tests/NavigationTraversalTests.cs) 与 [NavigationLinkAssetTests](../../tests/SomeEngine.Assets.Tests/NavigationLinkAssetTests.cs)。其中分离平台、动作编号和跨越时长是验收例子；公开端口、方向、能力、参数及反馈接口表达通用动作交接。

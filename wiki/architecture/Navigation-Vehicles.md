# 车辆运动基元与状态格路径

车辆使用 `NavigationProfile.VehicleModel` 描述最小转弯半径和倒车能力。`NavigationVehicleControl` 保存有符号速度、曲率及持续时间，`NavigationVehicleModel.Integrate` 按项目的 XZ 朝向约定积分：零朝向沿 +Z，正曲率转向 +X。搜索、引导和实际运动消费使用同一积分入口。

## 共享运动数据

`NavigationMotionSet` 保存模型、格分辨率、朝向集合及连续的基元、控制、时间采样和扫掠三角形数组。每个 `NavigationMotionPrimitive` 声明起止位姿、起止朝向索引和相应数组范围，调用方可提供自己的完成数据。

`CreateCarLattice(profile, resolution, speed)` 是一个明确的四朝向生成器：它生成直行和四分之一圆转向，支持规格允许的倒车。转弯半径向上对齐格分辨率，控制速度同时满足线速度和角速度限制，保持所选弧长。时间采样表达每个时刻的位姿与速度，扫掠数据覆盖采样轮廓及相邻轮廓的连接面。

`NavigationMotionBlock` 与 `NavigationProfileBlock` 同属 `NavigationProfileAsset`。前者保存运动模型、格分辨率和五组独立数组，后者保存规格及其可选车辆模型。Block 的 `Create/CreateAsync` 返回普通完成对象，角色与查询可在资产关闭后共享这些数组。

以下是保存与重开车辆规格的例子，形体和分辨率由业务提供：

```csharp
NavigationMotionSet motions = NavigationMotionSet.CreateCarLattice(profile, resolution, desiredSpeed);
using (var asset = new NavigationProfileAsset())
using (var profileBlock = new NavigationProfileBlock())
using (var motionBlock = new NavigationMotionBlock())
{
    profileBlock.SetProfile(profile);
    motionBlock.SetMotionSet(motions);
    asset.SetBlock(profileBlock);
    asset.SetBlock(motionBlock);
    await asset.SaveAsync(path);
}
using var reopened = await NavigationProfileAsset.OpenAsync(path);
NavigationProfile restoredProfile = await (await reopened.GetBlockAsync<NavigationProfileBlock>()).CreateAsync();
NavigationMotionSet restoredMotions = await (await reopened.GetBlockAsync<NavigationMotionBlock>()).CreateAsync();
```

## 搜索与车体扫掠

`NavigationVehicleSpace` 组合 Grid、MotionSet 和可选 Costs。Point 变体保存原始点支撑，候选控制按完整旋转轮廓查询车体通行。连续控制按行进距离和转角细分；相邻位姿的轮廓凸包加入圆弧相对弦的偏差，逐格检查支撑高度、净空、台阶、区域成本和有向通行连接。

`StateLatticeSearchState` 以格位置、支撑层及朝向索引组织离散状态，按匹配起始朝向的基元扩展，保留独占队列、最佳成本和前驱。每段控制由 VehicleSpace 进行完整扫掠检查，费用沿实际行进距离累计。距离启发下界乘以包含 BaseCost 的最小单元成本；搜索按调用方预算在 Job 中持续推进。

`NavigationPath.Poses` 保存世界位姿序列，`MotionSegments` 保存被选中的实际控制和段端点。状态格搜索的算法背景见 [Macenski 等人的 State Lattice 说明](https://arxiv.org/html/2401.13078v2#S3.SS4.SSS3)。

## 引导与实际消费

以下是公开组合的例子：

```csharp
var space = new NavigationVehicleSpace(grid, restoredMotions);
var navigation = new NavigationSystems<NavigationVehicleSpace, StateLatticeSearch,
    StateLatticeSearchState, Movement, MovementReader>(world);
navigation.AddAgent(entity, space, restoredProfile, policy, goal, desiredSpeed);

NavigationStep step = navigation.Schedule(deltaTime, expansionBudget, motionReady);
JobHandle moved = new MoveVehicle(deltaTime, restoredMotions.Model).ScheduleParallel(world, step.Steering);
JobHandle progress = navigation.ScheduleProgress(moved);
```

车辆引导使用实际位置和朝向推进控制段，把本步控制写入 `Guidance.VehicleControl` 和 `Steering.VehicleControl`。移动消费者按 `min(deltaTime, control.Duration)` 调用模型积分，并写回实际位姿与速度；进度使用实际位置和目标朝向判定到达。`Steering.Response` 同时保留其它已选择的速度响应数据，业务按自身运动模型消费对应控制。

公开行为检查见 [NavigationVehicleTests](../../tests/SomeEngine.Navigation.Tests/NavigationVehicleTests.cs) 和 [NavigationMotionAssetTests](../../tests/SomeEngine.Assets.Tests/NavigationMotionAssetTests.cs)。它们分别观察车体通过弯道时的曲率与通行范围，以及重开规格和完整运动基元后继续按角速度限制到达目标。

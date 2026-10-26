# 径向距离区间

本页维护已实现的径向区间参数、数值状态和公共用法。源码入口为 [DistanceRange](../../../src/SomeEngine.Physics/DistanceRange.cs)、[AVBD 适配](../../../src/SomeEngine.Physics/AvbdDistanceRangeMath.cs)及[共同径向数学](../../../src/SomeEngine.Physics/DistanceRangeMath.cs)。公共运动入口见 [DistanceRangeTests](../../../tests/SomeEngine.Physics.Tests/DistanceRangeTests.cs)。

`DistanceRange` 是无向关系的 payload，双方实体由受保护的 `UndirectedRelationEndpoints<DistanceRange>` 保存。`FirstAnchor`、`SecondAnchor` 分别相对 `EndpointA`、`EndpointB` 的实体原点。当前世界锚点间的标量距离采用 `[Minimum, Maximum]` 区间，单位为米；相等的上下界给出定长约束。

`Stiffness` 是区间外的材料刚度，单位 N/m；`float.PositiveInfinity` 选择理想刚性距离界。`AvbdDistanceRangeState` 在同一关系实体保存上、下界的力乘子、独立罚参数和子步起点误差。两个单边力乘子均为非负值，净径向力为上界减下界；定长情形以第一分量保存有符号力。

`AvbdDistanceRangeMath` 从共同 `PhysicsBodies` 原位读取双方状态，按质心杠杆装配径向平移和角向 Jacobian。材料 K 与罚参数分别进入 `AvbdMaterialTerms`；当前力及正几何曲率进入已有 Body 方程。全部 Body 更新后，dual 将当前力写回原区间状态。该适配同时提供活动误差与参与 Body 上的力、力矩，供共同休眠和断裂阶段使用。

自动[关节长程界](Long-Range-Constraints.md)与显式距离区间共用径向力、锚点及正曲率函数。长程界的端点和区间继续由原关节拓扑推导；显式距离区间保存项目选择的两实体、局部锚和上下界。

下面是带偏心锚的距离系绳使用例子；参与 Body 与共同预测阶段由调用方创建：

```csharp
var tether = world.CreateRelation(firstBody, secondBody, new DistanceRange
{
    FirstAnchor = Vector3.UnitY,
    SecondAnchor = -Vector3.UnitY,
    Minimum = 0,
    Maximum = 4,
    Stiffness = float.PositiveInfinity,
});
world.Add(tether.Entity, new AvbdDistanceRangeState { Penalty = 1e6f });
var query = world.Query(new QueryDefinitionBuilder()
    .Read<DistanceRange>().Read<UndirectedRelationEndpoints<DistanceRange>>()
    .ReadWrite<AvbdDistanceRangeState>());
var constraints = new AvbdGraphConstraints<DistanceRange, AvbdDistanceRangeMath>(query);
```

调用方将该具体集合传给 `SolveAvbdGraph.Create`，或与其他具体集合组合，并显式选择迭代、Body 局部迭代、乘子保留与初始误差比例。关系改接、Body 销毁与查询生命周期沿原 ECS 和 Job 完成链接续。

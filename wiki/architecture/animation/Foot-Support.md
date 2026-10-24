# Foot Support

足部支撑消费物理域的 [场景表面查询](../ECS/Scene-Surface-Queries.md)，输出动画目标数据。几何、角色移动和刚体变换继续由各自的数据提供方持有。源码入口为 [FootSupportQueries](../../../src/SomeEngine.Animation.Pose/FootSupportQueries.cs)。

## 批量接触目标

`FootSupportQueries` 绑定 `ICollisionQueries`、探测设置和最大批量容量，并复用射线、命中与状态工作区。`FootSupportSettings` 的 `Up` 是单位世界竖直方向，`Above`、`Below` 是请求位置上下的世界距离。`Sample` 批量执行从上向下的最近命中，并按输入顺序写入调用方目标范围。

`FootSupportRequest` 保存世界位置、接触类型和预测时钟时间。`FromPrediction` 消费 [Foot Motion](Foot-Motion.md) 的同一个 `FootPredictionSample`：`ToePivot` 选择脚尖，其余类型选择脚跟，再应用传入的模型到世界矩阵。调用方也可用已取得的世界轨迹直接创建请求。

`FootContactTarget` 保存原请求的时间与接触类型，`HasSupport` 表示当前查询命中。`Support` 是物理域的 `CollisionHit`，包含支撑实体、点、法线、支撑矩阵和表面版本。`LocalPoint`、`LocalNormal` 是该接触在支撑局部空间的锚点。接触类型继续表达自由、支撑、滑动与脚尖旋转的意图，姿势消费方据此选择约束。

以下是从选定预测接触产生目标的调用示例，`contact` 来自 `FootPrediction.TryGetNextContact`，`modelToWorld` 由角色变换提供：

```csharp
using var scene = Collision.Query(world, members, tree);
var feet = new FootSupportQueries(scene,
    new FootSupportSettings(Vector3.UnitY, Above: 0.8f, Below: 1.2f), capacity: 32);
FootSupportRequest[] requests = [FootSupportRequest.FromPrediction(contact, modelToWorld)];
var targets = new FootContactTarget[requests.Length];
feet.Sample(requests, targets);
```

## 移动支撑

`TryRefresh` 按保存的支撑实体取得当前变换，以局部锚点重建世界接触点和法线。法线沿矩阵逆转置变换并归一化。结果保留原预测时间、接触类型和局部锚点，同时交付当前支撑矩阵与表面版本。重建的接触行程为零。支撑实体离开查询成员后，调用方可重新探测。

场景查询接口也可由地形或导航提供方实现：批量结果统一使用 `CollisionHit`，支撑刷新提供实体当前的矩阵与表面版本。具体场景组件可沿 `ICollisionSurfaceReader<TComponent>` 接入现有 World 查询和碰撞树。

## 跨步地形包络

`FootStride` 保存世界坐标起点、预计落点和脚掌代理球半径。`SampleEnvelope` 在两端之间等距采样，每个位置沿设置的竖直方向执行一次向下球形扫掠。输出范围长度决定样本数，包含分数 0、1 两端，调用方提供至少两个样本并保持在工作区容量内。

`FootTerrainSample` 保存跨步分数、命中状态、首次接触时的球心与完整支撑命中。中间的凸起以及球体覆盖的台阶边缘会参与实际三角形求交。样本数决定沿路径间距，球半径决定每个样本的脚掌覆盖范围。调用方可以用球心的竖直分量约束摆腿，同时用命中点、法线与版本保留地形信息。空处的样本保留该分数的原跨步位置。

以下是一次跨步包络采样的调用示例，`departure` 和 `landing` 是同一条已选动作路径在世界中的离地与落地位置：

```csharp
var terrain = new FootTerrainSample[17];
feet.SampleEnvelope(new FootStride(departure, landing, Radius: 0.12f), terrain);
```

公共消费入口位于 `tests/SomeEngine.Animation.Pose.Tests/FootSupportBehaviorTests.cs`。

[Foot Locking](Foot-Locking.md) 将接触目标接到脚跟/脚尖锚点、滑动与骨盆可达补偿，并通过公开 GPU 腿部目标进入普通最终姿势。

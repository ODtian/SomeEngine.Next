# 加速度约束与 AVO 响应

`AccelerationVelocityAvoidance` 使用共同邻居、实际速度和局部边界，为 XZ 圆盘角色选择满足加速度及速度限制的目标速度。组合入口是 `NavigationAvoidanceSystem<AccelerationVelocityAvoidance, TMotion, TReader>`；其余引导、邻居、边界、移动和进度句柄沿用[共同避让链](Navigation-Avoidance.md)。

## 完成输出与运动模型

`NavigationSteering.Velocity` 保存目标速度 `w`，`Response` 保存本步实际选中的 `NavigationVelocityResponse`。AVO 使用 `AvoidanceAgent.ResponseTime` 作为正数时间常数 `δ`，Profile 提供最大加速度 `a`。目标变化位于以实际速度 `v₀` 为中心、半径 `δa` 的圆内，并与 Profile 最大速度圆相交。

`NavigationVelocityResponse.Integrate` 对整个时间间隔使用同一闭式积分：

```text
v(t) = v₀ + (1 − exp(−t / δ)) (w − v₀)
p(t) = p₀ + v₀ t + (t − δ (1 − exp(−t / δ))) (w − v₀)
```

因此初始加速度为 `(w − v₀) / δ`，后续按指数衰减。`TimeConstant = 0` 表示瞬时速度模型。移动消费和轨迹预测读取完成的 `Steering.Response`，使用相同目标和积分入口。以下是业务运动消费的例子：

```csharp
NavigationVelocityState next = steering.Response.Integrate(
    movement.Position, movement.Velocity, steering.Velocity, deltaTime);
movement.Position = next.Position;
movement.Velocity = next.Velocity;
```

圆盘避让约束作用于 XZ 运动；业务运动系统继续解释垂直引导、支撑和朝向。

## 可达速度与互惠约束

实现按有限时域轨迹构造速度变化空间中的禁入集合：邻居产生圆盘，静态有限线段产生胶囊；两者都先与可达范围相交，再取凸包及朝向安全侧的支持约束。每次构造采样 100 个时间点，圆盘和胶囊采用 32 个方向的外接支持多边形。时间离散决定有限时域数值近似精度。

角色的分担权重由 `δa × Responsibility` 决定。双方响应时间分别进入位移系数 `b(t) = t − δ (1 − exp(−t/δ))`，组合系数为 `αb₁(t) + (1−α)b₂(t)`，对应的相对可达半径也按这两个系数组合。双方使用同一支持边界与互补份额，实际采用的独立目标变化落在各自加速度圆内。共同展望时间取双方 `TimeHorizon` 的较大值。

静态边界使用角色完整份额，并扣除数据已有的烘焙半径。约束求解同时保留实际速度中心的可达圆与最大速度圆；结果随 `Response` 一起写入本角色 Steering。各角色的采样多边形、凸包和约束缓冲保存在独占工作区，按角色并行执行。

响应方程、可达目标及互惠思想依据 [van den Berg 等，ICRA 2011](https://gamma.cs.unc.edu/AVO/AVO.pdf)。上述双方独立响应系数和有限线段胶囊是本实现对共同数据接口的展开。半平面求解沿用带有 [RVO2 归属及许可](../../src/SomeEngine.Navigation/ThirdParty/RVO2/NOTICE) 的数值实现，并加入两个圆的交集限制。

公开行为检查见 [AccelerationVelocityAvoidanceTests](../../tests/SomeEngine.Navigation.Tests/AccelerationVelocityAvoidanceTests.cs)：不同加速度与响应时间的角色按公开积分方法相向经过，持续保持可达目标、逐步速度变化和配置间距；持续朝向边界的运动请求提前减速并保持身体间距。

# Future Foot Placement

本文记录选定动作序列的未来落点、摆腿包络与切换接续。源码入口为 [公开输入](../../../src/SomeEngine.Animation.Pose/FootPlacement.cs)、[逐角色预测与历史](../../../src/SomeEngine.Animation.Pose/FootPlacementSamples.cs)、[统一脚部工作区](../../../src/SomeEngine.Animation.Pose/FootLockSamples.cs) 和 [FootLockSystem](../../../src/SomeEngine.Animation.Pose/FootLockSystem.cs)。当前接触与腿部可达区间由 [Foot Locking](Foot-Locking.md) 持有。

## 输入与持有关系

`FootLockParameters.Prediction` 提供共享 `FootPlacementSettings`：足部球半径、附加净空、包络采样数，以及选择变化的接续时长。每个安装实例分别分配连续的落点、查询、包络、离地点和运动历史数组。

`FootLockInstance.Source` 的初值来自共享参数。调用方将它配置为描述实际姿势来源的 `PosePredictionRequest`，包括已选 `PosePath`、时间基准和 `Conditions`。路径支持既有 Sequence、Blend、Layer 和 Loop；接触分类、下一实际接触和精确根/脚采样复用 [FootPrediction](Foot-Motion.md) 与 [PosePredictionBatch](Pose-Prediction.md)。`ContactWindow` 覆盖调用方希望预测的接触区间。父骨架的就绪 `ModelPose` 沿相同采样接口传入。

`FootLockInstance.Movement` 保存当前角色的 `FootMovementInput`。移动阶段提供 `FootMovementTrajectory`：每个 `FootMovementPoint` 保存请求时钟下的时间、世界位置、速度和朝向。朝向零指向 +Z，正值转向 +X；位置采用带速度的 Hermite 插值，朝向沿较短角差插值。`Points` 使用至少两个按时间递增的样本并覆盖请求的未来接触；移动阶段写入这些样本，在动画更新前交付完成的轨迹。这个数据形状可以承接带时间的位置/速度/朝向预测。

输入中的可选 `MotionWarpInput` 提供同一选择的剩余根运动。来源姿势、已应用到角色的根运动与移动预测采用调用方实际播放的同一约定；例如提取根运动的播放来源以原地姿势参与预测，移动阶段应用对应根运动。

设当前角色变换为 `A`，完成的移动轨迹为 `T(t)`，当前请求时钟为 `c`，未来接触时钟为 `f`。预测先保留实际角色相对于轨迹当前样本的偏移：

```text
M(f) = A * inverse(T(c)) * T(f)
```

配置 Warp 后，以来源时间 `p(t)` 查询其累计修正 `W`：

```text
futureActor = W(p(f)) * inverse(W(p(c))) * M(f)
```

来源根和脚 G 再乘 `futureActor`，得到未来世界空间的根与脚部。移动阶段继续持有实际角色变换，脚部控制阶段持有目标与模型平移，最终骨骼沿普通提取、GPU Two-Bone 和 Cluster 路径交付。

## 接触时间与摆腿包络

每只自由脚向选定窗口查询下一次自由运动进入接触的时刻。系统在该时刻精确采样根、脚跟和脚尖，用未来移动变换生成世界探针，并将所有活动脚的落点探针一起交给实际场景射线查询。`FootLandingPrediction` 保存这次选择的采样元数据、未来根的世界 G 及 `FootContactTarget`。

一次起摆从此前修正结果取得离地点。当前来源脚部保留自身的前后运动和抬脚形状；离地偏移随摆腿进度消退，地形落点偏移随进度进入。进度由实际离地时钟和选定接触时钟归一化，时间线继续采用原动作的持续时间与播放速率。

从离地点到落点，系统按时间采样选定动作的预计算足部轨迹，逐点应用移动预测、Warping 和当前离地/落点偏移，再通过 [FootSupportQueries.SampleEnvelope](Foot-Support.md) 对这条世界路径执行向下的球扫掠。转身等动作的弯曲摆腿路径由这些实际采样点表达。令步内进度为 `u`，离地与落点之间的直线高度为 `b(u)`，连续高度曲线为：

```text
h(u) = b(u) + lift * 4*u*(1-u)
lift >= (ground(s)-b(s)) / (4*s*(1-s)) + radius + clearance
```

各内部包络样本 `s` 提供一个高度下限。球首次接触中心沿竖直减去半径得到保守表面高度；所有下限的最大值形成整段连续包络。当前来源脚部高度与此包络取最大值，保留更高的原动作。接近落地时，脚掌逐步采用来源旋转叠加地形倾斜。

骨盆提前接收落点相对来源的地形高度偏移，使用同一摆腿进度进入。当前支撑脚、预测摆腿目标和其他来源脚目标共同参与既有双腿可达高度约束。达到动作接触时刻后，预测落点的支撑局部锚点交给当前接触状态，随后按支撑的最新变换与版本更新。

未来接触、原动作形状和地形包络的概念参考 Ubisoft 的 [Fitting the World](https://media.gdcvault.com/gdc2016/Presentations/Roche_Clifford_Fitting%20the%20World.pdf)。本页的输入职责、连续高度曲线和切换历史对应当前源码实现。

## 选择变化与观测

路径、时间基准或 `Conditions` 变化时，工作区重新绑定选定采样和接触窗口，并重新查询落点。接续历史保存此前修正后的世界脚部位置、旋转、线速度、角速度与骨盆位置/速度。旧结果按原速度外推，通过端点零导数的平滑权重进入新结果；接续长度同时受配置时长和最近接触时刻约束。旋转向量运算复用 [惯性化](Inertialization.md) 的实现。

请求的来源时间映射与 `GpuPoseTarget.CalculationTime` 分别表达动作时钟和计算时钟。普通帧推进计算时钟；选择输入变化启动接续。

`FootLockInstance.Landings` 供调用方读取未来选择、根与实际支撑，`HasPrediction` 表达该脚已取得选定窗口内的未来接触。选择变化时先更新这份未来数据，当前支撑锚点继续由接触状态持有。`FootLockInstance.Contacts` 表达当前动作接触。`FootLockInstance.Costs` 分别返回本次更新的姿势采样与几何查询耗时：前者包括选择绑定、当前来源和未来根/脚采样；后者包括支撑刷新、落点射线与包络球扫掠。脚部修正、骨盆和切换计算继续计入完整应用的动画阶段耗时。

以下是接入例子，复用 [Foot Locking](Foot-Locking.md) 中的普通 GPU 来源和脚部系统安装：

```csharp
builder.LockFeet(target, new(feet, selectedRequest, surfaces,
    new FootSupportSettings(Vector3.UnitY, 2, 2),
    ContactWindow: 2, Prediction: new FootPlacementSettings()));
Entity animation = builder.Play();
var input = (Actor: actor, Source: selectedRequest,
    Movement: new FootMovementInput(completedMovement, warpInput));
world.ExecuteBufferWrite<FootLockInstance, (Entity Actor, PosePredictionRequest Source, FootMovementInput Movement)>(
    animation, ref input, static (instances, ref input) =>
    {
        instances[0].Actor = input.Actor;
        instances[0].Source = input.Source;
        instances[0].Movement = input.Movement;
    });
```

应用消费入口为 [FootPredictionRenderingTests](../../../tests/SomeEngine.Runtime.Tests/FootPredictionRenderingTests.cs)。其中 step 与 redirected-step 是通用序列模型的示例动作夹具；同一动作在平地、斜坡和台阶上读取最终 GPU 骨骼、接触时刻与普通 Cluster 呈现，并在同一流程统计预测、几何与整帧成本。

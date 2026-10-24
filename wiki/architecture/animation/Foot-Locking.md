# Foot Locking and Pelvis Compensation

本文记录公开脚部控制输入、接触状态和模型空间补偿。源码入口为 [FootLock](../../../src/SomeEngine.Animation.Pose/FootLock.cs)、[FootLockSamples](../../../src/SomeEngine.Animation.Pose/FootLockSamples.cs)、[FootLockSystem](../../../src/SomeEngine.Animation.Pose/FootLockSystem.cs) 及 [GPU IK](GPU-IK.md)。

## 定义与每个角色的状态

`FootLockRig` 保存骨盆编号及 `FootLockLeg`。腿的顺序对应选定路径的足部轨道；每项引用同一骨架的 `GpuTwoBoneChain`、GPU 目标编号、根骨骼局部弯曲方向和末端局部足底法线。`PelvisBone` 同时提供来源高度与 `GpuTwoBoneParameters.TranslationBone` 的配置。

`FootLockParameters.Source` 是描述实际 GPU 来源的初始选定路径及时间映射，安装后由 `FootLockInstance.Source` 保存当前选择。来源姿势通过 `PosePredictionBatch` 一次采样骨盆、各腿的根/中间/末端及脚跟/脚尖，接触种类通过 `FootPrediction` 复用已有动作数据。`ContactWindow` 是绑定接触窗口的叶节点时钟秒数；窗口推进时重新绑定，有限路径的终点继续复用末段数据。

每次安装分配该角色自己的连续采样、查询、目标和接触数组。`FootLockInstance.Contacts` 提供当前接触种类和完整 `FootContactTarget`，其中包括支撑实体、局部锚点、法线、当前变换及表面版本。共同启动标记变化时重新建立接触历史。

`FootLockInstance.Actor` 指向移动阶段维护 `WorldTransform` 的实体，初值为姿势目标。调用方可以在安装后将它绑定到现有角色实体。脚部系统在普通 Game 更新中读取这个变换，向姿势目标写入 `GpuPoseGoal` 和 `GpuPoseTarget.ModelOffset`，最终 G 沿既有提取与蒙皮路径输出。

## 接触自由度

| 种类 | 接触点位置 | 脚掌旋转 | 锚点更新 |
| --- | --- | --- | --- |
| Free | 沿当前来源轨迹 | 沿当前来源旋转 | 清空接触锚点 |
| Support | 脚跟保持支撑局部位置 | 来源旋转叠加地形倾斜 | 由当前支撑变换刷新 |
| Sliding | 沿来源的切向位移贴地移动 | 来源旋转叠加地形倾斜 | 查询当前表面并更新滑动偏移 |
| ToePivot | 脚尖保持支撑局部位置，脚跟随动作转动 | 来源旋转叠加地形倾斜 | 由当前支撑变换刷新 |

支撑和脚尖支点在持续接触中重建同一个支撑局部锚点，平台的平移、旋转与表面版本进入本帧目标。脚跟与脚尖支点之间交接时，新探针从当前修正后的脚部姿势取得。滑动保存相对于来源点的支撑局部偏移，随后由本帧来源和支撑变换重建探针，保持动作的切向位移及交接位置。

足底的模型参考法线在建立共享 `FootLockRig` 时从骨架参考姿势取得。初次接触将命中法线转换到模型空间，得到地形修正旋转，再把这个朝向保存到支撑局部。持续接触通过支撑当前旋转和角色当前旋转重建修正，随后叠加当前来源末端旋转；平台绕法线的转动也进入脚掌朝向。重探测在当前支撑朝向上对齐新的法线，并将修正保存在命中支撑的局部坐标中。

脚跟或脚尖到 IK 末端的当前来源偏移随同接触修正旋转，再从接触点还原末端位置。脚尖支点因此保留动作的抬跟和转动，接触种类交接沿用当前修正朝向。

两脚处于自由区间时，模型平移与目标权重归零，最终姿势保持来源动作。一个脚接触时，另一脚使用当前来源末端目标抵消骨盆平移对脚部轨迹的影响。

接触自由度与相对支撑锁定参考 Ubisoft 的 [Fitting the World](https://media.gdcvault.com/gdc2016/Presentations/Roche_Clifford_Fitting%20the%20World.pdf) 中 Foot Locking、Foot Orientation 和 Stabilizing the Hips；当前接口、锚点交接和下述可达区间由本项目实现。

## 骨盆可达高度

目标位置、来源关节、弯曲方向及骨盆位移统一采用角色模型空间。世界竖直方向经角色逆变换并归一化，得到模型方向 `U`。

对一条腿，来源根位置为 `H`，目标末端位置为 `F`，两段实际长度为 `a`、`b`。弯曲角 `theta` 对应半径平方 `a*a + b*b + 2*a*b*cos(theta)`，与 GPU Two-Bone 的角度约定相同。最小和最大弯曲角分别给出最远和最近半径。

令 `h = dot(H-F, U)`、`q = lengthSquared(H-F)-h*h`，竖直距离的近、远范围为 `sqrt(max(rNear*rNear-q,0))` 和 `sqrt(max(rFar*rFar-q,0))`。系统沿来源根相对于目标的上下分支，将它们转换成骨盆高度区间，再对所有腿取交集。各接触目标相对来源的平均竖直位移提供期望高度，交集约束得到最终高度。自由脚的来源目标也参加同一可达计算。

共同图权重及所选 `AnimationLayers` 权重同时缩放骨盆位移和 GPU 目标权重。GPU 在 FK 后平移所配置的骨盆及后代，再执行两个腿部求解；完整姿势随后进入历史和普通 Cluster 消费。

## 安装

以下是两条腿的接入例子。`path` 描述与 `source` 相同的来源与时钟，`surfaces` 是已经就绪的场景查询，`actor` 是已有移动实体。

```csharp
var feet = new FootLockRig(pelvisBone,
[
    new(leftLeg, 0, Vector3.UnitZ, Vector3.UnitY),
    new(rightLeg, 1, Vector3.UnitZ, Vector3.UnitY),
]);
var pose = new GpuTwoBonePose(
    new GpuTwoBonePose(source, leftLeg, 0), rightLeg, 1);
Entity target = GpuPoseAnimation.CreateTarget(world, skeletonInstance);
using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    builder.Leaf<GpuTwoBoneParameters, GpuTwoBoneLeaf>(target,
        new(pose, TranslationBone: feet.PelvisBone));
    builder.LockFeet(target, new(feet, new(path, new PoseTimeOrigin(0)),
        surfaces, new FootSupportSettings(Vector3.UnitY, 1, 2)));
}
Entity animation = builder.Play();
world.ExecuteBufferWrite<FootLockInstance, Entity>(animation, ref actor,
    static (instances, ref actor) => instances[0].Actor = actor);

GpuPoseAnimation.AddSystems(systems);
AnimationSystems.Add(systems, new FootLockSystem(), AnimationPhase.Composition);
```

`FootLockSystem` 注册在 GPU 来源系统之后。它先收集活动叶节点，随后在各自的 World 查询范围中读取实际几何并发布目标。Game 阶段完成移动和碰撞树维护后，动画阶段取得同一帧的变换；提取复制目标和骨盆位移，Rendering world 据此求解。

动作接触与时间映射见 [Foot Motion](Foot-Motion.md)，实际地表与锚点接口见 [Foot Support](Foot-Support.md)。公开应用消费见 [FootLockRenderingTests](../../../tests/SomeEngine.Runtime.Tests/FootLockRenderingTests.cs)。

提前摆腿、未来根/移动与落点、动作切换接续和分项成本见 [Future Foot Placement](Future-Foot-Placement.md)。

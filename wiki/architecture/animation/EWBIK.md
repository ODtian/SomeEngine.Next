# EWBIK

EWBIK 通过多个位置与朝向目标的加权方向拟合，逐关节修正骨架。源码入口为 [共享定义](../../../src/SomeEngine.Animation.Pose/EwbikRig.cs)、[方向构造与 CCD](../../../src/SomeEngine.Animation.Pose/EwbikSamples.cs)、[四元数拟合](../../../src/SomeEngine.Animation.Pose/EwbikRotationFitting.cs)、[公开叶节点](../../../src/SomeEngine.Animation.Pose/EwbikLeaf.cs) 和 [依赖阶段系统](../../../src/SomeEngine.Animation.Pose/EwbikSystem.cs)。

## 定义与实例

`EwbikRig` 引用就绪 `Skeleton`、共同根的自身骨骼编号及 `EwbikEffector` 数组。末端和中间骨骼使用同一种效应器。创建定义时解析各效应器到共同根的祖先、每个关节可见的目标、深度权重，以及旋转后需要更新的自身后代；这些数组由播放实例共享。

`EwbikEffector.DepthFalloff` 控制后方目标通过这个 pin 向祖先传播的权重。跨过中间 pin 时乘入该值，`1` 保持后方目标的权重，`0` 让祖先消费这个 pin 自身的目标。多个 pin 可引用同一个骨骼，其目标分别累加。`AxisLength` 是朝向方向对在模型单位下的长度，决定朝向项相对于位置项的几何尺度。

每个安装实例持有自己的 `Targets`、`Settings`、来源/输出引用、局部姿势、模型矩阵、父视图和拟合数组。每次求值从当前来源局部姿势重新建立模型结果。继承父结果沿已完成的实时 `BonePose` 读取，共同节点依赖保持父 G 先于消费方完成。

`EwbikTarget.Position`、`Orientation` 使用共同模型空间，朝向为单位四元数。`Weight` 是非负整体权重，`AxisPriorities` 是非负的 X/Y/Z 朝向优先级；零朝向优先级提供纯位置目标。各轴指的是该骨骼自身轴通过目标朝向变换后的方向。

## 方向拟合与阻尼

对每个祖先关节，令其模型位置为 `P`，当前末端位置为 `E`，目标位置为 `T`。位置项使用方向 `E-P` 与 `T-P`。对于每个活动朝向轴，分别把当前轴和目标轴乘 `AxisLength`，围绕末端与目标位置构造正、负方向对：

```text
current = E-P ± currentAxis*AxisLength
target  = T-P ± targetAxis*AxisLength
```

位置项采用整体权重乘深度权重，轴方向对再乘对应轴优先级。方向对同时表达目标位置与朝向；所有目标共同最小化：

```text
sum(weight * squaredLength(R*current-target))
```

`EwbikRotationFitting` 累加 3×3 加权协方差，再形成对称 4×4 四元数特征问题。Jacobi 旋转求最大特征空间，归一化四元数形成正确旋转。纯位置方向存在自由扭转时，把单位旋转投影到最优特征空间，取得其中角度较小的修正。

一次迭代按骨骼从末端向共同根处理。`EwbikSettings.Damping` 是每次关节修正的最大角度，单位为弧度；`EwbikBoneSettings.DampingScale` 逐骨缩放该角度，零值保留该骨骼的来源旋转。系统把模型修正变回当前父坐标系下的局部旋转，立即更新后代模型结果，再处理下一个关节。来源局部平移与缩放持续参与普通 FK。

`Iterations` 限定一次求值的迭代次数。所有活动位置误差进入 `PositionTolerance`，且活动轴方向夹角进入 `OrientationTolerance` 后提前完成。存在竞争的目标通过同一加权拟合取得折中姿势。

## 共同图调用

下面是双手与一个肘部目标的接入例子，骨骼编号与 [EwbikBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/EwbikBehaviorTests.cs) 中的示例夹具对应：

```csharp
var rig = new EwbikRig(skeleton, 0, [new(4), new(8), new(2)],
    bones: [new(0, DampingScale: 0)],
    settings: new(Damping: .35f, Iterations: 192));
using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(target,
        new(clip, new PoseResultId(0), Loop: true));
    builder.SolveEwbik(target,
        new(rig, new PoseResultId(0), new PoseResultId(1)));
}
Entity animation = builder.Play();
world.ExecuteBufferWrite<EwbikInstance, EwbikTarget[]>(animation, ref goals,
    static (instances, ref goals) => goals.CopyTo(instances[0].Targets, 0));
PoseAnimation.AddSystems(systems,
    nodes: static nodes => nodes.Add(new EwbikSystem()));
```

`EwbikSystem` 在对应 `PoseNodeContext.Level` 读取当次来源，提交各实例的具体 Job，完成后写入普通 `LocalPose`/`PoseResult`。来源采样时刻、相位、启动标记与根运动继续随输出传递；共同图、层、普通姿势组合与最终 `SkeletonInstance` 沿现有入口消费该结果。调用方可通过实例 `Settings` 分别调整求解参数。

## 上游与许可

加权后代方向、pin 深度传播与角度阻尼改编自 Eron Gjoni 的 [Everything-Will-Be-IK](https://github.com/EGjoni/Everything-Will-Be-IK/tree/acc3383ed2603a53b92e272847d6a6af8c557013)，固定版本为 `acc3383ed2603a53b92e272847d6a6af8c557013`。上游的 `SegmentedArmature.java`、`AbstractIKPin.java` 和 `QCP.java` 提供参考；此实现使用实际当前位置方向，并以对称特征求解取得加权最优旋转。

作者与数学来源见 [EWBIK attribution](../../../external/ewbik/README.md)。完整 [MIT 许可](../../../external/ewbik/LICENSE) 随源码保存，并经 Pose 项目的内容项复制到构建及发布的 `licenses/EWBIK-LICENSE.txt`。

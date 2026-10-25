# EWBIK

EWBIK 通过多个位置与朝向目标的加权方向拟合，逐关节修正骨架。源码入口为 [共享定义](../../../src/SomeEngine.Animation.Pose/EwbikRig.cs)、[方向构造与 CCD](../../../src/SomeEngine.Animation.Pose/EwbikSamples.cs)、[四元数拟合](../../../src/SomeEngine.Animation.Pose/EwbikRotationFitting.cs)、[Kusudama 范围](../../../src/SomeEngine.Animation.Pose/Kusudama.cs)、[公开叶节点](../../../src/SomeEngine.Animation.Pose/EwbikLeaf.cs) 和 [依赖阶段系统](../../../src/SomeEngine.Animation.Pose/EwbikSystem.cs)。

## 定义与实例

`EwbikRig` 引用就绪 `Skeleton`、共同根的自身骨骼编号及 `EwbikEffector` 数组。末端和中间骨骼使用同一种效应器。创建定义时解析各效应器到共同根的祖先、每个关节可见的目标、深度权重，以及旋转后需要更新的自身后代；这些数组由播放实例共享。

`EwbikEffector.DepthFalloff` 控制后方目标通过这个 pin 向祖先传播的权重。跨过中间 pin 时乘入该值，`1` 保持后方目标的权重，`0` 让祖先消费这个 pin 自身的目标。多个 pin 可引用同一个骨骼，其目标分别累加。`AxisLength` 是朝向方向对在模型单位下的长度，决定朝向项相对于位置项的几何尺度。

每个安装实例持有自己的 `Targets`、`Settings`、`PreferenceWeights`、来源/输出引用、局部姿势、模型矩阵、父视图和拟合数组。每次求值从当前来源局部姿势重新建立模型结果。继承父结果沿已完成的实时 `BonePose` 读取，共同节点依赖保持父 G 先于消费方完成。

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

一次迭代按骨骼从末端向共同根处理。`EwbikSettings.Damping` 是每次关节修正的最大角度，单位为弧度；`EwbikBoneSettings.DampingScale` 逐骨缩放该角度，零值保留该骨骼经硬范围投影后的来源旋转。系统把模型修正变回当前父坐标系下的局部旋转，应用该关节的 Kusudama，立即更新后代模型结果，再处理下一个关节。来源局部平移与缩放持续参与普通 FK。

`Iterations` 限定一次求值的迭代次数。所有活动位置误差进入 `PositionTolerance`，且活动轴方向夹角进入 `OrientationTolerance` 后提前完成。存在竞争的目标通过同一加权拟合取得折中姿势。

## Kusudama 摆动、扭转与偏好

`Kusudama` 持有一个或多个有序 `KusudamaCone`。每个 cap 的 `Axis` 是限制坐标系下的单位向量，`Radius` 是弧度。相邻 cap 之间用两侧相切圆连接，允许区域为 cap 与这些走廊的并集。就绪定义使用可形成两个相切圆的相邻锥：轴夹角位于 `(0, pi)`，半径和小于 `pi`，半径差的绝对值小于轴夹角。

构造时计算 cap 的正余弦、每段的两个相切圆中心、圆半径和楔形边界法线。对轴 `A/B`、半径 `a/b`，相切圆半径为 `t=(pi-a-b)/2`；单位中心满足 `dot(C,A)=cos(a+t)` 和 `dot(C,B)=cos(b+t)`。循环内通过这些预计算平面判断连通走廊。范围外的方向投影到最近 cap 或对应走廊的圆边界。

`LimitingFrame` 为从限制坐标系转到骨骼父坐标系的单位四元数；`PrimaryAxis` 为骨骼自身的单位主轴。对局部旋转 `q`，用 `inverse(LimitingFrame)*q` 表达相对旋转，其变换后的主轴是被限制的摆动方向。相对旋转再按主轴分解 swing/twist，把有符号扭转限制在 `[MinimumTwist, MaximumTwist]` 内；这两个端点以弧度给出，位于 `[-pi, pi]` 并按大小排列。

`EwbikConstraint` 绑定一个自身骨骼编号和共享 `Kusudama`。创建 `EwbikRig` 时解析逐骨约束索引，并把约束骨骼的祖先纳入共同节点依赖。每次求值先对来源姿势应用硬范围，随后每次拟合修正也应用硬范围。

`PreferenceWeight` 初始化播放实例的 `PreferenceWeights`，数组顺序与 `rig.Constraints` 一致。权重是非负的角度倍率：每次迭代分别以 `Damping * DampingScale * PreferenceWeight` 为上限向锥序列中心路径与扭转中点修正，再应用硬范围。零权重保持纯硬限制；正权重在效应器目标与舒适区域之间形成连续折中。多个播放实例共享范围几何，各自更新偏好权重。

下面是一个旋转限制坐标系下的接入例子；两个 cap 和骨骼编号取自 [KusudamaBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/KusudamaBehaviorTests.cs) 的示例夹具，通用接口消费任意就绪锥序列：

```csharp
var region = new Kusudama(
    [new(new(-.5f, .8660254f, 0), .12f), new(new(.5f, .8660254f, 0), .12f)],
    limitingFrame, Vector3.UnitY, minimumTwist: -.2f, maximumTwist: .25f);
var rig = new EwbikRig(skeleton, 0, [new(2), new(1), new(4)],
    bones: [new(0, DampingScale: 0)], constraints: [new(1, region)]);
float preference = .2f;
world.ExecuteBufferWrite<EwbikInstance, float>(animation, ref preference,
    static (instances, ref preference) => instances[0].PreferenceWeights[0] = preference);
```

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

加权后代方向、pin 深度传播、角度阻尼与 Kusudama 改编自 Eron Gjoni 的 [Everything-Will-Be-IK](https://github.com/EGjoni/Everything-Will-Be-IK/tree/acc3383ed2603a53b92e272847d6a6af8c557013)，固定版本为 `acc3383ed2603a53b92e272847d6a6af8c557013`。上游的 `SegmentedArmature.java`、`AbstractIKPin.java` 和 `QCP.java` 提供拟合参考；此实现使用实际当前位置方向，并以对称特征求解取得加权最优旋转。`AbstractLimitCone.java` 与 `AbstractKusudama.java` 提供连通区域、限制坐标系 swing/twist 与软偏好参考，相切圆中心在此实现中由两平面与单位球的交点直接计算。

作者与数学来源见 [EWBIK attribution](../../../external/ewbik/README.md)。完整 [MIT 许可](../../../external/ewbik/LICENSE) 随源码保存，并经 Pose 项目的内容项复制到构建及发布的 `licenses/EWBIK-LICENSE.txt`。

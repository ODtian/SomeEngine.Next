# Pose Blending

本页记录已实现的姿势混合数据、公共图入口与批量求值。源码入口为 `BoneTransformOps`、`PoseBlendLeaf`、`PoseBlendSystem`、`BlendSpace` 与 `BlendSpaceSystem`；图内结果的范围和时间由 [Pose Graphs](Pose-Graphs.md) 说明。

## 权重混合

`PoseBlend` 保存来源 `PoseResultId`、固定权重及可选的 `GraphParameters` 下标。`PoseBlendLeaf` 在安装时将编号解析为当前目标的 `PoseResultReference`。每帧有效输入权重为固定权重、参数值及来源当前贡献权重的乘积。输入使用非负权重，正权重总和用于归一化。

`BoneTransformOps` 实现 `IValueOps<BoneTransform>`。平移和缩放使用线性组合，旋转按同一半球对齐后加权并归一化。具体骨骼循环保留 `Vector3`、`Quaternion` 与 `Vector128<float>` 运算；两个输入使用共同 `ValueOps` 的批量 `Lerp` 入口。

输出的 `Time`、累计 `Phase` 与根运动增量按同一组输入权重混合，结果自身的贡献权重来自该叶节点的图权重和目标动画层权重。后续节点继续消费这个具名结果。

以下是将两个已生产结果混合为第三个结果的使用例子：

```csharp
var blend = new PoseBlend([
    new(new PoseResultId(0), Parameter: 0),
    new(new PoseResultId(1), Parameter: 1),
]);
builder.BlendPose(target, new PoseBlendParameters(blend, new PoseResultId(2),
    new MotionBoundary(StageEnd.Duration, 2)));

PoseAnimation.AddSystems(systems, static composition =>
    AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));
```

同一 `CompiledGraph` 可以安装到多个目标。每个实例持有解析后的引用、连续工作区及自己的时间和输入参数。同类混合节点的结果引用构成有向无环的生产关系，`PoseBlendLeaf.Complete` 按已解析的输入与输出计算依赖层级。系统逐层为各实例提交具体混合 Job，声明输入、权重与输出数组的访问；该层完成句柄收敛并写入公开结果 buffer 后，再求值下一层。生产者与消费者按这组结果依赖求值，最终 Composition 消费所选结果。

## Blend Space 与相位

Blend Space 中的片段使用同一骨架。`BlendSpace.From1D` 接收至少两个互异坐标及正时长片段，按坐标排序后保存相邻区间。`BlendPose1D` 从实例的 `GraphParameters` 读取坐标，以所在区间的两个端点线性插值；范围外的坐标使用最近端点。

`BlendSpace.From2D` 接收二维点和显式 `BlendTriangle` 三角剖分，保存顶点编号及预计算的重心坐标变换。`BlendPose2D` 从两个图参数读取坐标，使用包含该点的三角形的三个重心权重；三角网外的坐标投影到最近边。

每个点保存片段及以循环数表示的 `PhaseOffset`。实例独立持有连续累计的 `Phase`，每帧按 `Delta × sum(weight / clip.Duration)` 推进。所有贡献片段使用共同相位加自身偏移，小数部分映射到各自片段时长。改变混合坐标会改变之后的循环频率，并延续已有相位。

`PoseResult.Time` 是该 Blend Space 叶节点的累计播放时间，`Phase` 是其累计循环数。片段中的根 TRS 随局部姿势一起混合。`BlendSpaceSystem` 在具体 Job 内采样贡献片段并混合完整骨骼数组；将它注册在消费这些结果的系统之前。

以下是按速度参数选择行走和跑步片段的使用例子：

```csharp
var locomotion = BlendSpace.From1D([
    new BlendPoint1D(1, walk),
    new BlendPoint1D(4, run, PhaseOffset: 0.25f),
]);
builder.BlendPose1D(target, locomotion, new PoseResultId(0), parameter: 0);
PoseAnimation.AddSystems(systems, static composition =>
    AnimationSystems.Add(composition, new BlendSpaceSystem(), AnimationPhase.Composition));
```

`BlendSpaceLeaf` 使用共同 `Leaf` 安装入口；持续播放由共同时间线和包含它的状态、阶段控制。

以下是使用二维方向参数和显式三角剖分的使用例子：

```csharp
var directions = BlendSpace.From2D([
    new BlendPoint2D(new(-1, 0), left),
    new BlendPoint2D(new(1, 0), right),
    new BlendPoint2D(new(0, 1), forward),
], [new BlendTriangle(0, 1, 2)]);
builder.BlendPose2D(target, directions, new PoseResultId(0), parameterX: 0, parameterY: 1);
```

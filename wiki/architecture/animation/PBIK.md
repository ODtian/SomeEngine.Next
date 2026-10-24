# PBIK

`PbikRig` 连接共享根骨骼与多个末端目标，保存骨骼刚度、优先角度、关节限制和求解精度。`PbikLeaf` 消费共同图中的局部姿势，`PbikSystem` 在实例工作区中一起求解骨盆、躯干和四肢，再将局部结果交给普通姿势合成与 FK。

## 定义与实例数据

骨骼编号对应 `Skeleton.Parents` 的自有骨骼索引。`Root` 是各末端的共同祖先；求解骨骼由每个末端到根的父链并集组成。`PbikRig` 复制定义数组并预先建立父子连接，多个播放实例共享同一定义。

`PbikBoneSettings` 配置位置刚度、旋转刚度和优先角度。刚度范围为 `0` 到 `1`，分别表示自由参与修正和固定对应的输入位置或旋转；中间值改变约束修正的分配。`PreferredAngles` 使用相对参考局部旋转的 X/Y/Z 欧拉角，单位为弧度，`PreferredWeight` 控制输入旋转向该优先姿势偏置的比例。

`PbikJointLimit` 以同一参考局部旋转为基准限制 X/Y/Z 欧拉角。旋转组合为 `Qz * Qy * Qx`。相等的上下界固定该轴角度；其他轴按各自范围投影。

每个 `PbikInstance` 分别持有 `Targets`、工作数组和结果引用。目标按 `Rig.Effectors` 顺序排列，`Position` 位于模型空间。`Weight` 从当前输入末端位置向目标插值，范围为 `0` 到 `1`；每次求值使用当前动画姿势重新计算该位置。

## 全身约束投影

每帧从输入姿势生成模型位置与旋转，并在父骨骼坐标中记录子骨骼连接点。优先角度建立初始弯曲方向，多个末端的平均位移提供骨盆移动的初始值。

每轮将末端位置向加权目标投影，再沿整个骨骼树往返投影父子连接约束。连接误差由父子位置移动度及父骨骼角移动度共同分配，父骨骼旋转通过连接点的力臂修正。共享骨盆和躯干因此同时接受多条肢体的约束。随后在真实父坐标系中应用关节角度限制，并用当前骨盆与骨骼旋转计算末端的 FK 误差。所有目标进入 `Tolerance` 后结束，`Iterations` 限制本次工作量。

输出保存求解根的局部平移和参与骨骼的局部旋转，其他局部平移、缩放及分支继续来自输入姿势。局部结果通过共同 `PoseResult` 交接，普通 FK 生成公开模型姿势。来源时间、相位、启动标记和根运动随输出传递，节点贡献采用共同图与动画层权重。

位置与角度投影采用[基于位置的刚体约束方法](https://matthias-research.github.io/pages/publications/PBDBodies.pdf)。多效应器、刚度和优先角度的使用语义参考 [Full-Body IK](https://dev.epicgames.com/documentation/en-us/unreal-engine/control-rig-full-body-ik-in-unreal-engine)。

## 共同图调用

下面是以骨骼 `0` 为骨盆、`4` 和 `7` 为左右手的调用示例。目标实体通过 `PoseAnimation.CreateTarget` 分配两个结果，并选择结果 `1` 作为最终输出。

```csharp
var rig = new PbikRig(skeleton, 0, [4, 7],
    settings: [new(3, PreferredAngles: new(0, 0, -.6f), PreferredWeight: 1)],
    limits: [new(3, new(0, 0, -1.5f), Vector3.Zero)]);

using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(target,
        new(clip, new PoseResultId(0), Loop: true));
    builder.SolvePbik(target, new(rig, new PoseResultId(0), new PoseResultId(1)));
}
Entity animation = builder.Play();
PbikTarget[] goals = [new(new(-2, 1.5f, 0)), new(new(2, 1.5f, 0), .5f)];
world.ExecuteBufferWrite<PbikInstance, PbikTarget[]>(animation, ref goals,
    static (instances, ref goals) => goals.CopyTo(instances[0].Targets, 0));

PoseAnimation.AddSystems(systems,
    nodes: static nodes => nodes.Add(new PbikSystem()));
```

`RegisterNode` 建立输入输出依赖，`PbikSystem` 在对应级别提交具体 `SolveBody` Job。Job 读取共享定义与骨架，写入该实例的工作区；完成同步后，系统写回来源对应的局部姿势结果。活动节点每帧读取当前目标和来源姿势。

[PbikSolver](../../../src/SomeEngine.Animation.Pose/PbikSolver.cs) 接收连续的局部姿势、目标、模型矩阵和工作区 Span。共同图实例继续由 `PbikSamples` 持有这些数组；[链重定向](Retargeting.md) 的采样调用借用数组并执行同一求解循环。

源码：[共享定义](../../../src/SomeEngine.Animation.Pose/PbikRig.cs)、[全身约束投影](../../../src/SomeEngine.Animation.Pose/PbikSolver.cs)、[实例工作区](../../../src/SomeEngine.Animation.Pose/PbikSamples.cs)、[叶节点与实例](../../../src/SomeEngine.Animation.Pose/PbikLeaf.cs)、[具体求解系统](../../../src/SomeEngine.Animation.Pose/PbikSystem.cs)。

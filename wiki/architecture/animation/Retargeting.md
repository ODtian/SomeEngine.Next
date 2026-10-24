# Chain Retargeting

本文记录已就绪骨架之间的链映射、参考旋转传递和末端校正。公共入口为 [RetargetMapping](../../../src/SomeEngine.Animation.Pose/RetargetMapping.cs)，具体采样适配器为 [RetargetSampler](../../../src/SomeEngine.Animation.Pose/RetargetSampler.cs)，末端求解复用 [PBIK](PBIK.md) 的 [PbikSolver](../../../src/SomeEngine.Animation.Pose/PbikSolver.cs)。

## 共享映射

`RetargetMapping` 引用来源和目标 `Skeleton`。`RetargetRoot` 指定双方自身骨骼编号、参考模型轴及根平移比例；`RetargetChain` 指定双方父到子的相邻自身骨骼链、参考模型轴、`GoalScale`、`GoalWeight` 与目标模型空间的 `GoalOffset`。每条链至少包含两根骨骼，各目标末端由共同的目标根连接。

`SourceAxes` 和 `TargetAxes` 是把同一基准轴系旋转到各自参考模型空间的单位四元数。映射创建时完成参考 FK、链弧长、来源采样骨骼及必要祖先、目标旋转对应和 PBIK 拓扑解析。目标与来源可以使用不同段数、段长和局部参考旋转。目标链按参考弧长归一化，每根目标骨骼在相同归一化长度上取得两根来源骨骼及其插值量。

令来源插值模型旋转为 `Qs`，对应来源参考旋转为 `Rs`，目标参考旋转为 `Rt`，轴系对齐为 `A = TargetAxes * inverse(SourceAxes)`，目标模型旋转为：

```text
Qt = A * (Qs * inverse(Rs)) * inverse(A) * Rt
```

采样时再使用当前目标父模型旋转的逆，把 `Qt` 转为局部旋转。目标保留自身参考骨段平移和缩放；其余分支以自身参考局部值跟随已映射父骨骼。

## 根运动与末端

根平移以来源根相对参考位置的变化为输入：先转入 `RetargetRoot.SourceAxes`，逐轴乘 `TranslationScale`，再通过 `TargetAxes` 转到目标模型空间并加上目标参考根位置。根旋转采用相同的参考旋转传递。输出片段在来源声明了根运动时使用目标根作为 `RootMotionBone`，保留时长和事件；普通 `ClipLeaf` 可以继续提取 `RootMotionDelta`，移动阶段消费该增量。

每条链的末端变化按来源根测量，结合目标自身参考末端与链长比例生成目标。设 `S` 为来源末端相对当前来源根的位置，`Sr` 为其参考值，`Tr` 为目标末端相对目标参考根的位置，`P` 为重定向后的目标根位置：

```text
goal = P + Tr + rotate(A, S-Sr) * (targetLength/sourceLength) * GoalScale + GoalOffset
```

`GoalWeight` 进入既有 `PbikTarget.Weight`，其余刚度、优先角度、关节限制、容差和迭代设置由映射的 `Correction` 持有。PBIK 使用这次参考旋转传递产生的局部姿势作为输入，将所有链末端放进同一目标骨架求解，再交回普通局部姿势与 FK。

参考长度归一化、FK 后执行 IK 的组织方式参考 Epic 的 [Retargeting Operation Stack](https://dev.epicgames.com/documentation/unreal-engine/retargeting-operation-stack-in-unreal-engine-5-8)。本页的输入职责、参考变换和目标公式对应当前源码。

## 公共采样与播放

`CreateClip(AnimationClip)` 与 `CreateClip(PosePath)` 返回同一种普通目标 `AnimationClip`。`PosePath` 可以提供当前已选定的序列和混合。`AnimationClip.SampleLocal`、[ClipSampleBatch](Clip-Assets.md) 的局部/模型采样、共同图 `ClipLeaf`、`PoseBlendLeaf` 和最终 `SkeletonInstance` 沿现有入口消费此片段。

共享适配数据保存映射、来源和可选父 `ModelPose` 视图。每次采样调用借用来源局部行、模型矩阵、目标和 PBIK 工作数组；一个批次先采样来源各时刻，再逐行转移和求解，结束时归还数组。图中的每个 Clip 实例继续持有自己的输出与播放历史。

继承骨架的 `sourceParent` 与 `targetParent` 读取调用方已完成的当前父 G；这两种 `ModelPose` 都保持既有实时视图语义。省略时，采样使用对应参考父姿势。目标模型批量采样使用同一个目标父结果配置 `ClipSampleSelection.parentPose`；播放阶段使用同一个目标父实例。调用方在采样阶段前完成父姿势，并在消费期间保持结果有效。

以下为二段来源链映射到三段目标链的接入例子，运动根为 `0`、肢体链根为 `1`；骨骼和轴系由调用方已取得的数据提供：

```csharp
var mapping = new RetargetMapping(sourceSkeleton, targetSkeleton,
    new RetargetRoot(0, 0, sourceAxes, targetAxes, new Vector3(1.5f)),
    [new RetargetChain([1, 2, 3], [1, 2, 3, 4], sourceAxes, targetAxes)],
    settings: [new PbikBoneSettings(0, PositionStiffness: 1, RotationStiffness: 1)]);
AnimationClip clip = mapping.CreateClip(selectedPath);

using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(target,
        new(clip, new PoseResultId(0), RootMotion: RootMotionMode.Extract));
    builder.BlendPose(target,
        new(new PoseBlend([new(new PoseResultId(0))]),
            new PoseResultId(1), new(StageEnd.Settled)));
}
```

公开消费入口为 [RetargetingBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/RetargetingBehaviorTests.cs)。Reach、broad 和 tall 是通用链映射的示例动作和身体夹具；公共局部/模型采样与真实 World 中的共同图消费同一份片段。

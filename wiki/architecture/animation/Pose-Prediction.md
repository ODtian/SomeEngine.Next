# Pose Prediction

`PosePath` 表达调用者已选定的播放路径。`FromClip` 引用就绪片段，`Sequence` 按声明顺序拼接路径，各段使用自己的局部时间。路径保留共享资源的普通引用。

`FromClip` 可同时绑定导入所得 `FootMotion`，供同一选定路径的足部轨迹和接触查询使用，见 [Foot Motion](Foot-Motion.md)。

`PosePredictionRequest` 把路径、`PoseTimeOrigin` 和调用者的 `Conditions` 标识绑定在一起。时间映射为 `PathTime + (requestedTime - Time) * Speed`，结果采样时间落在路径持续时间内。重新选择动作或调整时间基准时，创建对应的新请求。

`PosePredictionBatch` 把多条请求及其骨骼选择绑定到既有 `ClipSampleBatch`。根、脚链与其他骨骼按调用者指定的顺序返回；模型矩阵计算使用已有的祖先展开与父骨架结果。片段编解码器仍接收自己的批量时间和骨骼选择。

以下是批量查询根与脚链的调用示例：

```csharp
PosePath selected = PosePath.Sequence([
    PosePath.FromClip(turn),
    PosePath.FromClip(land),
]);
var request = new PosePredictionRequest(selected,
    new PoseTimeOrigin(Time: 10, PathTime: 0.25f, Speed: 1), Conditions: 42);
var batch = new PosePredictionBatch([
    new(request, [root, toe, ankle]),
    new(request, [root, toe, ankle]),
]);
var poses = new Matrix4x4[batch.OutputCount];
var samples = new PosePredictionSample[batch.Count];
batch.SampleModel([10.5, 11.5], calculationTime: 10.1, poses, samples);
```

每个 `PosePredictionSample` 保存完整请求、请求的时钟时间、实际路径采样时间和计算时钟时间。`GetOutputRange` 给出该请求在输出中的区段。就绪路径的时间采样只读取路径数据；真实播放的时间线、图状态、姿势与事件继续由已安装的播放实例持有。

## 混合曲线与遮罩层

`PosePath.Blend` 接受多条 `PosePathInput`，每条来源绑定一个 `PoseWeightCurve`。曲线按路径秒数求出共同 `Curve` 的归一化进度，再映射到 `From` 和 `To`。缓动、函数与关键帧沿既有确定性曲线求值，关键帧数组在创建时复制。

`PosePath.Layer` 接受基础路径、来源路径、就绪 `BoneMask`、共同 `LayerMode` 和权重曲线。Override 使用相同的 TRS 插值；Additive 使用 `BoneTransformOps.Additive` 的参考相对位移、旋转差和缩放比。片段时间在各来源自身的持续时间内采样，组合持续时间取来源最大值。

以下是已选定交叉淡入与上身动作的调用示例：

```csharp
PosePath locomotion = PosePath.Blend([
    new(PosePath.FromClip(walk), new PoseWeightCurve(1, 0, duration: 0.4f)),
    new(PosePath.FromClip(run), new PoseWeightCurve(0, 1, duration: 0.4f)),
]);
PosePath selected = PosePath.Layer(locomotion, PosePath.FromClip(aim), upperBodyMask,
    LayerMode.Override, new PoseWeightCurve(0, 1, duration: 0.2f));
```

组合路径把各子来源的多个时刻与所需骨骼一起送入具体采样器，再对选定骨骼执行共同混合运算。请求中的路径对象及曲线就是预测采用的播放选择。

## 独立状态推进

`PosePrediction<TParameters, TState, TAlgorithm>` 接收明确的初始局部姿势和算法历史，两者对应请求的时间基准。构造时复制局部姿势，并通过具体算法的 `CopyState` 复制可变历史；不可变定义和就绪资源继续共享普通引用。

算法实现 `IPosePredictionAlgorithm<TParameters, TState>`。`Advance` 接收共同 `MotionTime`、当前路径目标姿势、预测实例的可写姿势以及独立状态引用。状态算法可以直接调用既有 `IMotion` 运算，也可以一次处理完整骨骼数组。

`AdvanceTo` 按调用者选择的递增时刻推进，算法时间沿时间基准持续增长，增量为本次与前次时钟差乘播放速率。每次先在选定路径的持续时间内采样目标，再运行具体算法，最后使用既有批量层级计算返回所选模型空间骨骼。状态算法的积分步长由调用序列和具体算法共同决定。

以下是带速度历史的姿势算法调用示例：

```csharp
var prediction = new PosePrediction<SpringParameters, SpringPoseHistory, SpringPoseAlgorithm>(
    new(request, [root, toe]), in parameters, in initialHistory, initialLocalPose);
PosePredictionSample first = prediction.AdvanceTo(10.25, calculationTime: 10.01, poses);
PosePredictionSample next = prediction.AdvanceTo(10.5, calculationTime: 10.02, poses);
```

只读批次和状态预测实例各自持有复用工作区。真实 World 的时间线、事件 writer 和叶节点历史继续由原播放实例持有。

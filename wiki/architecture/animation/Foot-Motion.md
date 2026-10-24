# Foot Motion

`FootMotion` 是片段的就绪足部数据。它持有一份 `RootMotionPath`，以及每只脚的脚跟、脚尖轨迹和接触区段。`FootBones` 保存骨架自身骨骼编号，区段时间使用片段秒数。数据通过普通引用保持有效。

`FootMotionBlock` 把同一个就绪对象保存在 `AnimationClipAsset` 中；`CreateMotion` 返回该对象。`FootMotionImporter` 与 Raw、ACL 或量化片段 Importer 并列选择，各自交付对应 Block。

## 来源与分析

骨架来源在 `skin.extras.someengine_feet` 中声明脚跟和脚尖的 glTF 节点编号。动画来源在 `animation.extras.someengine_foot_contacts` 中声明动作标记，`foot` 对应该脚数组的位置。以下为一只脚和一次脚尖旋转的来源示例：

```json
{
  "someengine_feet": [{ "heel": 2, "toe": 3 }]
}
```

```json
{
  "someengine_foot_contacts": [
    { "foot": 0, "start": 0.25, "end": 0.75, "kind": "ToePivot" }
  ]
}
```

来源 `.meta` 选择 `FootMotionImporter`。`FootMotionSettings` 指定采样率、skin、根节点、地面高度、低位容差、静止速度容差和竖直轴；默认采样率 60 Hz，竖直轴 1 表示 Y。地面高度和容差使用来源骨架的模型空间单位。

分析使用已有 `ClipSampleBatch` 一起采样根、脚跟、脚尖及必要祖先，模型轨迹包含根的移动。采样时间合入动作标记端点。高度和相邻样本速度生成区段，随后动作标记按来源列表顺序覆盖对应区间。

| 分类 | 数据含义 | 自动分析依据 |
| --- | --- | --- |
| `Support` | 支撑接触 | 脚跟低位静止 |
| `ToePivot` | 脚尖支点 | 脚尖低位静止，脚跟移动 |
| `Sliding` | 贴地滑动 | 脚跟与脚尖低位移动 |
| `Free` | 自由运动 | 抬脚与摆动 |

`FootMotion.Analyze` 也接受程序提供的就绪片段、根编号、脚骨骼和动作标记。`Contacts` 返回区段，`ContactAt` 返回指定时刻的分类。`Sample` 返回模型空间根位姿与脚跟、脚尖位置；根旋转采用球面插值，双足位置按预计算样本线性插值。采样率决定轨迹近似精度。`Root` 可直接作为 Motion Warping 的共享来源轨迹。

## 选定路径与预测窗口

创建 `PosePath` 时把对应 `FootMotion` 与片段一起绑定。`FootPrediction` 接收同一 `PosePredictionRequest` 和向前窗口长度，长度使用请求的时钟秒数。`StartTime`、`EndTime` 描述可查询范围；有限路径在结束时收束窗口。

以下是单次攻击、转身与落地的调用示例：

```csharp
PosePath selected = PosePath.Sequence([
    PosePath.FromClip(attackClip, attackFeet),
    PosePath.FromClip(turnClip, turnFeet),
    PosePath.FromClip(landingClip, landingFeet),
]);
var request = new PosePredictionRequest(selected,
    new PoseTimeOrigin(Time: 10, PathTime: 0.4f, Speed: 2), Conditions: 81);
var feet = new FootPrediction(request, lookAhead: 1.5f);
if (feet.TryGetNextContact(foot: 0, afterTime: 10, calculationTime: 10.01, out var contact))
    UseContact(contact.Prediction.Time, contact.Contact.Kind, contact.Trajectory);
```

`TryGetNextContact` 查询窗口内下一次自由运动进入接触的时刻；同一接触中的分类变化通过 `Sample` 读取。结果保留完整请求、时钟时间、累计路径时间和计算时刻，接触区段使用同一累计路径时间轴。

`PosePath.Loop(finitePath)` 把有限路径接到累计循环时钟。循环的根变换由首尾根位姿求得，包含旋转和平移；每一轮把同一变换累积到模型根、脚跟和脚尖。接触区段也按周期投影到累计路径时间。以下是循环转身的调用示例：

```csharp
PosePath selected = PosePath.Loop(PosePath.FromClip(turnClip, turnFeet));
var request = new PosePredictionRequest(selected,
    new PoseTimeOrigin(Time: 100, PathTime: 1.1f, Speed: 2), Conditions: 91);
var feet = new FootPrediction(request, lookAhead: 1.1f);
```

片段和序列直接复用导入轨迹。混合与遮罩路径在绑定窗口时通过 `PosePredictionBatch` 批量采样足部链，再形成窗口内轨迹和接触数据。已声明动作标记沿该脚的主要来源传递：混合取最大权重来源，遮罩层依据脚链上的最大遮罩权重和层权重选择来源。其余区间按组合后的足部轨迹分析。

`SamplePose` 按需经同一个选定路径返回根、脚跟、脚尖三个模型矩阵，用于需要当次完整姿势采样精度的消费者。窗口查询复用已绑定数据和工作区。选择新路径、混合曲线或时间基准时，以新的请求绑定窗口。

`FootPrediction` 的可选 `parentPose` 参数将父骨架已经就绪的模型结果传入窗口分析与精确采样。未来接触到摆腿包络、骨盆及当前支撑的连接见 [Future Foot Placement](Future-Foot-Placement.md)。

源码：[FootMotion](../../../src/SomeEngine.Animation.Pose/FootMotion.cs)、[FootMotionBlock](../../../src/SomeEngine.Animation.Pose/FootMotionBlock.cs)、[FootMotionImporter](../../../src/SomeEngine.Animation.Pose.Importers/FootMotionImporter.cs)、[FootPrediction](../../../src/SomeEngine.Animation.Pose/FootPrediction.cs)。

选定足部轨迹与真实场景几何之间的支撑目标转换见 [Foot Support](Foot-Support.md)。

高度、速度筛选与多种足部接触意图参考 Ubisoft 的 [Fitting the World](https://media.gdcvault.com/gdc2016/Presentations/Roche_Clifford_Fitting%20the%20World.pdf)；本页的资源、时间与查询接口由当前实现定义。

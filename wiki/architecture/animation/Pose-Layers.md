# Pose Layers

`PoseLayerLeaf` 将来源姿势按骨骼遮罩叠加到基础姿势，使用共同 `AnimationLayers` 中对应层的 `Mode` 与 `Weight`。层权重通过同一个公开属性动画通道更新，姿势系统在本帧 Composition 阶段读取更新后的值。

## 遮罩资产与就绪数据

`BoneMaskAsset` 保存资源身份，`BoneMaskBlock` 持有一个只读的就绪 `BoneMask`，并编码该对象的逐骨权重。`CreateMask` 返回这个对象，消费图通过普通引用持有其使用期；Block 的复制分支共享同一份只读权重。权重顺序对应目标 `Skeleton` 的 own bones，长度等于 `BoneCount`，各值处于 `[0, 1]`。继承骨架各自的定义拥有自己的遮罩。

以下是保存、重开并取得就绪遮罩的使用例子：

```csharp
using (var asset = new BoneMaskAsset())
using (var block = new BoneMaskBlock([0, 1, 0.5f]))
{
    asset.SetBlock(block);
    await asset.SaveAsync(path);
}

BoneMask upperBody;
using (BoneMaskAsset asset = await BoneMaskAsset.OpenAsync(path))
    upperBody = (await asset.GetBlockAsync<BoneMaskBlock>()).CreateMask();
// upperBody 持有完整的就绪权重，并交给共享图使用。
```

调用方也可以通过 `new BoneMask(weights)` 直接创建就绪数据。构造函数复制输入数组，公共 `Weights` 返回只读视图。

## 逐骨合成规则

设基础姿势为 `B`，来源姿势为 `S`，骨架参考姿势为 `R`。每个骨骼的有效权重为 `AnimationLayer.Weight × sourceResult.Weight × mask[bone]`，以下记为 `w`。

| 层模式 | 平移 | 旋转 | 缩放 |
| --- | --- | --- | --- |
| Override | `Lerp(B.T, S.T, w)` | 对齐四元数半球后进行归一化线性插值 | `Lerp(B.S, S.S, w)` |
| Additive | `B.T + (S.T - R.T) × w` | `Normalize(Slerp(identity, S.R × inverse(R.R), w) × B.R)` | `B.S × Lerp(one, S.S / R.S, w)` |

基础、来源和参考旋转均使用归一化四元数，参考缩放各分量为正值。计算以 `System.Numerics` 四元数乘法表示，参考姿势提供来源差量的基准。

层结果继承基础结果的 `Time`、`Phase`、`Starts` 与当帧根运动，局部姿势按上述规则合成。结果自身的贡献权重使用层节点的图权重；当基础结果贡献为零时输出贡献为零。操作层权重已经参与逐骨合成，后续 Blend 可以按结果贡献继续组合。层权重为零时，结果保留基础姿势及其移动。

## 共同权重与公共图

以下是用属性动画驱动上身层权重的使用例子。结果 `0` 是基础片段，结果 `1` 是叠加片段，目标最终输出选择结果 `2`：

```csharp
var layers = new AnimationLayers { Count = 2 };
layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 0 };
world.Add(target, layers);

Channel<float> upperWeight = Channel<float>.Of<AnimationLayers>(
    static (ref AnimationLayers value) => ref value.Items[1].Weight);
var fade = Spec.Tween<float, FloatOps, Linear>(1);
PropertyAnimation.To(world, target, upperWeight, 1f, fade);

using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(target, new(baseClip, new(0), Loop: true));
    builder.Leaf<ClipParameters, ClipLeaf>(target, new(upperClip, new(1), Loop: true));
    builder.LayerPose(target, new PoseLayerParameters(new(0), new(1), new(2), upperBody), layer: 1);
}
builder.Play();
AnimationSystems.Add(systems, fade);
PoseAnimation.AddSystems(systems, nodes: static nodes => nodes.Add(new PoseLayerSystem()));
```

来源片段的图权重用于控制它对层的贡献，操作节点的 placement 选择 `AnimationLayers` 中的模式与层权重。Additive 使用相同入口，将对应 `AnimationLayer.Mode` 设为 `LayerMode.Additive`。

## 混合节点的依赖顺序

Layer、Blend 与 Warp 在安装时登记已解析的输入输出，`Complete` 将同一安装图的有向无环关系编译成依赖层级。每帧按层级运行具体系统，各系统批量完成当前层的计算并发布结果，消费者随后读取本帧来源。最终 `PoseCompositionSystem` 写入骨架实例并计算模型姿势。

以下是混合节点系统的登记例子，图内结果引用决定求值层级：

```csharp
PoseAnimation.AddSystems(systems, nodes: static nodes =>
{
    nodes.Add(new MotionWarpSystem());
    nodes.Add(new PoseLayerSystem());
    nodes.Add(new PoseBlendSystem());
});
```

`PoseLayerSystem` 的具体 Job 读取基础数组、来源数组、参考骨架及就绪遮罩，并声明自身输出数组的写入。每个安装实例持有独立的连续工作区。来源引用、遮罩与依赖层级沿安装期建立；逐帧工作集中于活动状态、权重和骨骼数值运算。用户节点通过 [Pose Graphs](Pose-Graphs.md) 的 `PoseNodeContext` 接入同一执行顺序。

源码：[遮罩](../../../src/SomeEngine.Animation.Pose/BoneMask.cs)、[Leaf](../../../src/SomeEngine.Animation.Pose/PoseLayer.cs)、[系统](../../../src/SomeEngine.Animation.Pose/PoseLayerSystem.cs)、[依赖连接](../../../src/SomeEngine.Animation.Pose/PoseNodes.cs)。公共行为入口：[PoseLayerBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/PoseLayerBehaviorTests.cs)。

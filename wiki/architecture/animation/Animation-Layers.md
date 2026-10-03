# Animation Layers

## Definition
动画层是按顺序叠加的合成单位，每层有模式、权重和遮罩。属性动画与姿势动画使用同一套合成规则。

```csharp
public struct AnimationLayers : IComponent { public LayerArray Items; public byte Count; }
[InlineArray(8)] public struct LayerArray { private AnimationLayer _item; }
public struct AnimationLayer { public LayerMode Mode; public float Weight; public LayerMask Mask; }
public enum LayerMode : byte { Override, Additive }
```

```text
result = 基准值
for 每个层（按顺序）:
    Override: result = Lerp(result, 层的值, Weight × Mask)
    Additive: result = result + (层的值 − 层的参考值) × Weight × Mask
```

## Invariants
- 层定义存于目标实体上的定长组件，因此层权重可以作为 `Channel<float>` 被动画：淡入淡出一层，就是动画它的权重。
- 遮罩：属性动画是通道集合；姿势动画是每根骨骼的权重表（引用资产数据）。
- 某个值只有一个来源写入时走快速路径，不执行合成。
- 状态机的交叉淡入在图内部用两个分支加权实现；作用于整个目标的叠加效果（悬停、上半身覆盖、呼吸、IK 修正）使用层。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/animation/Pose-Animation|Pose Animation]] · [[architecture/animation/Animation-Graph|Animation Graph]]

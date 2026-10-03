# Animation Graph

## Definition
动画图描述时间上的组合与混合关系，编译为"图数据 + 实例状态"。能展开成一条时间线的部分融合进同一个实体，以减少实体数量和指针追逐。

```csharp
using var g = new AnimationBuilder(world);
using (g.Sequence())
{
    g.To(panel, OffsetChannels.Y, 0f, Spec.Tween(0.3f));
    g.Delay(0.1f);
    using (g.Parallel())
    {
        g.To(title, SurfaceChannels.Fill, accent, Spec.Tween(0.2f));
        g.To(icon, TransformChannels.Scale, 1f, Spec.Spring());
    }
}
Entity animation = g.Play();
```

## Invariants
- 构建器使用 `using` 作用域和池化内存，不使用 lambda。
- 内置节点：`Sequence`、`Parallel`、`Stagger`、`Delay`、`Repeat`、`Blend1D`、`Blend2D`、`StateMachine`、`Layer`。用户可以用构建器原语（`Track`、`Delay`、`Boundary`）写成普通函数作为自己的节点。
- 编译规则：
  1. 固定时长的节点展开为同一条时间线上的绝对区间。
  2. 开放时长的节点（弹簧、外部推进、状态机条件）成为阶段边界，仍留在同一个实体中。
  3. 只有以下三种情况才拆成多个实体：
     - 用户显式要求某个分支有独立句柄（`Branch`）；
     - 想内联到的目标正在运行另一张图，且新图不是替换它；
     - 轨道数超出容量阈值。
- 放置：所有输出都写同一个实体时，内联在该实体上；否则使用池化的融合实体，完成后禁用而不销毁。
- 图数据的归属：
  - 运行时构建的图，图数据属于该实例；
  - 来自资产的图，在内容构建时编译一次，由多个实例共享，实例上只保存时间线、参数与状态机状态。
- 图参数（混合坐标、状态机条件）存于实例组件 `GraphParameters`，唯一写入方是驱动该图的游戏或界面代码。

## Relations
[[architecture/animation/Timeline|Timeline]] · [[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/animation/Property-Animation|Property Animation]]

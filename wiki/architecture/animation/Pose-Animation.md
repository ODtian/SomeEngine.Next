# Pose Animation

## Definition
姿势动画以整套骨骼姿势为数据单位，是引擎动画子系统的数据形态。它与属性动画共用时间线、层与混合规则、图的构建与融合；骨架、压缩片段、姿势缓冲、按骨骼遮罩混合、IK 与物理控制、根运动和蒙皮输出由它自己负责。

## Current design
- 状态：规格已确定，待实现。规格见 `docs/Pose-Animation-Spec.md`。
- 混合使用 `IValueOps<T>` 的批量版本，按骨骼数组做 SIMD。
- 蒙皮输出作为一种 Deform 实现接入 cluster 渲染（[[architecture/cluster/Deform-Cache|Deform Cache]]）。
- 片段压缩是可替换的算法接口：导入时的 `IAnimationClipEncoder` 与运行时按编码器标识解析为函数指针的解码器，由项目通过 DI 提供（与 `IClusterVertexEncoder` 相同方式）；默认实现参考 ACL。
- 骨骼不是实体；挂点按需成为实体。最终姿势存于世界级姿势存储，实例以 `PoseRange` 引用，唯一写入方是姿势求值系统。
- 读取口子：`PoseAnimation.ModelPose(world, entity)` 提供只读模型空间姿势及版本。
- 根运动输出 `RootMotionDelta`，由移动系统决定如何应用。

## Non-goals
- 不把每根骨骼表示为一条属性动画轨道。
- 推迟：物理控制与需要物理查询的 IK、骨架间重定向、可编辑的图资产、基于姿势的游戏功能。

## Relations
[[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/animation/Animation-Graph|Animation Graph]]

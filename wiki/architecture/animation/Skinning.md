# Skinning

本文记录现行的骨骼提取、上传和用户顶点求值接口。源码入口为
[`MeshSkin`、`SkinRenderExtractor`](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、
[`SkinningFeature`](../../../src/SomeEngine.Render.Features/Skinning/SkinningFeature.cs)、
[`SkinInstanceProducer`](../../../src/SomeEngine.Render.Features/Skinning/SkinInstanceProducer.cs)
和 [`skin_pose.slang`](../../../assets/Shaders/skin_pose.slang)。

## 数据路径

`MeshSkin` 将一个网格的 `SkinBinding` 绑定到 `SkeletonInstance`。`SkinRenderExtractor`
在既有提取读取窗口中复制完成的 CPU 模型空间 G，或复制 [GPU 片段](GPU-Clips.md)的采样控制。
Rendering world 的网格组件保存 source joint 到区段实体及区段内骨骼的映射。

提取按 `ModelPose.GetSection().Instance` 识别产生矩阵的实例。共享父实例的网格引用同一份
区段，父关系和 joint 映射在安装或重绑时解析。`SkinningFeature` 按版本上传改变的 CPU G，
并在 GPU 插值片段、传播层级 G。GPU 历史按每个区段的新输入推进；首次出现的区段以当前矩阵
初始化上一份矩阵，重复渲染同一输入保持原有矩阵对。

提取直接从同一 `QueryChunkView` 读取 `MeshSkin` 与 `GpuPoseTarget` 列，来源成员在完整
结构协调时核对。网格的 joint 映射与 distinct 依赖区段一起保存在绑定持有者中，普通帧按
依赖区段标记模型来源。目标持有者保存已发布的区段映射，实际重绑时更新 Rendering 组件。
已有区段的逐帧值更新通过 `IRenderExtractionSystem.CanApplyChangesDirect` 进入共同值更新
边界；创建、移除及重绑沿原有结构事务发布，组件 hook 仍由共同提取调度选择事务路径。

渲染准备从已声明的 chunk 列读取区段和网格绑定，再按同一成员列写出 `RenderPoseSection`。
网格绑定的成员版本由现有查询持有；实际成员变化时协调集合，普通帧按
`RenderMeshSkin` 的 chunk 版本更新已有绑定。
`PrepareEvaluations` 在这个阶段确定当前输入、模型版本和采样身份；帧资源更新直接使用
同一区段持有的结果来生成 GPU 控制上传。

渲染准备阶段生成每个网格的 `RenderSkinBinding`，其中两个 uint 分别是 joint 映射起点
和逆绑定矩阵起点。`SkinInstanceProducer` 将它写入普通实例属性存储，与 Cluster 的
空间、几何及材质属性组合。静态网格使用零值。

`SkinningFeature` 在装配变化时将 joint 索引和逆绑定矩阵上传到持久资源。每帧输出当前 G
和上一份 G，持久图通过四个 `GraphBufferInput` 绑定它们。用户 shader 的读取依赖于
`CompletePass`，资源使用期由帧资源与 GPU 完成状态共同管理。

当前/上一份模型与局部采样历史、播放状态、blend 状态和 XPBD 点状态由同一个
`PoseHistoryBuffers` 保存五个持久缓冲。定义复制持有迁移前后两组，上传 pass 提交成功后
将新组接受为历史，并把旧组加入退休集合；后续图编辑沿同一资源枚举移除旧组。
各区段的样本身份、时间和输入仍在 store pass 提交成功后接受。

## 应用接入

`ApplicationScene.Initialize` 在网格资产加载完成后提供 Game world、`GameFrameSystems`
和 `SceneEdit`。`UpdateFrame` 在该帧的游戏系统、动画求值和提取之前运行。
`Application.RunAsync(args, application)` 与 `SceneScript(application, ...)` 共用这些回调。
应用的动画阶段独立于界面开关。

`ApplicationScene.LodThreshold` 设置公开的像素误差阈值。
`FrameRendered` 在该帧 GPU 完成后接收 `ClusterFrameMetrics`；应用提供这一回调时启用帧计数读回与同步完成。

以下是接入示例，`skeleton` 和 `binding` 表示调用者已加载的资产，`instance` 的使用期覆盖应用运行：

```csharp
var application = new ApplicationScene(contentDirectory)
{
    Initialize = frame =>
    {
        PoseAnimation.AddSystems(frame.Systems.Animation);
        Entity target = PoseAnimation.CreateTarget(frame.World, instance);
        graph.Play(frame.World, target);
        frame.World.Add(frame.Scene.MeshEntities[0], new MeshSkin(instance, binding));
    },
};
await Application.RunAsync(args, application);
```

自定义应用组合 `ClusterRenderFeature.InstanceLayout` 与 `SkinInstanceProducer.Layout`，
在网格提取中注册 `SkinRenderExtractor`，再将 `SkinningFeature` 注册为渲染准备系统、
帧系统和 `IGraphResourceProvider`。普通 Runtime 已完成这一组合。

## 用户顶点 shader

`skin_binding.slang` 提供共同的实例绑定读取；`skin_pose.slang` 引入该绑定并提供模型骨骼、
joint 映射与逆绑定矩阵。选择预计算仿射乘积的材质引入 `skin_joint_transforms.slang`。
这些头文件提供以下读取函数：

| 接口 | 输入 | 结果 |
| --- | --- | --- |
| `LoadSkinBinding` | Cluster instance ID | joint 与逆绑定矩阵的起点 |
| `LoadSkinModelTransform` | binding、source joint、previous 标志 | 选定帧的模型空间 G |
| `LoadSkinTransform` | binding、source joint、previous 标志 | `InverseBind[joint] * G` |
| `LoadSkinJointTransform` | binding、source joint、previous 标志 | 变形边界阶段已计算的 `InverseBind[joint] * G` 仿射矩阵 |

`LoadSkinJointTransform` 适用于参与 `SkinBoundsFeature` 的蒙皮实例；这些实例具有
`RenderSkinBounds`。该 feature 通过 `IGraphResourceProvider` 暴露现有 `SkinJointTransforms`
字节资源，读取依赖同一关节计算 pass。`binding.x + sourceJoint` 选择每个绑定的 112 字节行，
当前与上一份 XYZ 仿射列分别位于行内偏移 0 与 48。布局与计算持有关系见
[Deformed Bounds](Deformed-Bounds.md)。Runtime 已将该 provider 加入共同材质资源组；
自行组合的渲染应用可按同一方式注册。材质选择这一读取函数来复用已有乘积，
`LoadSkinModelTransform` 与 `LoadSkinTransform` 继续提供直接读取 G 和求乘积的接口。

引擎的行向量约定对应 `mul(float4(position, 1), skinTransform)`。用户材质读取自己编码的
joint 与权重，选择影响数，并计算位置、法线和切线。上一帧求值使用
`VertexEvalContext.IsPreviousFrame` 选择上一帧 G。

`cluster_material_entries.slang` 与 `cluster_tessellation_entries.slang` 的
`CLUSTER_VERTEX_ENCODING` 宏声明材质接受的完整顶点编码标识。默认值对应标准位置与属性。
自定义编码可在包含这些入口之前替换该宏；Cluster 继续按同一顶点求值接口执行用户材质。
使用 Deform Cache 时，`CLUSTER_DEFORM_VERTEX_ENCODING` 应声明同一输入编码。

## Inline 与缓存选择

同一个用户求值器可实现 `IVertexDeformCache`：`evaluate` 读取自己的 joint/权重及
`LoadSkinBinding(ctx.instanceID)`，`writeCache` 和 `readCache` 解释用户选择的字节编码。
inline 材质直接把该类型作为 `VertexEvaluate` 与 `RasterVertexEvaluate`；缓存材质把
该类型声明为 `DeformEvaluate`，两个消费入口选择 `CachedVertexEval<DeformEvaluate>`，
并引入 `cluster_deform.slang`。完整接口见 [Deform Cache](../cluster/Deform-Cache.md)。

缓存写入为每个可见 cluster 分别构造当前与上一帧 `VertexEvalContext`，携带各自的
实例 ID、世界变换和 `IsPreviousFrame`。用户求值选择相同模型历史对应的 G、joint 映射与
逆绑定矩阵，或读取已计算的 joint 仿射乘积；材质绑定通过既有 feature resource binder 提供给变形计算入口。
光栅、着色和运动向量随后消费保存的两份结果。

[`SkinCacheRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SkinCacheRenderingTests.cs)
提供一个具体材质例子：每顶点 128 字节保存当前与上一份 `StandardVertex`，16 字节对齐。
四个面板显示两个独立角色各自的当前/上一姿势，两种材质共用同一求值代码，角色使用
不同播放速率和逆绑定矩阵。缓存与 inline 通过同一公开应用入口渲染。

`ApplicationScene.FrameMeasured` 接收完成后的 CPU 与 GPU 整帧时间；阶段类别由
`ClusterPipelineSystem.DeformationTimeGroup`、`DeformationEvaluationTimeGroup` 和
`RasterTimeGroup` 给出。边界与采集方法见 [GPU Timing](../render-graph/GPU-Timing.md)。

应用图像示例位于
[`SkinningRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SkinningRenderingTests.cs)。
这个示例材质选择每顶点两个影响，并分别演示 float 权重和两个 16 位定点权重的编码。
它将用户记录附加在标准表面属性后，由材质读取自己的尾部位置标记。源影响记录保持在
`ClusterSkinInfluencesBlock`，可供其他编码选择使用。

## 变形范围

配套 LBS 通过 [Deformed Bounds](Deformed-Bounds.md) 的关节影响盒生成每个姿势对应的
实例、BVH 和 cluster 边界。`MeshSource.BoundsExpansion` / `MeshInstance.BoundsExpansion`
继续表达调用者声明的额外有限偏移，并参与 Cluster 可见性判断。

相关主题：[Skeleton Inheritance](Skeleton-Inheritance.md)、[Pose Graphs](Pose-Graphs.md)、
[Deform Cache](../cluster/Deform-Cache.md)。

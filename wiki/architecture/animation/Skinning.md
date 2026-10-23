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

渲染准备阶段生成每个网格的 `RenderSkinBinding`，其中两个 uint 分别是 joint 映射起点
和逆绑定矩阵起点。`SkinInstanceProducer` 将它写入普通实例属性存储，与 Cluster 的
空间、几何及材质属性组合。静态网格使用零值。

`SkinningFeature` 在装配变化时将 joint 索引和逆绑定矩阵上传到持久资源。每帧输出当前 G
和上一份 G，持久图通过四个 `GraphBufferInput` 绑定它们。用户 shader 的读取依赖于
`CompletePass`，资源使用期由帧资源与 GPU 完成状态共同管理。

## 应用接入

`ApplicationScene.Initialize` 在网格资产加载完成后提供 Game world、`GameFrameSystems`
和 `SceneEdit`。`UpdateFrame` 在该帧的游戏系统、动画求值和提取之前运行。
`Application.RunAsync(args, application)` 与 `SceneScript(application, ...)` 共用这些回调。
应用的动画阶段独立于界面开关。

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

`skin_pose.slang` 提供四个 buffer 和三个读取函数：

| 接口 | 输入 | 结果 |
| --- | --- | --- |
| `LoadSkinBinding` | Cluster instance ID | joint 与逆绑定矩阵的起点 |
| `LoadSkinModelTransform` | binding、source joint、previous 标志 | 选定帧的模型空间 G |
| `LoadSkinTransform` | binding、source joint、previous 标志 | `InverseBind[joint] * G` |

引擎的行向量约定对应 `mul(float4(position, 1), skinTransform)`。用户材质读取自己编码的
joint 与权重，选择影响数，并计算位置、法线和切线。上一帧求值使用
`VertexEvalContext.IsPreviousFrame` 选择上一帧 G。

`cluster_material_entries.slang` 与 `cluster_tessellation_entries.slang` 的
`CLUSTER_VERTEX_ENCODING` 宏声明材质接受的完整顶点编码标识。默认值对应标准位置与属性。
自定义编码可在包含这些入口之前替换该宏；Cluster 继续按同一顶点求值接口执行用户材质。
使用 Deform Cache 时，`CLUSTER_DEFORM_VERTEX_ENCODING` 应声明同一输入编码。

应用图像示例位于
[`SkinningRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/SkinningRenderingTests.cs)。
这个示例材质选择每顶点两个影响，并分别演示 float 权重和两个 16 位定点权重的编码。
它将用户记录附加在标准表面属性后，由材质读取自己的尾部位置标记。源影响记录保持在
`ClusterSkinInfluencesBlock`，可供其他编码选择使用。

## 变形范围

网格的 `MeshSource.BoundsExpansion` / `MeshInstance.BoundsExpansion` 声明动画允许的
有限偏移范围，参与既有 Cluster 可见性判断。调用者根据选定片段、绑定矩阵与用户变形
确定该值。图像示例选择 3 单位扩展，覆盖示例片段的一秒平移以及两套绑定偏移。

相关主题：[Skeleton Inheritance](Skeleton-Inheritance.md)、[Pose Graphs](Pose-Graphs.md)、
[Deform Cache](../cluster/Deform-Cache.md)。

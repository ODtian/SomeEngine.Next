# GPU XPBD Secondary Chains

本页记录现行 GPU 次级骨骼链的配置、世界空间积分与姿势输出。源码入口为 [GpuXpbdPose](../../../src/SomeEngine.Animation.Pose/GpuXpbdPose.cs)、[共同来源链](../../../src/SomeEngine.Animation.Pose/GpuPoseRig.cs)、[正常提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[已解析定义与输入](../../../src/SomeEngine.Render.Features/Skinning/GpuXpbdTables.cs)、[GPU 状态及迁移](../../../src/SomeEngine.Render.Features/Skinning/SkinningFeature.cs) 和 [SimulateXpbdChain](../../../assets/Shaders/gpu_pose.slang)。

约束投影采用 Macklin、Müller、Chentanez 的 [XPBD (2016)](https://mmacklin.com/xpbd.pdf)，对应 Algorithm 1 与公式 17–18。骨链约束、坐标输入与 G 重建按下面的具体实现组织。

## 定义与实例

`GpuXpbdPose` 连接一个 `GpuPoseSource` 和共享 `GpuXpbdChain`。骨列表按直接父子顺序排列，使用该 Skeleton 的自身骨编号，各段具有正长度。第一点跟随当前来源骨骼，构造时将它的逆质量设为零；其余 `GpuXpbdBone.InverseMass` 决定约束修正的分配，零逆质量的点跟随各自来源位置。链的动态骨骼及其 GPU 后代由该具体步骤写入，多条链可以沿共同来源链组合。

| 参数 | 含义 |
| --- | --- |
| `LengthCompliance` | 长度顺应度；零值对应长度等式 |
| `BendCompliance` | 余弦弯曲顺应度；零值对应最大角的硬限制 |
| `MaximumBend` | 相邻两条出向线段之间的最大角，单位为弧度 |
| `Acceleration` | 世界空间加速度，单位为世界距离/游戏秒² |
| `Damping` | 速度指数衰减率，单位为每游戏秒 |
| `Step` | 正的最大积分子步时长，单位为游戏秒 |
| `Iterations` | 每个子步的约束迭代次数 |

共享定义保存骨索引、参考局部段向量、逆质量和设置。安装及显式来源/绑定变化时，它们解析为当前区段、后代范围和独立点状态范围。参考段向量取下一骨的 `ReferencePose.Translation`，通过当前来源父 G 和 actor 变换取得世界长度，来源与 actor 的缩放共同参与目标长度。

移动阶段写入 `GpuPoseTarget.ModelToWorld`，表示共同模型空间到世界空间的当前变换。`CreateTarget` 将其初始化为单位矩阵。已有 `ModelOffset` 继续提供可选平移步骤；`ModelToWorld` 表达 actor 的旋转、位置及缩放，经同一 Game-world 提取窗口交付 Rendering world。

每个安装实例在 GPU 连续范围内持续保存世界空间点位置和速度。当帧工作区保存来源 G、子步旧位置、参考长度及约束乘子。新安装和重播从当前来源 G 初始化位置，初速度为零；随后保留世界运动，当前父 G 经 actor 变换形成运动学锚点，子步之间线性接续锚点位置。actor 或父骨骼移动时，锚点通过约束带动动态点；当前逆 actor 矩阵将结果还原到模型空间，因而 actor 平移、旋转及加速相对于链的惯性保持一致。

## 子步与约束

当前求值结果的计算时间差分成 `ceil(delta/Step)` 个等长子步，每步时长为 `h`。它包含当次父 G 的依赖时间，来源采样时刻沿共同来源元数据交付。动态点预测为 `x + h*exp(-Damping*h)*v + h²*Acceleration`。每步清零乘子，随后依配置次数先投影全部长度约束，再投影全部弯曲约束，乘子在这些迭代间累积。

约束值 `C`、梯度 `g_i`、逆质量 `w_i` 和顺应度 `a` 给出：

```text
aTilde = a / h²
deltaLambda = (-C - aTilde*lambda) / (sum(w_i*dot(g_i,g_i)) + aTilde)
x_i += w_i * g_i * deltaLambda
lambda += deltaLambda
```

长度约束为 `C=length(x_b-x_a)-referenceLength`，两端梯度为负、正单位段方向。弯曲采用 `C=dot(u,v)-cos(MaximumBend) >= 0`。两段单位方向为 `u,v`，长度为 `l0,l1` 时，`d0=(v-dot(u,v)*u)/l0`、`d1=(u-dot(u,v)*v)/l1`，三点梯度为 `-d0,d0-d1,d1`。弯曲乘子投影到非负范围，活动约束采用上述 XPBD 更新；正顺应度允许相应约束能量下的形变。子步结束后，速度取最终位置与子步旧位置之差除以 `h`。

## 输出、时钟与历史

世界点经当前 `WorldToModel` 还原。每个骨骼从该次来源 G 出发，采用最短弧 swing 将来源段方向对齐到解出的段方向，并保留来源 frame 的扭转；末端骨采用最后一段的 swing。这是该算法的扭转约定。已解析后代接收旋转和平移，既有 CPU 范围保持其写入职责。

`Section` 的接受输入事实控制新输入推进，同次输入的多视图和重复呈现复用结果。链的安装匹配包含目标实体、SkeletonInstance、共享 Rig、骨架绑定版本、叶 `Starts` 及图安装 `AnimationStarts`，后两者的职责见 [GPU Pose Readback](GPU-Pose-Readback.md)。新安装、定义/绑定变化及重播初始化状态；区段移动时，同一 Rig 的点位置/速度随既有历史复制迁移。

最终 G 经 `StorePoseHistory` 成为下一输入的 previous。公开读回保留实际输入身份、计算时刻及共同来源采样时刻。`GpuXpbdParameters.Duration` 使用共同叶时钟，默认持续运行。

```text
当前 actor 变换 / 来源与父姿势输入
  → GpuPoseTarget / 共同 CompositionInputs
  → SkinRenderExtractor / RenderBoneSectionData
  → 当帧坐标、时间与已解析定义
  → 来源采样、FK、前置具体步骤
  → SimulateXpbdChain：世界积分、约束、模型 G 重建
  → StorePoseHistory / CompletePass
  → 变形边界 / Cluster 主视图与阴影视图 / 用户 shader
```

## 接入例子

下面是三点尾链的配置例子，骨编号与加速度由调用方按自己的资源和场景给定。能力适用于任意配置长度的直接骨链。

```csharp
var chain = new GpuXpbdChain(skeleton,
    [new(rootBone, 0), new(middleBone), new(tipBone)],
    new Vector3(0, -9.81f, 0), maximumBend: .6f,
    lengthCompliance: 0, bendCompliance: .0001f, iterations: 12);
var pose = new GpuXpbdPose(source, chain);
GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, skeletonInstance);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuXpbdParameters, GpuXpbdLeaf>(target, new(pose));
Entity animation = builder.Play();

GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
input.ModelToWorld = actorModelToWorld;
world.Replace(target, input);
```

公开应用见 [GpuXpbdRenderingTests](../../../tests/SomeEngine.Runtime.Tests/GpuXpbdRenderingTests.cs)。两条短尾链属于验证夹具；点位参考使用独立双精度数值 Jacobian，图像参考把期望模型变换烘焙为静态网格。

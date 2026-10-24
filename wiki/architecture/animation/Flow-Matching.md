# Autoregressive Flow Matching

本页记录已实现的学习式姿势模型、具体批次求值和共同图入口。源码为 [FlowMatchingModel](../../../src/SomeEngine.Animation.Pose/FlowMatchingModel.cs)、[FlowMatchingBlock](../../../src/SomeEngine.Animation.Pose/FlowMatchingBlock.cs)、[逐实例状态](../../../src/SomeEngine.Animation.Pose/FlowMatchingState.cs)、[具体矩阵批次](../../../src/SomeEngine.Animation.Pose/FlowMatchingBatch.cs)、[Leaf](../../../src/SomeEngine.Animation.Pose/FlowMatchingLeaf.cs) 与 [System](../../../src/SomeEngine.Animation.Pose/FlowMatchingSystem.cs)。

数学依据为 Control Operators 作者的 [训练与条件流](https://github.com/gouruiyu/ControlOperators/blob/main/train.py#L770-L886)、[控制器推理和姿势解码](https://github.com/gouruiyu/ControlOperators/blob/main/controller.py#L924-L964)、[不同网络表示](https://github.com/gouruiyu/ControlOperators/blob/main/networks.py) 及 [有序控制组合](https://github.com/gouruiyu/ControlOperators/blob/main/control_operators.py#L54-L65)。本实现独立编写，并选定作者区分的 ELU MLP 表示。

## 资产与 ready 模型

`AnimationModelAsset` 持有模型资产身份与 Block 目录。`FlowMatchingBlock` 保存 Skeleton 资产引用及具体模型导出数据；`CreateModel()` 返回同步可读的 `FlowMatchingModel`。已经取得的模型以普通引用保持网络参数有效，多个角色共享这一份只读定义。

```csharp
using AnimationModelAsset asset = await AnimationModelAsset.OpenAsync(path);
FlowMatchingModel model = (await asset.GetBlockAsync<FlowMatchingBlock>()).CreateModel();
```

`FlowMatchingModel.ReadJson(bytes)` 读取同一套版本 1 导出格式，可用于将外部训练结果放入 `FlowMatchingBlock`。格式包含如下数据。

| 字段 | 含义 |
| --- | --- |
| `boneCount`、`frameTime`、`steps` | 模型骨骼数、训练帧间隔、固定 Euler 步数 |
| `controls` | 有序 product 的字段名、编码种类及固定元素数 |
| `controlOffset`、`controlScale` | 控制编码的逐维归一化 |
| `latentOffset`、`latentScale` | decoder 输入前的 latent 反归一化 |
| `latentDeviation`、`latentMinimum`、`latentMaximum` | 初始噪声和速度缩放、积分结果范围 |
| `initialHistory` | 默认的归一化上一帧 latent |
| `poseOffset`、`poseScale`、`poseDeviation` | decoder 输出的反归一化 |
| `flow`、`decoder` | 两个具体 MLP 的 `widths`、逐层 `weights` 与 `biases` |

每个网络的隐藏层为 Linear → ELU(alpha=1)，末层为 Linear。每层权重按输出行排列，一行连续保存输入维度，偏置每个输出一项；计算采用 float32。网络拓扑、激活、归一化与解码数据随同一个模型导出。

## 输入与自回归推进

实例从共享初始历史建立自己的 previous latent、控制、噪声、生成姿势、根平移和根旋转。重新开始时，叶的实际 `Starts` 重建这些状态；`InitialHistory` 参数可以提供调用方选择的初始历史。

控制字段沿导出顺序连接。`FlowControlField.Offset`、`Width` 给出其编码范围，Vector3 按 XYZ 排列；Rotation6D 按两条矩阵轴的交错分量排列。固定数组依次展开元素。`GraphParameters` 从 `ControlParameter` 开始保存同一编码后的浮点值，使用公共参数数组的 16 项容量。

每次正 Delta 更新执行一次自回归模型步。调用方采用模型 `FrameTime` 对应的固定更新频率；根速度使用该次 Delta 积分。默认噪声由逐实例种子产生；指定 `NoiseParameter` 后，从图参数中的该范围读取调用方提供的标准噪声。这让相同历史、控制、噪声与步数形成可复现的求值输入。

设上一帧归一化 latent 为 `p`，标准噪声为 `n`，导出偏差为 `d`，积分步数为 `S`。初值为 `z = d * n`。第 `s` 步将 `[z, p, encodedControl, s/S]` 送入 flow 网络，并执行 `z += d * velocity / S`。完成后按导出范围 clamp，成为下一帧 previous latent。

decoder 输入为 `z * latentScale + latentOffset`；其输出依次乘 `poseDeviation`、`poseScale`，再加 `poseOffset`。这些顺序与独立训练、参考计算共用同一导出定义。

## 姿势与根运动解码

模型的 bone0 是 simulation root，bone1 是 hips。设后者及其它局部骨骼共 `N = boneCount - 1`，decoder 输出按以下顺序排列：根线速度 3、根角速度 3、hip 位置 3、每个非 simulation 骨骼的旋转轴 6、hip 速度 3、每个非 simulation 骨骼的角速度 3、两个接触值。完整向量长度为 `12 + 9*N + 2`。

六维旋转按 `[x.x,y.x,x.y,y.y,x.z,y.z]` 保存两条轴；先归一化 x，再正交化并归一化 y，以叉积取得 z，转换成引擎的行向量旋转。hip 使用解码位置，其余局部平移及全部缩放来自绑定 Skeleton 的参考姿势。

根速度先由当前根朝向旋转，再积分到实例自己的根位置和朝向。默认 `RootMotionMode.SourcePose` 将累计根写入局部 bone0。`Extract` 将参考根交付给姿势结果，并通过 `PoseResult.RootMotion` 返回本次根运动增量，供移动系统乘在前次角色变换之前。实例的 `PoseFeatures` 提供已解码的完整特征向量。

## 共同图与具体批次

以下是公共使用例子：Flow 产生结果 0，既有 PoseBlend 消费它并形成结果 1。

```csharp
Entity target = PoseAnimation.CreateTarget(world, skeletonInstance, 2, new(1));
using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    builder.GenerateFlowPose(target, new(model, new(0)));
    builder.BlendPose(target, new(new PoseBlend([new(new(0))]),
        new(1), new(StageEnd.Settled)));
}
Entity animation = builder.Play();

PoseAnimation.AddSystems(systems, nodes: static nodes =>
{
    nodes.Add(new FlowMatchingSystem());
    nodes.Add(new PoseBlendSystem());
});
```

安装时解析输出和共同 PoseNode 依赖，并按 ready 模型登记实例；安装及释放改变具体批次。普通帧读取当前图输入、时间、活动状态和层权重。每个模型复用连续矩阵工作区，一次具体 Job 执行该模型所有当前活动角色的 Euler 与 decoder 循环；各角色分别保存历史和结果。Job 完成后写入共同 LocalPose/PoseResult，后续依赖节点继续求值，最终 Skeleton G 通过正常提取、Skinning 和 Cluster 呈现。

Flow 叶持续提交运行状态，阶段和停止沿用共同时间线。零 Delta 启动更新交付初始参考姿势，后续零 Delta 更新保持生成历史。

## 可复用的训练例子

[example.json](../../../tools/FlowMatching/example.json) 明确声明一个程序动作数据例子：三骨骼、64 段各 64 个前后帧对、0.1 秒帧间隔。六维 latent 是相位的 sine/cosine、两维平滑速度与两维平滑目标。控制为按顺序连接的 `velocity: Vector3` 与 `target: Vector3`；这两个字段对该例子的具体动作含义由生成函数定义。

[train.py](../../../tools/FlowMatching/train.py) 独立生成样本并训练 flow 与 decoder；[learned export](../../../samples/SomeEngine.Animation.FlowMatching/example.flow.json) 是该例子的实际学习资产。脚本使用声明种子、网络宽度、优化器和迭代数；训练、数据生成及参考计算均经公共计算队列执行。

[reference.py](../../../tools/FlowMatching/reference.py) 用 NumPy 独立读取该导出，执行相同输入的积分与姿势解码。公开应用例子 [FlowMatchingRenderingTests](../../../tests/SomeEngine.Runtime.Tests/FlowMatchingRenderingTests.cs) 通过共享图驱动两个角色，并与其固定数值姿势及普通 Skeleton 输入的图像比较。

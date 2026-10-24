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

实例从共享初始历史建立自己的 previous latent、控制、噪声、生成姿势、根平移和根旋转。重新开始时，叶的实际 `Starts` 重建历史、生成姿势、根变换及随机种子，当前控制输入继续保存在实例数组中；`InitialHistory` 参数可以提供调用方选择的初始历史。

控制字段沿导出顺序连接。`FlowControlField.Offset`、`Width` 给出其编码范围，Vector3 按 XYZ 排列；Rotation6D 按两条矩阵轴的交错分量排列。固定数组依次展开元素，`Encode(value, product, element)` 写入选定元素。

每个实例的 `FlowMatchingState.Controls` 是完整 `ControlCount` 宽度的连续输入；它与求值器读取的是同一数组。调用方在 Composition 前沿 `ExecuteBufferWrite<FlowMatchingInstance>` 写入。默认使用这组实例输入；显式设置 `ControlParameter` 时，求值器改从 `GraphParameters` 的该位置复制整组控制。后者是适合公共参数数组 16 项容量的小参数便利入口。

以下是例子的两个 Vector3 字段写入同一实例输入的用法；其它声明宽度和固定数组使用各自字段的 Offset、Width 和元素索引。

```csharp
var values = (Velocity: velocity, Target: target);
world.ExecuteBufferWrite<FlowMatchingInstance, (Vector3 Velocity, Vector3 Target)>(animation, ref values,
    static (instances, ref values) =>
    {
        ref FlowMatchingInstance instance = ref instances[0];
        ReadOnlySpan<FlowControlField> fields = instance.Definition.Parameters[instance.Index].Model.Controls;
        fields[0].Encode(values.Velocity, instance.State.Controls);
        fields[1].Encode(values.Target, instance.State.Controls);
    });
```

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

[reference.py](../../../tools/FlowMatching/reference.py) 用 NumPy 独立读取该导出，执行相同输入的积分与姿势解码。公开应用例子 [FlowMatchingRenderingTests](../../../tests/SomeEngine.Runtime.Tests/FlowMatchingRenderingTests.cs) 通过共享图驱动两个角色，比较独立数值姿势和实例历史；普通 Skeleton 按同一组逐帧参考输入推进，使 Cluster 图像覆盖当前与前一帧 G。

## 训练数据与导出操作

`train.py` 的 `--data` 接受 NumPy NPZ，其中 `previous`、`following` 是原始 latent 的前后帧数组，`controls` 是按声明字段顺序编码的原始控制，`poses` 是后帧对应的完整姿势特征。四个数组均为 float32，形状分别是 `[N,D]`、`[N,D]`、`[N,C]`、`[N,K]`。程序动作例子在省略 `--data` 时由 `motion_example` 和 `pose_features` 确定性生成。

配置的 `initialLatent` 提供原始初始历史，`parents` 给出 Skeleton 骨骼数；运行时绑定该顺序的 Skeleton。控制描述的总宽度为 C，`controlOffset`、`controlScale` 可显式指定，默认逐项为 0、1。flow 网络输入宽度为 `2*D+C+1`，输出宽度为 D；decoder 输入为 D，输出为本页姿势格式的 K。网络宽度、训练种子、线程数、batch、优化步数和 Euler 步数均由配置提供。

下列命令是本仓库可复用训练例子的操作，在仓库根目录执行。该例子使用 Python 3.10、torch 2.3.1+cpu、NumPy 1.26.4 和四个 CPU 线程。

```powershell
$python = 'C:\Users\boqi\AppData\Local\Programs\Python\Python310\python.exe'
$env:OMP_NUM_THREADS = '4'
$env:MKL_NUM_THREADS = '4'
$training = @('--cpu', '--session', 'flow-training-example', $python,
    'tools/FlowMatching/train.py', '--config', 'tools/FlowMatching/example.json',
    '--output', 'samples/SomeEngine.Animation.FlowMatching/example.flow.json')
& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' @training
```

训练先生成或读取明确的数据，计算 latent/pose 归一化，训练 decoder 和条件 flow，再从实际 PyTorch 网络导出 row-major 参数。保存前，[export.py](../../../tools/FlowMatching/export.py) 用完整声明维度的固定输入，将内存中的训练网络与 JSON 往返后的独立 NumPy 求值比较，容差为 0.0002。已有学习资产可以复用同一入口：

```powershell
$comparison = @('--cpu', '--session', 'flow-training-example', $python,
    'tools/FlowMatching/export.py', '--model',
    'samples/SomeEngine.Animation.FlowMatching/example.flow.json')
& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' @comparison
```

## 导入与工程模型资产

[FlowMatchingSourceImporter](../../../src/SomeEngine.Animation.Pose.Importers/FlowMatchingSourceImporter.cs) 读取 `.flow.json` 为具体 `FlowMatchingModel`，按来源 `.meta` 的选择绑定 Block Importer，并通过 `GetAssetId("model")`、`AddAsset`、`GetAssetAsync` 完成实际模型资产。输出以 `.animation-model.asset` 替换来源的最后扩展名；例如 `example.flow.json` 对应 `example.flow.animation-model.asset`。工程沿既有 `AssetProject.ImportAsync` 调用模型资产自己的 `SaveAsync`，提交来源与目录。

[FlowMatchingImporter](../../../src/SomeEngine.Animation.Pose.Importers/FlowMatchingImporter.cs) 只生产 `FlowMatchingBlock`，保存同一导出数据和 `FlowMatchingSettings.SkeletonAssetId`。项目引用动画 Importers 程序集，使用 `[AssetCapabilities]` 生成发现入口；来源集成通过能力描述选择生产者。

以下是已保存 Skeleton 资产与模型导出的导入例子。

```csharp
var services = ProjectAssets.AddAssetCapabilities(new ServiceCollection());
var importer = new FlowMatchingSourceImporter(services);
var project = new AssetProject(projectRoot, [importer]);
var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
metadata.Importers.Add(new(FlowMatchingImporter.Id, JsonSerializer.SerializeToElement(
    new FlowMatchingSettings(skeletonAssetId), FlowMatchingSettingsJson.Default.FlowMatchingSettings)));
SourceMeta.Save(exportPath, metadata);
Guid modelId = (await project.ImportAsync(exportPath, importer))[0];
using AnimationModelAsset asset = await AnimationModelAsset.OpenAsync(
    Path.Combine(projectRoot, project.Manifest.Assets[modelId].Path));
FlowMatchingModel model = (await asset.GetBlockAsync<FlowMatchingBlock>()).CreateModel();
```

取得 ready 模型后，普通引用保持其参数有效。公开 App 使用这条导入路径重开模型资产，将实例控制组合送入同一 Flow → PoseBlend → Skeleton → Skinning → Cluster 流程。

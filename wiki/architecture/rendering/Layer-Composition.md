# Layer Composition

`LayerComposition` 组织层可见性、材质着色和逐 pass 合成。它接收渲染器已有的 `RenderPipelines`、RenderGraph 和 `RenderPrepareSystems`；图持有 GPU 资源，合成器保存已提交的资源 ID、pass 描述和容量。实现入口为 [LayerComposition.cs](../../../src/SomeEngine.Render.Features/Layers/LayerComposition.cs)。材质的 `Material.Layer` 持有所属 pass、八位作者次序、合成操作和 stencil reference，沿现有 Game world → Rendering world 链路进入渲染。

## 片元与载荷

片元存储由 `LayerFragmentBlocks`、`LayerTileHeads` 和 `LayerFragmentPayloads` 构成。每块包含 12 字节块头 `{ Tile, Next, Count }` 和 `BlockSize` 条记录。每个 tile 的链头指向其最近挂入的块；全局片元下标为 `block × BlockSize + recordOffset`，同时定位着色工作与载荷。

软件光栅的各 wave 分别持有当前块和记录数。首个活跃 lane 用普通共享内存读写取得本 wave 的连续区间，再广播区间并由各 lane 写入；块写满时才执行全局块申请和挂链。入口在全部 lane 活跃时捕获 wave 的索引，像素覆盖分支继续使用这个索引。每个 wave 的末块按实际记录数保留，工作组结束时汇总指标。

硬件光栅把当前 wave 中属于同一 tile 的有效片元组成独立批次。含 `n` 个片元的批次申请 `ceil(n / BlockSize)` 个新块，块头保存实际记录数。设 `B = BlockSize`，该批次的记录区利用率为 `n / (ceil(n / B) × B)`。原子操作次数与默认块大小的利用率示例统一见[重构计划 T4](../../../reviews/VSM-Tile-Refactor-Plan-2026-11-08.md#t4-层可见性光栅)。

记录 `LayerFragment` 为 12 字节：完整 32 位深度、32 位片元身份、32 位打包数据。打包数据保存 tile 内像素下标（10 位）、pass 序号（6 位）、`DrawOrder`（8 位）和引擎可见性标志（8 位）；第 24 位保存正面标志。`LayerColor` 为另外 12 字节，六个 half 分量分别保存预乘颜色和彩色透射，通过三个 uint 字显式打包。定义和打包函数集中在 [layer_fragment_types.slang](../../../assets/Shaders/layer_fragment_types.slang)；块分配、链头更新和记录读写集中在 [layer_fragment_store.slang](../../../assets/Shaders/layer_fragment_store.slang)。

着色分类与合成共同调用 [fragment_material.slang](../../../assets/Shaders/fragment_material.slang)，沿片元身份、可见 cluster、实例材质偏移和原始三角形的局部材质编号取得全局材质地址。合成通过这个地址读取 `MaterialLayers` 的 `{ OperationIndex, StencilReference }` 和 112 字节 `LayerOperations`。多个材质共享着色分箱时，各自仍按实际材质地址取得合成值。片元存储按每记录 12 字节、每载荷 12 字节计费。

着色输入从片元身份恢复原始三角形，以记录的完整深度反投影目标位置，将三角形、目标位置及导数输入交给用户 `Domain.reconstruct`。用户 Domain 负责曲面反算；不透明与层着色共用这条流程，再调用同一份材质 `evaluateSurface`。

## 阶段与提交

`ConfigureVisibility` 注册全局几何分箱与层可见性阶段，`ConfigureShading` 注册覆盖全部 pass 的着色分类与材质着色阶段。它们的回调接收 `LayerStageResources` 中的图 ID 和 region，并返回阶段首尾 pass ID。`RecordVisibility`、`RecordShading` 通过这些已提交的资源更新当帧数据。各层 pass 保存自己的合成描述、参数与合成派发。

可见性阶段先清空块链头和八项帧计数器；全部可见性工作结束后开始着色；全部着色结束后，从不透明颜色和深度初始化累加器，再按注册次序执行每个 pass 的一次 `LayerComposite` 派发。`Color` 直接引用颜色累加器；深度累加器、可写不透明深度和整数 stencil 图各自持有对应状态。`LayerStencil.Export` 与附件 `Import` 通过显式图阶段交接硬件 stencil 附件。

`AddPass`、`RemovePass`、阶段重配和容量扩展各自采用一次 `BeginEdit → 局部改图 → Commit → 采用字段`。`Passes` 返回已提交描述的只读 span。调用方持有活动编辑时，RG 的单编辑限制使注册抛出 `InvalidOperationException`，描述列表和图版本保持原值。参数结构由 [layer_composition_parameters.slang](../../../assets/Shaders/layer_composition_parameters.slang) 统一声明；常量缓冲按设备对齐要求创建，CPU 写入实际数据前缀。

## 排序与三级精确路径

[layer_composite.slang](../../../assets/Shaders/layer_composite.slang) 的每个工作组处理一个 tile。它沿块链统计每像素的记录数，对当前 pass 做前缀和与计数分桶，然后按以下次序比较完整记录：

- `DepthFrontToBack`：深度升序，再按片元身份。
- `DepthBackToFront`：深度降序，再按片元身份。
- `DrawOrderThenDepth`：作者次序升序、完整深度升序、片元身份。

每像素至多 16 条时，线程在局部 uint 索引数组中做插入排序；更多记录由工作组在 shared 存储中做 bitonic 排序。比较函数统一位于 [layer_fragment_sort.slang](../../../assets/Shaders/layer_fragment_sort.slang)。

共享区容纳 1024 条 `{ fragment, payloadIndex }`，每项 16 字节；三份 1024 项 uint 数组用于计数、偏移和写入位置，总计约 28 KiB，再加少量段选择状态。tile 的当前 pass 记录全部放得下时一次处理；更多记录按连续像素区间拆成可容纳的段，计入 `SplitTiles`。段选择由一个线程更新，全部 wave 先经过同一个组屏障，再读取段范围和退出条件。

单像素超过共享容量时，从 `LayerSortScratch`、`LayerMergeScratch` 取得自己的连续区间。工作组先把每段记录在 shared 中排序，再在两个 uint 索引区间之间归并；每级通过组内设备内存同步交接。该像素最终按归并结果合成，计入 `SlowPathPixels`。scratch 各自容量为总片元容量，帧开始清空一次分配计数器；每条记录只属于一个像素和一个 pass，所有慢路径区间的总需求受这个容量约束。

## 颜色、深度与 stencil

[layer_operations.slang](../../../assets/Shaders/layer_operations.slang) 用固定 switch 解释六种颜色操作和深度、stencil、X-ray 参数。材质着色输出预乘颜色 P 与透射 T；`Over` 使用标量透射，`Transmit` 使用彩色透射；加法的颜色函数为 `P + x`，乘法为 `(P + T) × x`，`Min`、`Max` 分别夹取源颜色和背景。

`DepthFrontToBack` 按排序先后累积贡献。统一状态逐通道表示为 `F(x) = clamp(C + A × x, L, U)`：仿射操作组合贡献 C 与背景因子 A，Min/Max 依据 A 的符号收紧值域上下界。这个表示保留前景贡献，也覆盖带负分量的 HDR 乘法。`DrawOrderThenDepth` 在同一作者次序内采用相同的近到远合成，进入下一个作者次序时先把上一组结果写入当前颜色，再开始新组；颜色终止状态随组切换重置。`DepthBackToFront` 按排序结果逐项覆盖当前颜色。

`LayerOperation.ColorWriteMask` 使用 RHI 的通道掩码。每次颜色操作只更新选定通道的颜色和函数状态，stencil 与深度继续按该操作执行。该字段占用 112 字节 GPU 操作行原有的一个填充字。

颜色已经吸收后续贡献时，后面的记录继续执行按序的深度和正反面 stencil 操作，颜色载荷读取在该像素结束。深度测试与写入可选择深度累加器或可写不透明深度；stencil 使用 RHI 的比较函数、掩码和三种测试结果对应操作。X-ray 根据片元与当前不透明深度的关系选择两组颜色乘数。每个像素的最终累加器状态只写回一次。

## 容量反馈与行为入口

公共 `LayerCompositionMetrics` 保存读回来源 `FrameIdentity`、记录总数 `Fragments`、整帧像素峰值 `MaxFragmentsPerPixel`、`BlocksUsed`、`FragmentOverflow`、`SplitTiles` 和 `SlowPathPixels`。GPU 计数器还记录块分配尝试、scratch 分配位置及来源块容量；后者使读回按其实际来源容量解释。

DefaultGame 的八类效果在 `CreateFrameFeatures` 中注册实际层 pass，再把该 pass ID 和操作值赋给已创建材质的 `Material.Layer`。`Measure-ClusterEffects.ps1` 保留各效果与场景的模拟帧窗口，每次通过统一三场景入口测量。`BlendMeasurementFeature` 对可见性、着色分类、用户材质着色和排序合成四组实际 pass 分别计时，并单列不透明深度页请求、不透明材质着色和独立贴花分箱。时间戳与同帧层计数器一起读回，通过公开的 `LayerCompositionMetrics.Read` 解释计数。逐帧结果保存记录字节、载荷字节、块池使用峰值和容量，以及 `SplitTiles`、`SlowPathPixels`；片元存储分项按块、载荷、tile 链头和计数器的实际图描述统计。其余排序工作缓冲、纹理、材质参数与几何驻留继续各自计费。

`Record` 把当前帧身份写入读回 tag；`OnReadback` 保存测量值。合成器注册自己的准备系统，在 `TryOpenNextFrame` 收到完成反馈之后、`BeginFrame(next)` 之前运行。来源容量匹配当前容量，且溢出或块使用超过 3/4 时，准备系统提交一次图编辑，同时扩大块池、载荷和两份排序 scratch，提交成功后采用新容量。目标保留一倍需求余量。 tile 列表的诊断接收方同样由合成器持有；准备阶段保持 Cells、Indices 与 Diagnostics 的图 ID，扩大索引和按阶段的成员缓冲，重建对应前缀扫描，提交成功后采用 `TileListCapacity`。帧资源数 F 为 2 的行为用例覆盖来源帧 N 和 N+F 的一次扩容及完整输出；实际完成收集允许反馈更早送达。

[LayerCompositionTests.cs](../../../tests/SomeEngine.Render.Tests/LayerCompositionTests.cs) 通过公开注册、准备系统、图执行、颜色输出和诊断验证：活动编辑失败保持提交状态、新 pass 一次提交后贡献颜色、半容量反馈扩容、64 层共享路径、同 cluster 三角形交错、4096 层单像素合成、16/32 tile 的八层分段、作者次序与同次序深度语义、反向排序中的不透明层，以及负/HDR 背景上的混合操作。[LayerFragmentStoreTests.cs](../../../tests/SomeEngine.Render.Tests/LayerFragmentStoreTests.cs) 通过公开指标和输出颜色验证跨 tile 的片元贡献。[LayerCompositionTestScene.cs](../../../tests/SomeEngine.Render.Tests/LayerCompositionTestScene.cs) 提供这些检查共用的设备、图和可见性/着色生产夹具。完整合成检查归入 `Hardware` 类别，夹具选择独立显卡；原合成算法在 RTX 3080 通过负/HDR 背景 Multiply 检查，同一检查在 WARP 超时，沿用 #128 的软件适配器范围。基础分箱与输入数据检查继续使用 WARP。

Runtime 在构造 renderer 时创建合成器与共享间接派发布局。合成器的准备系统镜像主视图为 `LayerTiles`；Cluster 按目标分组剔除并输出完整 tile 窗口。普通层可见性按实际光栅阶段分组；大窗口细分复用 Cluster 的 phase patch、UInt16 pattern 索引、逐实例 patch 索引与八条 DrawIndexed 参数。两条终端写入原始三角形身份和完整深度，着色共用材质入口。

VSM 的请求状态初始化、当帧重置和视图更新位于层可见性之前的 region。层请求先于不透明深度请求与页分配；层着色跟随不透明材质着色，合成后的颜色与 R32Float 深度交给 TAA。首次用户 feature 创建后，材质的 Layer 赋值再经过既有提取和准备流程，在首帧进入层路由。后续场景在 Game world 初始化材质时采用已注册 pass，沿正常提取更新。材质入口见 [Material-Entries](../shader/Material-Entries.md)。

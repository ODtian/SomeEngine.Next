# 渲染、加载与迭代速度重构方案（2026-11-08）

本方案覆盖本轮审查的全部结论：RHI、RenderGraph、RenderWorld 与实例数据、Cluster、tile（光照网格 / 透明 / shade bin）、VSM、shader 编译与加载、场景加载、冷启动、场景切换。

本轮变更遵循 [Raster-Execution](../wiki/architecture/cluster/Raster-Execution.md) 的基础管线能力边界：VS/PS 与 compute 覆盖多材质、不透明和细分；缺少 A64 时提供全硬件光栅和非原子直接写入两条管线 fallback。

## 怎么读这份方案

每个工作项按同一格式写：

- **现状**：已在源码里核对过的事实和位置。
- **改完后**：目标代码的样子，写到类型、字段归属、公开签名、删除清单。
- **判定**：改完的代码满足哪些条件才算完成。判定分三种：
  - `ARCH-xx`：架构检查。规则在第 1 节集中定义，全部可以自动执行。
  - `BEH-xx`：行为检查。按 [Behavior-Checks](../wiki/harness/Behavior-Checks.md) 编写，只用公共 API。
  - `PERF-xx`：性能证据。通过 `tools/Invoke-Benchmark.ps1` 和 `tools/Measure-Scenes.ps1` 提交，前后配置一致。

一个工作项的全部判定都满足才算完成。判定写不出来的改动不属于本方案。

## 0. 需要先拍板的决策

以下几项改变现有的设计约定，动手前需要确认。确认后先改对应 wiki 页，再改代码。

| 编号 | 决策 | 影响的 wiki 页 |
|---|---|---|
| D1 | 帧执行要么完整提交，要么 RenderGraph 进入 Lost 状态。非设备丢失的执行期异常视为程序错误，不再支持"部分提交后继续运行"。帧在 `Execute` 前被丢弃时，feature 的状态不受影响。 | render-graph/Range-Completions、validation/Execution-Failures、新增 render-graph/Frame-Submission |
| D2 | RHI 不再逐命令跟踪资源引用。资源 Dispose 后，native 对象在"Dispose 时刻各队列已提交值"全部完成后释放。资源被已录制但未提交的命令引用时，Dispose 属于程序错误，由 ValidationLayer 报告。 | RHI/Lifetime-Concurrency-and-Diagnostics |
| D3 | 导入阶段产出打包好的 Cluster 页（`ClusterMeshBlock`），运行时不再从 `ClusterGeometryBlock` 打包。`ClusterGeometryBlock` 保留为重新烘焙的输入。 | Assets/Block-Division、cluster/Opaque-Vertex-Data |
| D4 | 引擎 shader 只在引擎内容目录烘焙一份，应用和场景项目按 GUID 引用，不再复制到各项目的 `generated/EngineSources`。 | shader/Shader-Build、Assets/Asset-Objects |
| D5 | 不依赖逐 shader 宏配置的公共头文件改为 Slang `import` 模块，每个模块的 IR 只保存一份。 | shader/Shader-Composition、shader/Shader-Build |
| D6 | Slang 仍是唯一的反射来源（遵守 [Slang-Authority](../wiki/architecture/shader/Slang-Authority.md)）。本方案只减少运行时 Slang 工作的次数，不序列化平行的反射树。 | 无需改动 |

## 1. 架构检查（ARCH）定义

架构检查分两类实现，规则先写进 [Architecture-Checks](../wiki/harness/Architecture-Checks.md)：

- **元数据检查**：放在新文件 `tests/SomeEngine.Render.Tests/Architecture/RenderArchitectureChecks.cs`，用反射读取已编译程序集的类型、字段、方法签名和程序集引用。读取元数据不需要 `InternalsVisibleTo`，与现有 `AssemblyBoundaryTests` 做法一致。
- **源码检查**：新增 `tools/ArchitectureRules`，复用 `tools/TestRules` 的 C# 语法树做法，在 `Directory.Build.targets` 的测试入口里执行，违规时输出规则编号、文件和行列。

下文中"渲染层"指这些程序集：`SomeEngine.Render`、`SomeEngine.Render.Cluster`、`SomeEngine.Render.Cluster.Pipeline`、`SomeEngine.Render.Features`、`SomeEngine.Render.MaterialBindless`、`SomeEngine.Terrain`、`SomeEngine.Elements.Render`、`SomeEngine.Runtime`。

| 编号 | 类型 | 规则（违反即失败） |
|---|---|---|
| ARCH-01 | 元数据 | `SomeEngine.Graphics*`、`SomeEngine.RenderGraph`、渲染层中，引擎自己声明的类型、字段、属性、方法、参数名，按 PascalCase 拆词后不含 AGENTS 禁用词（Authoring Lease Cursor Plan Slot Owner Runtime Publication Admission Canonical Exact Stable Physical Logical Prepared Pending Snapshot Fingerprint Module Program Sink Contract Abi Invocation）。例外清单只含两类：外部绑定库声明的类型（Silk.NET、SlangShaderSharp 等），以及为实现外部接口而必须使用的成员名。程序集名 `SomeEngine.Runtime` 和 ECS 已有的 `QueryCursor` 不在本方案范围内，放进例外清单并注明。 |
| ARCH-02 | 元数据 | 渲染层的字段、属性、方法参数和返回值中，不出现 `BufferBoundaryState`、`TextureBoundaryState`，也不出现它们的数组、Span、List。 |
| ARCH-03 | 元数据 | 渲染层的字段和属性中，不出现 `QueueCompletion`，也不出现它的数组或集合。 |
| ARCH-04 | 源码 | 渲染层不调用 `IGraphicsBackend.CreateBuffer`、`CreateTexture`、`Map`。资源一律由 RenderGraph 创建或注册。 |
| ARCH-05 | 元数据 | `IRenderGraphFrameExecutionObserver` 只有一个方法 `void OnFrameSubmitted(in RenderGraphSubmission submission)`。`RenderGraphSubmission` 不提供 barrier 状态读取，也没有 `TryGetPassCompletion`。 |
| ARCH-06 | 元数据 | `SomeEngine.Render.Features` 不引用 `SomeEngine.Render.Cluster` 和 `SomeEngine.Render.Cluster.Pipeline`。`SomeEngine.Render.Tests` 不引用 `tools/` 下的任何项目。 |
| ARCH-07 | 元数据 | `SomeEngine.RenderGraph` 中，`FrameResources`（由 `FrameSlot` 改名而来）不含 `GraphSchedule`、`GraphEditChanges` 类型的字段，也不含这些类型的集合。`RenderGraph` 恰好有一个 `GraphSchedule` 字段。 |
| ARCH-08 | 源码 | `SomeEngine.Graphics.Direct3D12` 和 `SomeEngine.Graphics.Vulkan` 中没有 `[ThreadStatic]`，也没有可变的 `static` 字段。例外：原生库句柄和不可变常量表，逐项列在例外清单里。 |
| ARCH-09 | 源码 | `SomeEngine.Graphics.Direct3D12/Command*.cs` 和 `SomeEngine.Graphics.Vulkan/Vulkan{Commands,Rendering,Copies,Barriers,Indirect}.cs` 中没有 `throw new ArgumentException`、`ArgumentOutOfRangeException`、`ArgumentNullException`、`InvalidOperationException`、`ObjectDisposedException`。 |
| ARCH-10 | 源码 | `CreateSession(`、`CreateCompositeComponentType(`、`.Link(`、`.Specialize(`、`LoadModuleFromIRBlob(`、`LoadModuleFromSource(` 只出现在两处：`SomeEngine.Resources/ShaderLibrary*.cs`，以及 `SomeEngine.Assets/Importing/Shaders/` 下的导入器。全仓库不出现 `Lazy<Shader>`。 |
| ARCH-11 | 源码 | 渲染层不直接调用 `IGraphicsBackend.Create*Pipeline`、`Create*PipelineAsync`，只调用 `RenderPipelines` 的方法。`RenderPipelines` 内部的每次创建调用都传入非空的 `PipelineCache`。 |
| ARCH-12 | 源码 | `SomeEngine.Runtime` 中，`new RenderGraph(`、`new RenderDevice(`、`new ClusterPipelineSystem(`（以及拆分后的 Cluster 类型）、`new LightingFeature(`、`new VirtualShadowFeature(` 只出现在 `ApplicationRenderer` 的构造函数里，并且每个各出现一次。 |
| ARCH-13 | 源码 | `SomeEngine.Runtime` 不引用 `ClusterGeometryBlock`、`ClusterVertexAttributesBlock`。`ClusterMeshData` 位于 `SomeEngine.Assets.Importers`，访问级别为 `internal`。 |
| ARCH-14 | 元数据 | 渲染层字段名不以 `_pending`、`_committed`、`_staged` 开头。 |
| ARCH-15 | 源码 | `assets/Shaders/*.slang` 中的 `#include` 只能指向 [Shader-Build](../wiki/architecture/shader/Shader-Build.md) 列出的"宏配置头文件"清单。 |
| ARCH-16 | 源码 | `MapType.Read` 只出现在 `SomeEngine.RenderGraph` 中。 |
| ARCH-17 | 元数据 | `SomeEngine.RenderGraph` 中名为 `UploadPage` 的类型恰好有一个。 |
| ARCH-18 | 元数据 | `SomeEngine.Render.Cluster.Pipeline` 中没有 partial class，类型名不含 `Shadow`、`Vsm`、`Transparency`、`Decal`、`Blend`。 |
| ARCH-19 | 源码 | `SomeEngine.Render.Features/Lighting/` 中，每个 shader 资源名字符串字面量（如 `"ClusteredLightGrid"`）只出现一次，即在 `LightingResources` 表里。 |
| ARCH-20 | 元数据 | `RenderGraphFrameState` 中 Dictionary 的 key 类型（包括元组的各个成员）不含 `string`。 |
| ARCH-21 | 源码 | 渲染层不调用 `ShaderResourceLayout.Resolve` 时传入写死的名字 switch。名字到资源的映射只能来自各 feature 的资源表。 |
| ARCH-22 | 元数据 | 渲染层和 `SomeEngine.Animation.Pose` 中，ECS 组件类型（实现组件标记接口的类型）不含 `Task`、`TaskCompletionSource`、委托类型的字段，也不含引用这些类型的数组或集合。 |

PERF 判定中引用的 trace 站点（`shader.Slang.CreateSession`、`shader.Slang.LoadModule`、`cluster.MeshPageBuild` 等）目前不存在，由对应工作项用 `TraceSite` 新增。站点名在工作项合入时写进 [Engine-Trace](../wiki/architecture/Engine-Trace.md)。

## 2. 阶段总览

| 阶段 | 内容 | 依赖 | 主要收益 |
|---|---|---|---|
| A | 可观测性：场景加载分阶段计时、基线测量 | 无 | 让后续每一步都有前后数字 |
| B | 加载与迭代：shader 库、PSO 缓存、烘焙 Cluster 页、并发加载、切场景保留渲染器、内容构建、shader 热重载、import 模块 | A | 冷启动、切场景、改 shader 的等待时间 |
| C | 帧语义与状态归属：提交语义、GPU 完成跟踪、资源状态、实例数据、反馈通道 | 决策 D1 | 删掉回滚双份状态和多处 GPU 完成跟踪 |
| D | RenderGraph：共享调度、当帧 pass 分段、小项清理 | C1 | 每帧 CPU 开销，规则符合 AGENTS 的持久图要求 |
| E | RHI：检验迁移、生命周期、命令复用、录制句柄 | 决策 D2，C2 | 命令热路径开销，检验可关闭 |
| F | 渲染功能边界：视图进 RenderWorld、拆 Cluster、合并 VSM、光照资源表、透明网格 | C、D | 职责清晰、去掉双向耦合 |
| G | 命名清理 | 各阶段改到的文件随改随清，最后统一收尾 | ARCH-01 通过 |

B 阶段与 C~F 阶段改动的文件基本不重叠，可以并行。C 必须先于 D、E、F。

## 阶段 A：可观测性

### A1 每次场景加载都记录分阶段耗时

**现状**
- `ApplicationStartupTiming.RecordStage` 在 `Result` 非空后直接返回，第一次 Present 之后不再记录任何阶段（[ApplicationStartupTiming.cs](../src/SomeEngine.Runtime/ApplicationStartupTiming.cs)）。
- 测试平台的 `FirstFrameMs` 从 `SceneStarted` 开始计时，不包含旧场景的拆除时间（[SceneTestPlatform.cs:285](../samples/DefaultGame/SceneTestPlatform.cs)）。

**改完后**
- 新增 `public sealed record SceneLoadTiming(string ContentDirectory, int LoadIndex, IReadOnlyList<ApplicationStartupStage> Stages, double RequestToFirstFrameMs)`。每次场景加载（包括第一次）都生成一份。
- 阶段名固定为：`Teardown`、`ContentOpen`、`WorldLoad`、`MeshLoad`、`MaterialLoad`、`ShaderLoad`、`TextureLoad`、`MeshRegister`、`PipelineCreate`、`GraphReconcile`、`FirstFrame`。并行阶段按实际起止时刻记录。
- 计时从 `ApplicationScene.LoadScene(...)`（见 B6）被调用的时刻开始。
- `ApplicationScene` 新增 `Action<SceneLoadTiming>? SceneLoaded`。`SceneTestPlatform` 把每个场景的 `SceneLoadTiming` 写进 `results.json`。
- 冷启动阶段（`DeviceCreation` 等）保留在 `ApplicationStartupTimes` 里。场景相关阶段从中移除，统一由 `SceneLoadTiming` 提供。

**判定**
- BEH-A1：通过公共入口依次加载场景 A、B、A，`SceneLoaded` 恰好被调用 3 次，`LoadIndex` 依次为 0、1、2，每次的 `Stages` 都包含上面列出的全部阶段名。
- `results.json` 的每个场景条目都有 `SceneLoad.Stages` 字段。

### A2 基线测量

**改完后**
- 在 A1 合入后、B 阶段开始前，用 `tools/Invoke-Benchmark.ps1` 提交两组测量，分别作为冷启动和场景切换的基线：
  - 冷启动：三个场景各自作为首个场景启动，各 5 次。
  - 场景切换：小球 → Sponza → Bistro → 小球，各 5 轮。
- 再用 `tools/Measure-Scenes.ps1` 测一次帧性能基线，供 C~F 阶段对比。
- 测量配置写进报告：分辨率 1280×720、几何预算 256 MiB、TAA/VSM/异步计算开启、JIT 和 NativeAOT 各一组、开启和关闭 trace 分开记录。

**判定**
- PERF-A2：报告包含 stdout、stderr、退出码、测量配置、每次加载的 `SceneLoadTiming`。后续所有 PERF 判定都与这份基线比较。

## 阶段 B：加载与迭代速度

### B1 进程级 shader 库（ShaderLibrary）

**现状**
- 每个内容目录各有一个 `ApplicationContent`，各自创建 Slang global session（[RuntimeApplication.cs:70](../src/SomeEngine.Runtime/RuntimeApplication.cs)）。shader 缓存的 key 包含 `AssetLoader`，三个场景项目里字节完全相同的引擎 shader 被当作不同资源（例如三份 `cluster_draw.shader.asset` 的 md5 一致）。
- 每个 shader 加载时都会：新建 `ISession`，对每个源模块调用 `LoadModuleFromIRBlob`，再 composite、link（[ShaderSourceBlock.Slang.cs:178](../src/SomeEngine.Assets/Schema/ShaderSourceBlock.Slang.cs)）。
- 所有加载被按 global session 分配的 `SemaphoreSlim(1,1)`（`s_slangFrontEnds`）串行化。
- `loadedContent` 从不清理，每访问一个新目录就多保留一份 AssetLoader、Slang session 和一套强引用的 shader。

**改完后**
- 新类型 `SomeEngine.Resources.ShaderLibrary`，进程内每个编译目标（DXIL 或 SPIR-V）一个实例，由 `ApplicationRenderer`（B6）持有。它独占：
  - 一个 `IGlobalSession`；
  - 一个 `ISession`（每个编译目标一个）；
  - 已加载模块表 `Dictionary<string, IModule>`，key 为模块名。同名模块只加载一次。
  - shader 表 `Dictionary<ShaderKey, Shader>`，其中 `ShaderKey = (Guid AssetId, ulong ContentHash)`。`ContentHash` 来自 `ShaderBlock.InputVersions` 和保存代码的哈希。
- 所有 Slang 调用在一个专用线程上执行。调用方通过 `ValueTask<Shader> GetAsync(AssetLoader files, AssetId<ShaderAsset> id, CancellationToken)` 提交请求，库内部按 key 去重。串行是显式的"Slang 线程"，不再是隐藏的信号量。
- `ShaderAsset.CreateAsync`、`ShaderBlock.CreateAsync`、`ShaderSourceBlock.CreateUsingSavedCodeAsync` 改为 `internal`，只由 `ShaderLibrary` 调用。
- 删除：`ApplicationContent.Slang`、`ApplicationContent.ShaderSettings`、`ApplicationContent.ShaderValues`、`s_slangFrontEnds`、`Shader.CreateSlangGlobalSession` 在 Runtime 中的调用。
- 删除 `loadedContent` 列表：`ApplicationContent` 只描述一个内容目录的文件访问（`AssetLoader` 和 manifest），场景卸载时一起释放。

**判定**
- ARCH-10 通过。
- 源码检查：`SomeEngine.Runtime` 中没有 `CreateSlangGlobalSession(` 和 `CreateSession(`。
- BEH-B1：同一进程依次打开两个内容目录，两个目录里有字节相同的 shader asset。两次 `ShaderLibrary.GetAsync` 返回同一个 `Shader` 实例（`ReferenceEquals` 为真）。
- PERF-B1：trace 站点 `shader.Slang.CreateSession` 在整个进程生命周期内的计数等于编译目标数（D3D12 下为 1）。场景切换回已访问过的场景时，`shader.Slang.LoadModule` 计数增量为 0。

### B2 特化变体在材质加载时就绪

**现状**
- 材质特化变体存放在 `Lazy<Shader>` 里。首次用到时在 ReconcileGraph 中同步执行 `CreateEntryAsync(...).GetAwaiter().GetResult()`，做一次 Slang specialize+link（[ShaderBlock.cs:129](../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[ClusterMaterialStages.cs:1532](../src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs)）。
- 这违反 [RHI-CACHE-004](../wiki/architecture/RHI/Pipeline-Cache/Asynchronous-creation-and-warm-up-boundary.md) 的规定："No frame should perform Slang compile/link/codegen or first-use PSO creation."

**改完后**
- `ShaderLibrary.GetAsync` 返回的 `Shader` 已包含该 asset 声明的全部特化变体，变体在 Slang 线程上提前完成 link。
- `Shader.TryGetSpecialization` 只做查表，不触发任何 Slang 调用。
- 材质加载（`RuntimeScene` 中的材质任务）等待其 shader 的全部变体就绪后才算完成。

**判定**
- ARCH-10（不存在 `Lazy<Shader>`）通过。
- PERF-B2：trace 中 `render.Cluster.ResolveShadeStages` 和 `render.Cluster.ResolveRasterStages` 作用域内的 `shader.Slang.*` 子作用域计数为 0。

### B3 PSO 磁盘缓存

**现状**
- RHI 已有 `PipelineCache`，并且 `Create*PipelineAsync` 都接受 `PipelineCache? cache` 参数。但 Runtime、Render、samples 中没有任何地方创建或传入缓存。每次启动和每次切换场景，所有 PSO 都从 DXIL 重新创建。

**改完后**
- 新类型 `SomeEngine.Render.RenderPipelines`，每个 Device 一个，由 `ApplicationRenderer` 持有。它负责：
  - 持有一个 `PipelineCache`。启动时从 `Library/PipelineCache/{backend}-{vendorId:x4}-{deviceId:x4}-{driverVersion}.bin` 读取，退出时用 `TryGetPipelineCacheData` 写回。版本或环境不匹配时使用空缓存。
  - 提供 `Task<GraphicsPipeline> CreateCompute(...)`、`CreateGraphics(...)`、`CreateMesh(...)`，按描述去重，内部一律传入缓存。
- `ClusterPipelineSet`、`ClusterMaterialStages`、`LinkedComputePipeline`、`LinkedRasterPipeline` 以及各 feature 改为通过 `RenderPipelines` 创建管线。

**判定**
- ARCH-11 通过。
- PERF-B3：第二次冷启动时 `D3D12Diagnostics.PipelineCreation` 报告的缓存命中数等于创建请求数。场景切换回已访问过的场景时，新的原生 PSO 创建数为 0（由 `RenderPipelines` 去重保证）。

### B4 导入时烘焙 Cluster 页

**现状**
- glTF 导入只保存 `ClusterGeometryBlock` 和 `ClusterVertexAttributesBlock`（[ClusterGeometryImporter.cs](../src/SomeEngine.Assets.Importers/Importers/ClusterGeometryImporter.cs)）。
- 运行时 `RuntimeScene.CreateAsync` 优先读取 `ClusterGeometryBlock`，每次加载都执行 `ClusterMeshData.Build`：把所有 LOD 重新打包成页并构建 BVH，内部用了 LINQ `GroupBy` 和逐步增长的 `List<byte>`（[ClusterMeshData.cs:29](../src/SomeEngine.Assets/Pipeline/ClusterMeshData.cs)）。
- 内存中同时存在三份几何：block 对象、编码后的列表、页字节。
- Bistro 有 1532 个 mesh asset，每次加载、每次切换都完整重做一遍。

**改完后**
- `ClusterGeometryImporter` 在同一次导入中同时产出 `ClusterGeometryBlock`、`ClusterVertexAttributesBlock` 和 `ClusterMeshBlock`。`ClusterMeshBlock` 的 payload 就是 GPU 页加 BVH 字节。
- `ClusterMeshData` 移到 `SomeEngine.Assets.Importers`，访问级别为 `internal`。
- `ClusterGeometryBlock.CreateAsync`、`ClusterGeometryBlock.OpenPayloadSourceAsync`、`ClusterGeometryBlock.LoadPagesAsync` 删除。
- `RuntimeScene.CreateAsync` 只读 `MeshBlock`、`ClusterMeshBlock`、`MeshMaterialsBlock`。启动时只读取最低细节页，其余页由 `PageStream` 按需从文件读取，不再一次性生成整个 mesh。
- `samples/ImportedScenes` 的 Sponza 和 Bistro 重新导入一次。导入器版本号递增，使旧的导入结果失效。

**判定**
- ARCH-13 通过。
- BEH-B4：导入一个包含两个材质区段的 glTF，生成的 mesh asset 包含 `ClusterMeshBlock`。渲染它得到的图像，与用 `ClusterMeshBuilder.Build` 在运行时构建相同顶点和索引得到的图像在指定区域内一致。
- PERF-B4：Bistro 场景加载的 `MeshLoad` 阶段与 A2 基线对比，在同一份报告里给出数值。trace 站点 `cluster.MeshPageBuild` 在运行时计数为 0。

### B5 场景内容并发加载

**现状**
- 材质要等全部 mesh 加载完才开始；单个材质内部的纹理逐张 `await`（[RuntimeScene.cs:122-187](../src/SomeEngine.Runtime/RuntimeScene.cs)）。
- mesh 注册逐个 `await`，`ClusterMeshes` 内部还有 `SemaphoreSlim(1,1)`（`_registrationAdmission`）。全部 mesh 注册完后才开始加载首批页（[ClusterMeshPreparation.cs:116](../src/SomeEngine.Render.Cluster/ClusterMeshPreparation.cs)）。
- `worldTask` 和 `sceneResourcesTask` 是串行链。

**改完后**
- `RuntimeScene.CreateAsync` 改为按依赖驱动的任务图：
  - 每个 mesh 任务读完 `MeshMaterialsBlock` 后，立即为其中尚未请求过的材质启动材质任务。用 `ConcurrentDictionary<AssetId<MaterialAsset>, Task<Material>>` 去重。
  - 每个材质任务对其全部纹理并发调用 `TextureAsset.GetAsync`，然后 `Task.WhenAll`。
  - mesh 和材质并发数共用 `AssetLoader.MaxConcurrentReads`。
- `ClusterMeshes` 的注册分两步：
  - 页范围和 BVH 空间的分配在 `_gate` 锁内批量完成，一次处理一批 mesh；
  - BVH 字节复制在锁外并行执行。
  - 删除 `_registrationAdmission`。
- `ClusterMeshPrepareSystem.PrepareMissingAsync` 改为对全部缺失 mesh 一次调用批量注册，然后对它们的首批页一次性发起加载。

**判定**
- 源码检查：`RuntimeScene.cs` 的材质和纹理加载部分没有在 `foreach`/`for` 循环体内 `await`。
- 元数据检查：`ClusterMeshes` 不含 `SemaphoreSlim` 字段。
- PERF-B5：Sponza 和 Bistro 的 `SceneLoadTiming` 中，`MeshLoad`、`MaterialLoad`、`TextureLoad` 三个阶段的时间区间重叠。

### B6 切换场景保留渲染器

**现状**
- `SelectScene` 调用 `Application.Reload()`，帧循环 break，`RunRenderer` 中所有 `using var` 依次释放，然后 `RunScene` 从头重建（[RuntimeApplication.cs](../src/SomeEngine.Runtime/RuntimeApplication.cs)）。
- 每次切换都会重建：RenderWorld、RenderGraph、第二个 `RenderDevice`（`ApplicationDisplay.Rendering` 中已有一个）、256 MB 页堆和 64 MB BVH、全部 feature、`ClusterPipelineSet` 和全部 PSO。
- 拆除与加载串行，拆除阶段要逐个等待流送、注册和 GPU 提交完成。

**改完后**
- 新类型 `SomeEngine.Runtime.ApplicationRenderer`，每个 Device 创建一次。它持有：
  - `RenderDevice`（全局唯一，删除 `ApplicationDisplay.Rendering`）
  - `RenderGraph`、`RenderPipelines`、`ShaderLibrary`
  - `RenderWorld`、提取和准备系统
  - Cluster 几何与着色管线、`LightingFeature`、`VirtualShadowFeature`、`TemporalFeature`、`SkinningFeature`、呈现管线
- 新类型 `ApplicationSceneContent`，每个场景一份。它持有 GameWorld、`ApplicationContent`、场景 mesh 和材质的引用，以及场景脚本的设置。
- `ApplicationScene` 新增 `public void LoadScene(string contentDirectory)`，替换现在"改 `ContentDirectory` 再调 `Reload()`"的用法。切换流程：
  1. 后台开始加载新场景的 `ApplicationSceneContent`（B5 的任务图）。
  2. 当前场景继续渲染，直到新内容加载完成。
  3. 在一帧的提取之前，用新的 GameWorld 替换旧的。提取系统把旧 GameWorld 的全部渲染实体当作删除、新实体当作新增。`ClusterMeshPrepareSystem` 通过现有的 `ReleaseUnusedMeshes` 释放旧 mesh，材质分配系统按现有的增量路径更新材质。
  4. 旧的 `ApplicationSceneContent` 在 `RenderGraph.RetireAfterSubmittedFrames` 之后释放。
- 场景设置（阴影、时间抗锯齿等）通过各 feature 的 setter 应用，不重建 feature。
- 设备选项变化（mesh shader 开关、校验层开关、后端切换）仍走完整的 `ApplicationRenderer` 重建，这是唯一保留的重建路径。
- `ApplicationScene.Reload()` 保留，但只用于设备选项变化。

**判定**
- ARCH-12 通过。
- 源码检查：`SomeEngine.Runtime` 中 `new RenderDevice(` 恰好出现一次。
- BEH-B6a：通过公共入口加载场景 A、B、再回到 A。`ApplicationFrame.Index` 在三次加载之间单调递增，不归零。
- BEH-B6b：回到 A 后第 N 帧的图像，与首次进入 A 后第 N 帧的图像在指定区域内一致（固定步长、静态场景）。
- PERF-B6：场景切换的 `Teardown` 阶段与 A2 基线对比，在同一份报告里给出数值。回到已访问过的场景时，`ShaderLoad` 和 `PipelineCreate` 阶段耗时都低于 1 ms。

### B7 内容构建

**现状**
- 每个 `.shader` 都同时编 DXIL 和 SPIR-V（`SlangShaderImporter.CreateDefaultTargets`），还要为每个特化变体各生成一次代码。
- shader 在循环里逐个 `await project.BuildAsync`，`AssetProject` 内部还有 `_importGate` 把编译全程锁住（[SampleContent.cs:56](../samples/DefaultGame/SceneContent/SampleContent.cs)、[AssetProject.cs:69](../src/SomeEngine.Assets/AssetProject.cs)）。
- 引擎 shader 被复制进每个项目的 `generated/EngineSources`。DefaultGame 有 80 个 shader 产物共 76 MB，Sponza 76 个共 88 MB，Bistro 79 个共 128 MB。
- `tools/Build.ps1` 依次执行 Debug 构建并生成内容、Release 构建整个解决方案、Release 发布，引擎被编译三遍。

**改完后**
- 编译目标由项目设置决定：`AssetProject` 新增 `IReadOnlyList<SlangCompileTarget> ShaderTargets`。DefaultGame 开发配置只编 DXIL。发布配置按目标平台列出。
- `AssetProject.BuildAsync` 拆成三段：
  1. 在 `_importGate` 内做输入检查和复用判断；
  2. 在锁外编译；
  3. 回到 `_importGate` 内提交 manifest。
- 新增 `AssetProject.BuildAllAsync(IEnumerable<string> sources, int maxConcurrency)`，并行编译多个 shader。`SampleContent` 改用它。
- 引擎 shader（决策 D4）：
  - 在引擎的 `assets/Shaders` 下只烘焙一份。
  - `AssetLoader` 支持多个存储根：应用自己的内容目录，加上引擎内容目录。
  - 删除 `SampleContent.BuildEngineShadersAsync` 和 `generated/EngineSources` 目录。
- `tools/Build.ps1`：内容生成改为调用一个独立的内容构建工具（`tools/BuildContent`，Release 编译一次），之后的解决方案构建复用同一份 Release 输出。

**判定**
- 文件系统检查：仓库中不存在 `generated/EngineSources`。`samples/` 下没有 `cluster_draw.shader.asset`，只有引擎 `assets/Shaders` 下有一份。
- 源码检查：`SlangShaderImporter` 不再有 `CreateDefaultTargets`，编译目标只来自 `AssetProject.ShaderTargets`。
- PERF-B7：修改 `cluster_structures.slang` 中一行注释以外的代码后，执行内容构建。记录总耗时，与改动前的基线放在同一份报告中。

### B8 shader 热重载

**现状**
- 没有任何文件监视，也没有替换 shader 的接口。唯一的重载方式是 `ApplicationScene.Reload()`，它重建整个场景。[Hot-Reload](../wiki/architecture/Assets/Hot-Reload.md) 中描述的设计尚未在 Runtime 中实现。

**改完后**
- 新类型 `SomeEngine.Runtime.ShaderSourceWatcher`，只在开发构建中启用（`SOMEENGINE_DEVELOPMENT`）。它监视项目的 shader 目录和引擎 shader 目录中的 `.slang` 和 `.shader` 文件。
- 文件变化处理流程：
  1. 根据 `SourceBuildRecord.Inputs` 找出受影响的 `.shader`；
  2. 调用 `AssetProject.BuildAllAsync` 重新编译；
  3. 调用 `ShaderLibrary.Replace(ShaderKey previous, Shader next)` 发布新的 `Shader`。
- 消费方按 [Hot-Reload](../wiki/architecture/Assets/Hot-Reload.md) 的约定交换外层引用：
  - Cluster 着色和光栅阶段：在 `ClusterMaterialAssignments` 中把引用该 shader 的材质标记为已改变，走现有的材质增量路径。
  - 引擎管线 shader：持有它的 feature 提供 `ReplaceShader`，只重建受影响的 pass 和 PSO。
- 编译失败时打印诊断信息，继续使用旧的 shader。

**判定**
- BEH-B8a：开发构建中，修改材质 shader 输出颜色的常量后，在有限帧数内渲染区域的颜色变为新值，期间 `ApplicationFrame.Index` 连续递增（场景没有重启）。
- BEH-B8b：写入一个有语法错误的 shader 后，渲染图像不变，应用不退出。

### B9 公共头文件改为 import 模块

**现状**
- 仓库里只有 2 个 `.slang` 使用 `import`。公共代码靠 `#include` 进入每个 shader。例如 `resource_effects.slang` 被 include 40 次，`cluster_structures.slang` 被 include 17 次，`vsm_sampling.slang`（1473 行）被 include 15 次。
- 公共代码被嵌进每个 shader 的 IR，单个 shader 产物平均约 1 MB。改一个公共头文件，就要重编全部引用它的 shader。

**改完后**
- 先逐个检查被 include 的文件，列出依赖逐 shader 宏或生成内容的文件。例如 `generated/render_instance_layout.slang` 依赖生成的常量，这类文件保留 `#include`。这份"宏配置头文件"清单写进 [Shader-Build](../wiki/architecture/shader/Shader-Build.md)。
- 清单之外的文件改为 Slang 模块，各自有一个 `.shader` 定义，导入时只产出模块 IR。引用方改用 `import`。
- 导入器在 `SourceBuildRecord` 中把模块依赖记录为 asset 引用，而不是文件依赖。`ShaderLibrary`（B1）按模块名只加载一次。

**判定**
- ARCH-15 通过。
- 文件系统检查：引擎 `assets/Shaders` 下全部 shader 产物的总大小，与改动前一起记录在报告中。
- PERF-B9：修改一个模块文件后，内容构建只重编该模块和直接 `import` 它、且自身需要重新 link 的 shader，构建日志列出的重编文件与 `SourceBuildRecord` 的依赖一致。

## 阶段 C：帧语义与状态归属

### C1 帧提交语义与观察者接口

**现状**
- 16 个子系统实现了 `IRenderGraphFrameExecutionObserver`，在 `OnRenderGraphFrameFinished` 里通过 `TryGetPassCompletion` 判断本帧是否完成，再决定提交还是回滚。渲染层中 `TryGetPassCompletion` 有 27 处调用。
- 帧有三种结束方式：
  1. 正常执行；
  2. `Cancel`：`RenderGraphFrame.Dispose` 时还没执行，观察者收到的结果为空；
  3. 执行中途抛异常：可能已经提交了一部分。
- 为了应对后两种情况，各 feature 维护 `_frameX`、`_committedX`、`_previousX` 三套字段。`VirtualShadowFeature` 的各 partial 文件加起来有 182 个私有字段，回滚逻辑见 [VirtualShadowFeature.cs:1028](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)。

**改完后**（依赖决策 D1）
- `IRenderGraphFrameExecutionObserver` 只保留一个方法：`void OnFrameSubmitted(in RenderGraphSubmission submission)`。它只在本帧全部命令成功提交后调用一次。
- `RenderGraphSubmission` 是一个 ref struct，只提供：
  - `ulong FrameIdentity`
  - `bool TryGetReadbackCompletion(GraphReadbackId, out ulong completedFrame)`
- `RenderGraphFrameState.Execute` 遇到任何异常都调用 `Graph.MarkLost()` 后再抛出。`RenderGraph` 进入 Lost 状态后，`TryBeginFrame` 抛出 `RenderGraphLostException`。
- `Cancel` 不通知任何观察者。
- 每个 feature 遵守同一条规则：**向 `RenderGraphFrame` 暂存本帧数据的方法，只读取 feature 字段，不修改字段。** feature 字段只在 `OnFrameSubmitted` 中修改。
- GPU 读回例外：接收方只在 `OnReadback` 中修改读回相关字段，包含测量结果、来源帧 tag 和所需容量。RG 在 GPU 完成后的 `TryOpenNextFrame` / `CollectCompleted` 路径交付这些数据。读回送达后的 prepare 阶段、`BeginFrame(next)` 之前，由 feature 自持一次图编辑扩容，提交成功后采用结构字段。其余本帧产生的持久状态仍由 `OnFrameSubmitted` 采用；图编辑的结构归属遵守 [Graph-Edits](../wiki/architecture/rendering/Graph-Edits.md)。
- 删除：`OnRenderGraphFramePreparing`、`OnRenderGraphSubmitted`、`OnRenderGraphFrameFinished`、`RenderGraphFrameResult`，以及全部 feature 中为回滚而存在的字段。

**判定**
- ARCH-05、ARCH-14 通过。
- 源码检查：`SomeEngine.RenderGraph` 以外的代码中没有 `TryGetPassCompletion`。
- BEH-C1a（RenderGraph 层）：一个测试观察者在暂存时把计数写入 pass 数据，在 `OnFrameSubmitted` 中累加。依次执行"渲染、暂存后丢弃、渲染"，最终计数等于 2，丢弃的那一帧对 GPU 输出没有影响。
- BEH-C1b：执行中 pass 回调抛出异常后，`RenderGraph.TryBeginFrame` 抛出 `RenderGraphLostException`。
- BEH-C1c（Runtime 层）：渲染 N 帧、丢弃一帧、再渲染 1 帧得到的图像，与连续渲染 N+1 帧得到的图像在指定区域内一致。

### C2 GPU 完成跟踪只由 RenderGraph 持有

**现状**，以下各处都在维护 GPU 完成信息：
- RHI：每条命令记录资源引用（`NativeLease` 等）。
- RenderGraph：`FrameSlot.Completions`、`RetireAfterSubmittedFrames`。
- `RenderDevice`：`TrackSubmission`、`WaitForTrackedSubmissions`、`RetireAfterTrackedSubmissions`、`_retired`。
- `RenderFrameCoordinator`：`_readerFences`，以及读者、准备、时间线的各种计数。
- `RenderTimeline`：每个分代一组 fence。
- 各层的在途帧数互不相同：Runtime 中的 RG 为 2，实例数据为 2 代，Cluster residency 为 1 代。

**改完后**
- `RenderGraph` 是渲染层唯一的 GPU 完成来源，对外提供：
  - `ulong LastSubmittedFrame { get; }`
  - `ulong LastCompletedFrame { get; }`：在 `CollectCompleted` 中更新。
  - `bool IsFrameComplete(ulong frameIdentity)`
  - `WaitStatus WaitForFrame(ulong frameIdentity, TimeSpan timeout)`
  - 现有的 `RetireAfterSubmittedFrames(IDisposable)`
- 渲染层需要"GPU 用完之后"语义时，保存帧号（`ulong`），不保存 `QueueCompletion`。
- 删除以下类型和文件：
  - `RenderFrameCoordinator`、`RenderFrame`、`RenderObservation`、`RenderPrepareScope`
  - `RenderTimeline`、`RenderQueueCompletions`、`RenderFrameSynchronizationDiagnostics`
  - `RenderReadOwnership`、`RenderFrameUseLease`、`RenderTimelineLease`
- 渲染循环的顺序在 `ApplicationRenderer.RenderFrame()` 里写死为：提取 → 准备 → 构建帧 → 执行 → 呈现。RenderWorld 只在"准备"中被写入。CPU 侧的重叠只有一处：GameWorld 的下一轮更新与渲染线程并行，这一点由现有的 `nextGameUpdate` 任务表达。
- `RenderDevice` 改为在构造时接收 `RenderGraph`。纹理替换后的旧资源通过 `graph.RetireAfterSubmittedFrames` 退役。删除 `TrackSubmission`、`WaitForTrackedSubmissions`、`RetireAfterTrackedSubmissions`、`_retired`。

**判定**
- ARCH-03 通过。
- 文件系统检查：`src/SomeEngine.Render/Frame/` 下只剩 `TemporalJitter.cs` 和 `TemporalResolveSettings.cs`。
- BEH-C2：执行一帧后调用 `WaitForFrame(identity, ...)` 返回 `Completed`，此后 `IsFrameComplete(identity)` 为真，`LastCompletedFrame >= identity`。

### C3 资源状态只由 RenderGraph 持有

**现状**
- 外部资源的 barrier 边界状态由各 feature 自己保存，每帧通过 `BindExternalBuffer(..., CaptureEndpoints())` 交回 RG，再从 `GetBufferStates` 取回存下。涉及 12 个渲染文件：Cluster、VSM、MaterialBindless、`RenderDevice.Textures`、Terrain、实例数据等。
- 这些资源绕过 RG，直接用 `backend.CreateBuffer/CreateTexture` 创建。例如 VSM 的 atlas、页表、page owner（[VirtualShadowMap.cs:156](../src/SomeEngine.Render/Shadows/VirtualShadowMap.cs)）。
- RG 已经有 `RegisterExternalBuffer`、`RegisterExternalTexture`，注册后由 RG 跨帧持有状态。但渲染层用的是 `DeclareExternal*` 加每帧 `BindExternal*` 的写法。

**改完后**
- 渲染层的每个 GPU 资源只有两种来源：
  1. `RenderGraphEdit.CreatePersistentBuffer` / `CreatePersistentTexture`（RG 创建、持有）；
  2. 少数必须由调用方创建的资源（swapchain 图像、外部互操作资源），通过 `RegisterExternalBuffer` / `RegisterExternalTexture` 注册一次。
- `RegisterExternal*` 改为不接收边界状态参数，初始状态取资源自身的 `InitialSync` 和 `InitialAccess`。
- 删除 `DeclareExternalBuffer`、`DeclareExternalTexture`、`BindExternalBuffer`、`BindExternalTexture`，以及 `RenderGraphFrame.Import(Buffer, ReadOnlySpan<BufferBoundaryState>)` 和对应的 Texture 重载。swapchain 的 `Import(in SwapchainImage, Queue)` 保留。
- 需要改容量的资源（例如 VSM 页表扩容）通过 `RenderGraphEdit.SetBufferDescription` 修改，由 RG 负责旧资源退役。
- 读回统一走 RG 的 Readback API（`GraphReadbackId`），渲染层不再自己创建 readback 堆上的 buffer。`GpuPoseReadback` 和 `FrameOutputVerifier` 一起迁移。

**判定**
- ARCH-02、ARCH-04、ARCH-16 通过。
- 公共 API 检查：`RenderGraphFrame` 不再有接收 `BufferBoundaryState` 或 `TextureBoundaryState` 的方法。

### C4 实例数据通路

**现状**
- `RenderInstanceResources` 用两代 Upload 堆 buffer 作为持久存储（[RenderInstanceResources.cs](../src/SomeEngine.Render/Instances/RenderInstanceResources.cs)）：
  - `CopyBatchState` 和 `CaptureBatchBytes` 用 `MapType.Read` 从上传堆读回数据。上传堆是 write-combined 内存，读取很慢。
  - `OpenRead` 每帧对整块 buffer 做 Flush。
  - 之后再由 `RenderInstanceDataTransfer` 把数据 GPU 拷贝到 device 缓冲。
- 加上 RenderWorld 里的 ECS 源数据，实例数据一共有 4 份。
- `ConditionalWeakTable<RenderGraph, RenderInstanceDataTransfer>` 把对象隐式挂在 RG 上。

**改完后**
- `RenderInstanceResources` 改名为 `RenderInstanceData`，持有：
  - 一份 CPU 端托管数组 `byte[] _propertyBytes` 和 `byte[] _metadataBytes`。这是 RenderWorld 之外唯一的 CPU 副本，`ReadInstance<T>` 从这里读取。
  - 一个 RG 持久 buffer `GraphBufferId PropertyData`、一个 `GraphBufferId Metadata`，在构造时通过 `RenderGraphEdit` 创建。
  - 一个本帧变化区间表 `BufferRangeSet _changedRanges`。
- 写入实例时：写托管数组，并把变化区间加入 `_changedRanges`。
- 构建帧时：对每个变化区间调用 `RenderGraphFrame.GetBufferDataWriteSpan` 或 `Upload` 加拷贝 pass，把数据写进 device buffer，然后清空区间表。
- 删除：
  - `UploadBuffer`、全部"generation"概念（`_generationNeedsFlush`、`_batchGenerations`、`SelectWritableGeneration`）
  - `CopyBatchState`、`CaptureBatchBytes`、`_batchOriginalBytes`
  - `RenderInstanceDataTransfer` 中的两代 source
  - `StoreReadStates`、`StorePropertyReadStates`、`StoreMetadataReadStates`
  - `ConditionalWeakTable` 关联：`RenderInstanceData` 在构造时直接接收 `RenderGraph`。
- 删除 `RenderInstanceWriteScope` 和 `RenderInstanceBorrowerLease`：写入只发生在准备阶段（C2 中已固定顺序），不需要作用域对象。

**判定**
- ARCH-04、ARCH-16 通过。
- 源码检查：`src/SomeEngine.Render/Instances/` 中没有 `MemoryType.Upload`、`Generation`、`ConditionalWeakTable`。
- BEH-C4：修改 1 个实例的变换，下一帧 GPU 读到的实例数据与 CPU 写入一致（通过 RG 读回比较）；未修改的实例数据不变。
- PERF-C4：`Measure-Scenes` 中 1024 小球（每帧修改全部实例变换）的 P50/P95 与 A2 基线对比。

### C5 统一的 GPU→GameWorld 反馈通道

**现状**
- GPU 姿势回读把 `TaskCompletionSource` 放进 GameWorld 组件，经过提取带进 RenderWorld，再由渲染侧完成（[GpuPoseReadback.cs:31](../src/SomeEngine.Animation.Pose/GpuPoseReadback.cs)）。这是绕开提取边界、跨两个 world 共享的可变对象。
- 各种反馈通道（Cluster 流送、姿势、VSM 质量、RG readback）形状各不相同。

**改完后**
- 新类型 `SomeEngine.Render.RenderFeedback<T> where T : unmanaged`。它是渲染侧写入、GameWorld 系统读取的单向队列，每条记录带上产生它的帧号。
- `ApplicationRenderer` 在每帧 GameWorld 更新开始前，把已完成帧的反馈交给 GameWorld 系统。只交付 `LastCompletedFrame` 之前的记录，符合 [Delayed-Feedback](../wiki/architecture/rendering/Delayed-Feedback.md) 的规定。
- `GpuPoseReadbackRequest` 改为值类型，只包含身份和骨骼范围。结果通过 `RenderFeedback<GpuPoseReadback>` 交付，GameWorld 中的 `GpuPoseFeedbackSystem` 按身份匹配后写入组件。
- `ClusterStreamingFeedbackChannel` 改为 `RenderFeedback<ClusterPageDemand>`。VSM 质量读回改为 `RenderFeedback<VirtualShadowQualitySample>`。

**判定**
- ARCH-22 通过。
- 源码检查：`SomeEngine.Animation.Pose` 中没有 `TaskCompletionSource`。
- BEH-C5：请求一次姿势回读，在第 k 帧（k ≥ 1）由 GameWorld 系统读到的骨骼矩阵等于 GPU 计算结果；请求帧本身读不到结果。

## 阶段 D：RenderGraph

### D1 共享调度

**现状**
- 每个 frame-in-flight 槽位（`FrameSlot`）各持有一份 `RenderGraphFrameState` 和 `FrameExecutor`。结构编辑会分发到每个槽位的 `_edits` 列表，各槽位分别重做依赖分析和调度（[FrameSlot.cs](../src/SomeEngine.RenderGraph/FrameSlot.cs)、[FrameExecutor.cs](../src/SomeEngine.RenderGraph/FrameExecutor.cs)）。
- 增量流程由 `_analysisReady`、`_analysisChanged`、`_recordingChanged`、`_reuseRecording`、`_scheduleInitialized`、`_rowsReady` 这几个标志组合驱动，属于隐式状态机。

**改完后**
- 新类型 `GraphSchedule`，由 `RenderGraph` 唯一持有。它包含持久 pass 的：依赖边、拓扑序、队列分配、跨队列同步点、资源生命周期、每个 region 边界上的资源状态。
- `GraphSchedule` 只在两个时机重建：`RenderGraphEdit.Commit` 之后的下一帧开始时，以及 `RenderGraphFrameOptions` 中影响调度的选项改变时。重建的输入是 `GraphStructure` 加 `GraphEditChanges`。
- `FrameSlot` 改名为 `FrameResources`，只持有本槽位独有的数据：
  - 临时资源的放置
  - 上传页和读回页
  - 已录制命令的复用缓存
  - 本帧的提交完成点
  - 当帧 pass 段（D2）的分析结果
- `FrameExecutor` 拆分为：
  - `GraphSchedule`：持久部分，放在 RenderGraph 上；
  - `FrameRecording`：每槽位一份，只做物化、录制、提交。
- 原来的布尔标志换成两个显式值：
  - `GraphSchedule.Version`（`ulong`）：每次重建递增；
  - `FrameResources.ScheduleVersion`：本槽位上次使用的版本。
  - 两者不相等时，本槽位重新物化。没有其他标志参与判断。

**判定**
- ARCH-07 通过。
- 元数据检查：`FrameRecording` 和 `FrameResources` 中没有名字以 `Changed`、`Ready`、`Reuse`、`Initialized` 结尾的 bool 字段。
- BEH-D1：`MaximumFramesInFlight = 3` 的图做一次结构编辑，随后连续执行 3 帧。公共 `RenderGraphStatistics` 显示只有第 1 帧的 `AnalyzedPassCount > 0`，第 2、3 帧的 `AnalysisReused` 为真。

### D2 当帧 pass 段

**现状**
- 当帧（动态）pass 和持久 pass 放在同一组行里，通过逐字段比对上一次使用该槽位时的数据，决定是否复用分析结果（[FrameAnalysis.cs:1146](../src/SomeEngine.RenderGraph/FrameAnalysis.cs)）。
- 当帧 pass 的数量或内容一变，就会通过全局 `_analysisChanged` 触发整图重新执行 `UpdateDependencies`、`ResolveSchedule`、`ResolveFrontiersAndLifetimes`。这违反 AGENTS 的要求："不能因此每帧重建主体图"。

**改完后**
- 每个 region 有两个固定的"当帧段"位置：region 开始处和 region 结束处，由 `AddComputePass(..., region, FrameSectionPosition.Start | End)` 指定。原来的 `insertion = interval.Start / interval.End` 规则变成这个显式参数。
- `GraphSchedule` 为每个当帧段位置记录持久资源的进入状态。
- 当帧段的分析只覆盖段内的 pass：
  - 段内依赖和同步在段内计算；
  - 段的入口 barrier 从 `GraphSchedule` 记录的进入状态出发；
  - 段的出口由 RG 插入 barrier，把持久资源恢复为 `GraphSchedule` 期望的状态。
- 当帧 pass 不能修改持久资源的描述，也不能创建持久资源。
- `RenderGraphStatistics` 新增两个公开字段：`bool PersistentScheduleReused` 和 `int FrameSectionPassCount`。

**判定**
- BEH-D2a：持久图不变的情况下，每帧加入数量不同的当帧计算 pass，这些 pass 读写一个持久 buffer。从第 2 帧起每帧 `PersistentScheduleReused` 都为真，`FrameSectionPassCount` 等于当帧加入的 pass 数。
- BEH-D2b：当帧 pass 写入一个持久 buffer 后，同一 region 中排在它后面的持久 pass 读到的是写入后的值（通过读回验证）。
- PERF-D2：`Measure-Scenes` 三场景的 CPU 帧时间（`ReconcileMs`、`ExecuteMs`）与 A2 基线对比。

### D3 小项

**改完后**
- 两个 `UploadPage`（`FrameBufferTransfers.UploadPage`、`ResourceAllocators` 中的 `UploadPage`）合并为一个类型 `UploadPage`，放在 `UploadPages.cs`。两处都使用它。
- `RenderGraphFrameState._bufferViews` 和 `_textureViews` 的 key 去掉 label 字符串，label 单独存在 view 行里。
- `RenderGraphBufferWrites` 不再使用 `ThreadLocal<List<...>>(trackAllValues: true)` 和 LINQ `SelectMany`：每个写入线程通过 `RenderGraphBufferWrites.CreateWriter()` 取得自己的 `BufferWriter`，提交时按写入者列表合并。

**判定**
- ARCH-17、ARCH-20 通过。
- 源码检查：`SomeEngine.RenderGraph` 中没有 `ThreadLocal<`。

## 阶段 E：RHI

### E1 检验迁到 ValidationLayer

**现状**
- D3D12 后端有 601 处 throw，Vulkan 有 265 处，`ValidationLayer` 只有 23 处。
- 同一条规则两边都检查，例如 SetPipeline 拒绝 WorkGraph、同 device 检查，分别出现在 [CommandBindings.cs:16](../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs) 和 [ValidationLayer.Commands.cs:445](../src/SomeEngine.Graphics.Validation/ValidationLayer.Commands.cs)。后端那一份始终开启，无法关闭。

**改完后**
- 按 [Validation](../wiki/architecture/validation/Validation.md) 的分类处理后端里的每一处 throw：
  - 内部程序错误（参数、类型来源、同 device、可见节点掩码、状态顺序）：移到 `ValidationLayer`。后端删除对应检查，直接执行。
  - 外部输入错误（文件、缓存数据）：保留在创建和读取接口中。
  - 内存安全保护（向原生内存复制前的边界检查）：保留，集中在 `NativeMemoryChecks` 静态类中。
  - 执行失败（HRESULT/VkResult 失败、设备丢失）：保留，统一抛 `GraphicsException`。
- 命令录制方法里的 `RequireCommandContext` 改为直接强制转换：`(D3D12CommandContext)context`。类型来源错误由 ValidationLayer 先行拦截。

**判定**
- ARCH-09 通过。
- 源码检查：`RequireSameDevice`、`RequireResourceVisible` 只出现在 `SomeEngine.Graphics.Validation` 中。
- 行为检查：`tests/SomeEngine.Graphics.Direct3D12.Tests` 中现有的参数错误类测试改为在启用 ValidationLayer 时运行，断言相同的异常类型或错误码。

### E2 资源生命周期

**现状**
- 每条命令都对资源和描述符做生命周期 capture（`PrepareCaptures`、`Capture(...)`、`NativeLease`、`DescriptorLease`），见 [CommandLifecycle.cs:521](../src/SomeEngine.Graphics.Direct3D12/CommandLifecycle.cs)。

**改完后**（依赖决策 D2）
- 每个 `Device` 持有一个 `DeferredReleases` 队列，元素为 `(nativeObject, perQueueSubmittedValues[])`。
- 资源 `Dispose` 时，记录当时各队列的已提交值，把 native 对象放进队列。`CollectCompleted` 时，释放所有完成值已达到记录值的对象。
- 描述符的回收走同一条路径。
- 删除：
  - `PrepareCaptures`、全部 `Capture(...)` 方法、`_captures`、`Recording.Capture*`
  - `NativeLease` 类型，改为直接持有 native 指针
  - `DescriptorLease` 改名为 `DescriptorAllocation`，不再有引用计数
- ValidationLayer 记录每个已录制、未提交的命令所引用的资源。资源在这期间被 Dispose 时，ValidationLayer 报告错误码 `ResourceDisposedWhileRecorded`。

**判定**
- 源码检查：`SomeEngine.Graphics.Direct3D12` 和 `SomeEngine.Graphics.Vulkan` 中没有 `PrepareCaptures`、`Capture(`。
- ARCH-01（不含 `Lease`）通过。
- BEH-E2a：提交使用某 buffer 的命令后立即 Dispose 该 buffer，等待队列完成后 GPU 结果正确，`CollectCompleted` 后 native 对象已释放（通过 `D3D12Diagnostics` 的公开存活对象计数观察）。
- BEH-E2b：启用 ValidationLayer 时，Dispose 被已录制、未提交的命令引用的 buffer，得到错误码 `ResourceDisposedWhileRecorded`。

### E3 命令复用改为显式

**现状**
- [CommandRecordingReuse.cs](../src/SomeEngine.Graphics.Direct3D12/CommandRecordingReuse.cs) 用静态 `ConcurrentDictionary<nint, D3D12CommandSlot>`（以原生 list 指针为 key）加 `[ThreadStatic]` 标志，拦截每一次原生调用来收集重放数据。
- RG 在上面还有一层 `_reuseRecording`，两层录制复用叠在一起。

**改完后**
- `CommandRecordingDesc` 新增 `bool Repeatable`。只有 `Repeatable` 的录制会在 `D3D12CommandContext` 实例字段中保存重放数据，不再通过全局表按指针查找。
- 原生调用包装（`CommandExecutionTrace`、`CommandBindingTrace`）不再调用 `CollectCommandData`。它们接收 `D3D12CommandContext` 参数，由调用方决定是否记录。
- RG 的 `FrameRecording` 对"可复用批次"传入 `Repeatable = true`，并且是唯一决定复用的地方。
- 删除：`CommandRecordings` 静态字典、`_threadRecording`、`_replayingCommands`、`FindRecording`。

**判定**
- ARCH-08 通过。

### E4 录制句柄简化

**现状**
- `RecordedCommandsLease` 用 lock、`Monitor.Wait`、按线程 id 记录的释放归属、5 种状态，组成一个复杂的状态机（[Commands.cs:57](../src/SomeEngine.Graphics/Commands/Commands.cs)）。

**改完后**
- `RecordedCommands` 改为 `sealed class`，只有一个状态字段：`enum RecordedCommandsStatus { Executable, Submitted, Discarded }`，用 `Interlocked.CompareExchange` 转换。
- 并发 Dispose 与 Submit 属于程序错误，由 ValidationLayer 检查，不再在后端内部用锁协调。
- 设备丢失通过 `Device` 的终止状态表达，不再写入每个句柄。
- 删除 `RecordedCommandsLease`。

**判定**
- 元数据检查：`SomeEngine.Graphics` 中不存在 `RecordedCommandsLease` 类型；`RecordedCommands` 没有 `object` 类型的锁字段。

## 阶段 F：渲染功能边界

### F1 视图进入 RenderWorld

**现状**
- VSM 调用 `_geometry.StageViews(..., policy: 1u)`，用魔数区分视图类型（[VirtualShadowFeature.cs:985](../src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)）。
- 调用顺序有隐式约定：先 primary 视图，再 policy 视图，并且必须在 Cluster 的 Update 之前（[ClusterPipelineSystem.cs:206](../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs)）。
- `Render.Features` 直接引用 `Render.Cluster.Pipeline`。

**改完后**
- RenderWorld 中新增组件：
  - `RenderViewTarget { ViewTargetKind Kind; uint TargetIndex; }`，其中 `enum ViewTargetKind { Screen, ShadowPages }`。
  - 已有的 `RenderView`。
- VSM 在准备阶段为每个阴影视图创建或更新 RenderWorld 实体，写入 `RenderView` 和 `RenderViewTarget { Kind = ShadowPages }`。上一帧的视图投影写在同一实体的 `RenderPreviousView` 组件中。
- Cluster 几何管线在每帧查询全部带 `RenderView` 的实体，按 `RenderViewTarget.Kind` 选择光栅模式。
- 阴影光栅需要的资源（页表、HiZ、深度缓存分类），由 VSM 通过已有的 `IGraphResourceProvider` 按名字提供。Cluster 只按名字请求，不知道 VSM 的存在。
- 删除：`ClusterPipelineSystem.StageViews`、`_staged*` 字段、`ClusterViewUniforms.TargetPolicy` 的 `uint` 语义（改为 `ViewTargetKind`）、`ClusterPipelineSystem.IntermediateOcclusionRegion` 的对外暴露（改为 `ClusterGraphRegions` 中一个具名 region，由 Runtime 组装时传给 VSM）。
- `SomeEngine.Render.Features.csproj` 删除对 `Render.Cluster.Pipeline` 的引用。

**判定**
- ARCH-06、ARCH-14 通过。
- 元数据检查：Cluster 管线类型中不存在名为 `StageViews` 的公开方法。
- 行为检查：现有的 VSM 图像测试（`Cluster-Image-Checks`）通过，图像与改动前在指定区域内一致。

### F2 拆分 ClusterPipelineSystem

**现状**
- `ClusterPipelineSystem` 是一个 partial 类，拆在 25 个文件里，约 8000 行。
- 它还持有透明、贴花、shading、混合测量、阴影请求 region 和 caster 缓存，与 [Cluster-Scope](../wiki/architecture/cluster/Cluster-Scope.md) 写的 "Excludes shadow/VSM" 不一致。
- 材质重配由 7 个布尔量驱动一个大函数（[ClusterPipelineSystem.cs:385](../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs)），并且在 4 个对象之间来回走 `ChangesSince/AcceptChanges` 协议。

**改完后**，拆成以下类型，每个类型一个文件，不使用 partial：

| 新类型 | 程序集 | 职责 | 来自原文件 |
|---|---|---|---|
| `ClusterGeometryPipeline` | Render.Cluster.Pipeline | 视图收集、剔除、BVH 遍历、光栅、可见性缓冲、HiZ | `.cs`、`.Raster`、`.CullWork`、`.Work`、`.Bounds`、`.Scene`、`.GeometryFrames`、`.Deformation`、`.Streaming`、`.Feedback` |
| `ClusterShadingPipeline` | Render.Cluster.Pipeline | 材质分箱和着色 | `.Shading`、`.Materials`、`.Textures` |
| `ClusterGraphInstaller` | Render.Cluster.Pipeline | 持久图的安装和材质拓扑变化后的结构编辑 | `.Passes`、`.GraphData`、`.Resources`、`.BufferData`、`.Lifetime` |
| `TransparencyFeature` | Render.Features/Transparency | 透明几何与 OIT 合成，自己持有 tile 网格参数 | `.Transparency`、`.TransparencyShaders`、`.Ordered` |
| `MaterialDecalFeature` | Render.Features/Decals | 材质贴花单元和资源 | `.Decals` |
| `ShadowCasterCache` | Render.Features/Shadows（并入 VSM） | caster 变化收集 | `.Casters`、`.ShadowRequests` |
| `BlendMeasurementFeature` | tools/SomeEngine.Transparency.Measurements | 混合测量（诊断工具，不属于引擎） | `.BlendMeasurements`、`ClusterBlendMeasurements.cs` |

- 材质重配不再由多个布尔量驱动，改为一个显式的变化描述：`ClusterMaterialChange { ChangedRasterBins; ChangedShadeBins; ChangedScalarRanges; ChangedTextures; ShadingPathChanged }`。由 `ClusterMaterialAssignments.TakeChanges()` 一次性产出，`ClusterGraphInstaller.Apply(in ClusterMaterialChange)` 一次性消费。删除各对象上的 `AcceptChanges`。

**判定**
- ARCH-18 通过。
- 源码检查：`src/SomeEngine.Render.Cluster.Pipeline/` 中不区分大小写地搜索 `shadow`、`vsm`、`transparen`、`decal`，结果为 0。
- 元数据检查：`ClusterMaterialAssignments`、`ClusterRasterStages`、`ClusterShadeStages` 中不存在名为 `AcceptChanges` 的方法。
- 行为检查：现有 Cluster 图像测试全部通过。

### F3 合并 VSM

**现状**
- `VirtualShadowMap` 在 Render 程序集，`VirtualShadowFeature` 在 Features 程序集，两层各自维护 pending/committed 状态，构成两级事务。
- 资源归属混用：atlas、页表、page owner 由后端直接创建，barrier 状态手动维护；prefilter 资源则走 RG 的持久资源。
- 有 `_prefilterUniforms[6]`、`[7]`、`[8]` 这类魔数下标。

**改完后**
- `SomeEngine.Render/Shadows/` 只保留数据类型：`VirtualShadowMapSettings`、`VirtualShadowView`、`VirtualShadowPageUniforms`、`VirtualShadowMapShaders`。
- `VirtualShadowMap` 的逻辑并入 `SomeEngine.Render.Features/Shadows/VirtualShadowFeature`，按职责拆成不使用 partial 的类型，每个类型一个文件：
  - `VirtualShadowPages`：页表、物理页、atlas、page owner。全部通过 RG `CreatePersistent*` 创建。页表扩容通过 `SetBufferDescription` 完成。
  - `VirtualShadowViews`：阴影视图的计算和 RenderWorld 实体（F1）。
  - `VirtualShadowRequests`：页请求和 receiver mask。
  - `VirtualShadowPrefilter`：预过滤。uniform 改为具名字段，例如 `_prefilterAxisUniforms`、`_prefilterMergeUniforms`。
  - `VirtualShadowQuality`：动态分辨率和质量反馈，使用 C5 的 `RenderFeedback`。
  - `ShadowCasterCache`：来自 F2。
- 遵守 C1 的规则：暂存方法不修改字段，状态只在 `OnFrameSubmitted` 中修改。

**判定**
- ARCH-02、ARCH-04、ARCH-14 通过。
- 文件系统检查：`src/SomeEngine.Render/Shadows/` 中不存在 `VirtualShadowMap.cs` 和 `VirtualShadowPageCacheResources.cs`。
- 源码检查：`src/SomeEngine.Render.Features/Shadows/` 中不存在形如 `_[a-zA-Z]+\[\d+\]` 的下标访问。
- 行为检查：现有 VSM 图像测试通过。
- PERF-F3：`Measure-Scenes` 三场景的帧时间与 A2 基线对比。

### F4 光照资源表与透明网格

**现状**
- `LightingFeature` 里，shader 资源名到 buffer 的字符串映射在 build pass、`TryResolve` 和 direct grid 三处分别手写一遍。入口检测依赖 `"prepare-v2"` / `"fill-v2"` 这样的版本字符串。
- 透明混合网格直接借用光照的 `ClusteredViewGrid`，在 `RuntimeApplication` 里手工接线。
- `SomeEngine.Render.Tests` 引用了 `tools/SomeEngine.Transparency.Research`。

**改完后**
- 新类型 `LightingResources`：一张静态只读表，元素为 `(string Name, ResourceBindingType Type, LightingResource Resource, PipelineSync Sync, GraphAccessMode Mode)`。build pass、`TryResolve` 和 direct grid 三处都从这张表查找。
- 入口检测改为检查 shader 是否声明了表中要求的入口和资源，不再比较版本字符串。
- `TransparencyFeature`（F2）自己声明 tile 尺寸和深度切片数，不再从 `LightingFeature` 取网格。`RuntimeApplication` 中删除 `lighting.CreateTileGrid()` 的接线。
- `SomeEngine.Render.Tests` 中依赖研究工具的测试（`TransparencyBehaviorTests`）移到 `tools/SomeEngine.Transparency.Research` 自己的测试项目。

**判定**
- ARCH-06、ARCH-19、ARCH-21 通过。
- 源码检查：`LightingFeature` 和 `LightingResources` 中没有 `-v2` 字符串字面量。

### F5 默认顶点布局核对

**现状**
- [cluster_draw.slang](../assets/Shaders/cluster_draw.slang)、`cluster_tessellation_phong.slang`、`sw_raster.slang` 直接使用 `StaticVertexEval`，即 normal/tangent/uv0 布局。`typedef OpaqueVisibilityEvaluate<StaticVertexEval.Vertex> DefaultVisibilityEvaluate` 写在管线 shader 里。

**改完后**
- 管线 shader（`cluster_*.slang`、`sw_raster*.slang`）中的入口全部写成以 `IVertexEvaluate` 为泛型参数的形式，不引用任何具体的求值类型。
- 具体的求值类型由材质 shader 选择：标准材质的深度和阴影入口选 `StaticPositionEval`，着色入口选 `StaticVertexEval`；自定义材质选自己的类型。
- `DefaultVisibilityEvaluate` 这个 typedef 移到标准材质的 shader 文件中。

**判定**
- 源码检查：`assets/Shaders/` 中 `StaticVertexEval` 和 `StaticPositionEval` 只出现在 `vertex_evaluate.slang`（定义处）和标准材质 shader 中，不出现在 `cluster_*.slang` 和 `sw_raster*.slang` 中。

## 阶段 G：命名清理

**改完后**，以下名字按表重命名。其余命中 ARCH-01 的名字，在所属工作项中随改随清。

| 现名 | 新名 |
|---|---|
| `NativeLease` | 删除（E2） |
| `DescriptorLease` | `DescriptorAllocation` |
| `RecordedCommandsLease` | 删除（E4） |
| `RenderFrameUseLease`、`RenderTimelineLease`、`RenderInstanceBorrowerLease`、`CommandContextLease` | 删除（C2、C4），或改名为 `CommandContextHandle` |
| `VulkanSwapchainImageLease` | `VulkanSwapchainImageUse` |
| `RenderGraphResourceOwnership` | `RenderGraphResourceSource { GraphCreated, Registered }` |
| `ClusterResidencyPublication`、`ClusterMeshPublication` | `ClusterResidentGeometry`、`ClusterMeshChanges` |
| `RenderGraphSnapshot`、`ClusterMeshesSnapshot`、`PageStreamSnapshot`、`MeshSnapshot` | `RenderGraphDiagnosticsData`、`ClusterMeshesDiagnostics`、`PageStreamDiagnostics`、`MeshDiagnostics` |
| `ClusterIndirectAbi` | `ClusterIndirectLayout` |
| `ClusterRasterInvocation`、`ClusterShadeInvocation`、`ClusterTessellationInvocation` | `ClusterRasterDispatch`、`ClusterShadeDispatch`、`ClusterTessellationDispatch` |
| `EnsureAuthoring` | `EnsureEditable` |
| `RenderGraphEdit.PersistentAuthoring.cs` | `RenderGraphEdit.Persistent.cs` |
| `FrameSlot`、`QuerySlot`、`VulkanCommandSlot`、`D3D12CommandSlot` | `FrameResources`、`QueryIndex`、`VulkanCommandBuffer`、删除（E3） |
| `RenderInstancePropertyLayout.Contract`、`HasSameContract` | `Layout`、`Matches` |
| `PreparedMeshPages`、`SetRootTablePrepared` 等 `Prepared*` | 按所表达的数据命名，例如 `MeshPageRanges`、`SetRootTableReserved` |
| `PendingBarrierTransition` 等 `Pending*` | 按数据命名，例如 `BarrierTransition` |
| `CacheAdmission`、`PrepareAdmission`、`CommitAdmission` | `CacheEntryRequest`、`ReserveCacheEntry`、`StoreCacheEntry` |

**判定**
- ARCH-01 通过，例外清单只包含第 1 节所述的两类条目。

## 3. wiki 更新清单

每个工作项合入时，同步更新对应的 wiki 页，先改 wiki 再改架构检查：

| 工作项 | wiki 页 |
|---|---|
| A1 | harness/Harness-Performance-Evidence（新增场景加载计时） |
| B1、B2、B9 | shader/Shader-Build、shader/Shader-Composition、shader/Shader-Block-Residency-Refactor |
| B3 | RHI/Pipeline-Cache 下的 Renderer 策略说明 |
| B4 | Assets/Block-Division、cluster/Opaque-Vertex-Data |
| B6、B8 | Assets/Hot-Reload、rendering/GameWorld-and-RenderWorld |
| C1 | 新增 render-graph/Frame-Submission；validation/Execution-Failures |
| C2 | rendering/Frame-CPU-Overlap、rendering/RenderDevice |
| C3 | render-graph/Resource-Access、render-graph/Extension-Points |
| C5 | rendering/Delayed-Feedback、features/Streaming-Feedback |
| D1、D2 | render-graph/Retained-Graph、render-graph/Incremental-Scheduling、render-graph/Incremental-Reconciliation |
| E1~E4 | RHI/Lifetime-Concurrency-and-Diagnostics、RHI/Queue-and-Commands、RHI/Validation-and-Evidence |
| F1~F5 | rendering/Multi-View、cluster/Cluster-Scope、features/Virtual-Shadow-Maps、features/Lighting、features/Translucency |
| 全部 ARCH | harness/Architecture-Checks |

## 4. 验收汇总

| 工作项 | ARCH | BEH | PERF |
|---|---|---|---|
| A1 | | A1 | |
| A2 | | | A2 |
| B1 | 10 | B1 | B1 |
| B2 | 10 | | B2 |
| B3 | 11 | | B3 |
| B4 | 13 | B4 | B4 |
| B5 | | | B5 |
| B6 | 12 | B6a、B6b | B6 |
| B7 | | | B7 |
| B8 | | B8a、B8b | |
| B9 | 15 | | B9 |
| C1 | 05、14 | C1a、C1b、C1c | |
| C2 | 03 | C2 | |
| C3 | 02、04、16 | | |
| C4 | 04、16 | C4 | C4 |
| C5 | 22 | C5 | |
| D1 | 07 | D1 | |
| D2 | | D2a、D2b | D2 |
| D3 | 17、20 | | |
| E1 | 09 | 现有参数错误测试迁移 | |
| E2 | 01 | E2a、E2b | |
| E3 | 08 | | |
| E4 | | | |
| F1 | 06、14 | 现有 VSM 图像测试 | |
| F2 | 18 | 现有 Cluster 图像测试 | |
| F3 | 02、04、14 | 现有 VSM 图像测试 | F3 |
| F4 | 06、19、21 | | |
| F5 | | | |
| G | 01 | | |

渲染相关的 PERF 判定一律使用 `tools/Measure-Scenes.ps1` 的三场景平台，分辨率、场景行为、渲染选项、JIT 配置和采样方式与 A2 基线保持一致。未收敛、失败或缺场景的报告不能作为通过依据。带 trace 的定位结果与不带 trace 的性能结果分开记录。

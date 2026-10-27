# 场景脚本

场景脚本通过 `Application.RunAsync(string[] args, SceneScript script)` 运行应用的真实帧循环。脚本、`ApplicationScene` 与泛型材质启动入口统一返回 `ApplicationResult`，包括成功状态、失败类别、已完成帧数和原始错误。可执行项目的 `Main` 按 `result.Succeeded ? 0 : 1` 转为退出码，库不把结果压缩为退出码。

`SceneScript` 持有内容目录、有限帧数和 `SceneAction` 序列。帧号从 0 开始；动作按帧号排列，同帧动作保持传入顺序。每次启动使用独立窗口线程，窗口与消息队列随该次启动一起结束。

默认环境为 D3D12 软件适配器、隐藏窗口、同步帧、固定 1/60 秒、关闭调试界面和窗口垂直同步。通用脚本不自动安装 DefaultGame 的种子运动或测量回放；需要这些行为时由样例组合它们。命令行可以指定固定帧时间和渲染选项，`--hardware-adapter` 选择硬件适配器。脚本帧数决定本次运行帧数。

## 场景回调与时间

`ApplicationScene.BeforeCameraInput` 与 `SimulateScene` 接收 `SceneEdit` 和绝对场景秒数，不通过累加 `ApplicationFrame.DeltaSeconds` 推导时间。固定步长时使用帧号乘以 `FixedFrameSeconds`；普通运行使用场景时钟，异步更新在安排下一帧任务前取得该次时间。

- 同步帧：同帧的全部脚本动作与 `UpdateFrame` 完成后，执行 `BeforeCameraInput`；相机输入之后执行 `SimulateScene`，再执行几何游戏系统、视图系统和提取。
- 异步首帧准备：在 `Initialize` 后只执行时间为 0 的 `BeforeCameraInput`，随后执行几何游戏系统、视图系统和提取，不执行 `SimulateScene`。
- 异步后续帧：下一帧游戏任务依次执行两个回调，再执行几何游戏系统、视图系统和提取。回调不会因为自身存在而强制切换为同步帧。
- 同步启动的零时间视图更新不执行这两个回调。

这两个回调只用于场景数据编辑。截图、取消帧、相机历史重置与影子刷新请求仍放在脚本动作或 `UpdateFrame` 中；它们不是异步场景数据。DefaultGame 在前一个回调执行测量回放，在后一个回调执行普通种子运动，具体策略不进入通用帧循环。

## 场景变化与帧图像

`SceneEdit` 在动作执行期间提供以下操作：

- `MeshEntities` 枚举当前 Game world 的网格实体；`MeshPositionMin`、`MeshPositionMax` 提供加载时的实例位置范围。
- `GetMaterial` 与 `ReplaceMaterial` 读取、替换实体的材质引用；`Materials` 提供按加载资产顺序排列的材质。传入的材质可以来自场景，也可以由调用方持有至运行结束。
- `SetCameraTransform` 设置相机位置与旋转；`CameraPosition`、`CameraForward` 读取当前相机数据。`SetCameraPosition` 保持方向与世界向上向量，`SetCameraView` 设置位置和目标并保留世界向上向量。
- `AddEntity` 以已有实体的网格、材质创建新实例，并返回其公共 ECS 实体身份；`DeleteEntity` 删除实体。
- `SetOutputSize` 改变窗口客户区尺寸，现有窗口事件和帧循环尺寸检查接着处理交换链变化。
- `CaptureImage` 登记当前帧的图像回调。
- `SetEntityTransform` 更改 Game world 中实体的变换。
- `PointLights`、`SetPointLight` 与 `SetDirectionalLight` 经已有灯光提取路径处理灯光变化。
- `ResetCameraHistory` 在该帧应用相机切换语义；`RefreshShadowMaps` 请求重新生成影子。
- `CancelFrame` 经过场景提取、渲染准备和帧记录，在 GPU 提交边界丢弃该帧。脚本帧号继续推进，已完成帧数和 Temporal 抖动序列按已提交帧推进。取消帧的图像回调随该帧一起丢弃。

动作位于 Game world 提取之前。Game world 与 Rendering world 继续通过已有 extraction 交付变化。脚本场景使用引擎默认的动态实例存储容量，供增删实体及重建时的旧、新批次同时存活。

图像通过现有 presentation 目标的 GPU 回读交付。回调位于 GPU 完成之后，返回该帧号、尺寸和从上到下紧密排列的 RGBA8 像素。每张 `SceneImage` 持有独立的托管数组，可以在应用退出后继续读取。同帧的多个回调收到同一张图像。

```csharp
SceneImage? captured = null;
var script = new SceneScript(contentDirectory, 5,
[
    new(3, edit => edit.CaptureImage(image => captured = image)),
]);
ApplicationResult result = await Application.RunAsync(["--hardware-adapter", "--no-taa", "--no-vsm"], script);
```

失败类别包括启动参数、内容加载、图形操作、设备丢失、适配器或功能支持、帧超时、场景动作、图像回调及渲染过程。动作失败时的已完成帧数指之前完成的帧；图像回调失败时包括已经完成 GPU 渲染的当前帧。

## 内容生成与测试

`tools/SomeEngine.SceneContent` 是共享生成库。`SceneContent.GenerateDefaultAsync` 从默认项目的源码、模型与引擎 shader 包含库构建完整内容；`GenerateSpheresAsync` 复用该构建，创建固定相机和变换的红、绿小球；`RenderScenarios.GenerateAsync` 提供原有 Phong、材质、位移与稀疏运动变体。原命令行工具调用该库。

测试集合以一个实例夹具在临时目录生成内容，结束时删除目录；启动尺寸变体使用单独的临时副本，在对应测试结束时删除。比较材质、相机和实体变化时，把第 3 帧的变化图像与第 0 帧已经具有目标状态的同一场景逐像素比较。绝对颜色验证覆盖整块球体内部区域。

`SceneScript` 的 `GeometryMemoryBytes` 与 `RasterEdgePixels` 配置几何流送内存和光栅阈值，`ShadowSettings` 与 `TemporalSettings` 直接传给已有 Feature。`UseCameraHistory=false` 为每帧生成新历史；`WaitForFrames=false` 运行在途帧，图像回调继续等待对应 GPU 完成。脚本动作始终在 Game world 提取之前执行。

Cluster、VSM、Temporal 的逐项迁移与图像期望见 [图像行为检查](../../harness/Cluster-Image-Checks.md)。

## 软件适配器证据（2026-10-04，票 #86）

软件适配器为 Microsoft Basic Render Driver，驱动 10.0.26100.9278。通过公共设备能力查询得到 64 位 buffer/image atomics 与 Mesh shaders 可用，compute forward progress 为 false。

| 阶段 | 本次 WARP 证据 | 测试分类 |
| --- | --- | --- |
| 内容、网格和页加载 | 完成，67 项默认内容构建成功，两个网格进入首帧提取与准备 | Software 内容失败和设备能力用例默认执行 |
| BVH、场景边界、两阶段剔除、光栅与着色分桶、HiZ、resolve、shade | 完整 Cluster 配置和图记录进入首帧提交；完整帧返回完成超时，独立 compute 阶段的完成状态由 #128 后续定位 | 图像行为场景归 Hardware，原因见 #128 |
| Mesh 光栅 | 使用默认软件适配器分别开启、关闭异步计算，完整组合均返回帧完成超时 | #128 回归用例按票号跳过 |
| VS/PS 光栅 | 使用 `--no-mesh-shaders --no-async-compute` 核对完整组合，也返回帧完成超时，单项约 4 分 10 秒 | #128 回归用例按票号跳过；图像行为归 Hardware |
| 软件光栅、细分 split/dice | 启动的软件适配器分支选择 fixed-function 光栅；高级变体使用 Hardware 类别，#87 承接各渲染行为迁移 | Hardware |

硬件适配器的真实帧循环已完成内存图像、材质替换、相机变换、实体增删及失败类别验收。[#128](https://github.com/ODtian/SomeEngine.Next/issues/128) 跟踪 WARP 完整组合的完成超时，验收要求把可运行场景归回 Software 并移除对应跳过。

尺寸变化用例从 64×64 在第 3 帧改为 80×48，第 4 帧比较图像。本次经窗口尺寸路径触发 `ResizeBuffers` 的 0x887A0001 图形错误；Cluster、Lighting 和 Temporal 的保留输出尺寸也属于同一次尺寸修改的后续验收。[#129](https://github.com/ODtian/SomeEngine.Next/issues/129) 要求修复尺寸变化并移除 `OutputSizeChangeMatchesStartingAtTheNewSize` 的跳过。

## 本票验收命令与结果

- `dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --filter "Category!=Hardware" --no-restore --logger "console;verbosity=minimal" --verbosity quiet -m:1`：21 项通过，2 项按 #128 跳过，0 项失败。
- `dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj --artifacts-path .artifacts/ticket-86-hardware --filter "Category=Hardware" --no-restore --logger "console;verbosity=minimal" --verbosity quiet -m:1`：9 项通过，1 项按 #129 跳过，0 项失败。
- `dotnet build tools/SomeEngine.RenderScenarios/SomeEngine.RenderScenarios.csproj --no-restore --verbosity quiet -m:1` 与测试项目构建：0 警告，0 错误。

临时内容目录、独立验收构建目录与外部采样文件已在使用结束后删除。

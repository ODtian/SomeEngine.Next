本次调整把资源绑定变化与图结构、同步状态变化分开处理。VSM 保留同帧接收面请求、分配、光栅和采样的时序，修正局部光的全图兜底页与方向光的缺页回退。

RenderGraph 的持久结构和显式编辑 API 原先已经存在。默认场景的主要问题不是每帧重新做完整的拓扑分析，而是频繁的绑定变化仍会重建依赖结构已经确定的视图行、同步描述和提交状态结果。

- 输入连接按资源行和范围更新。上传数据变了，但连接和范围未变时，不重新生成所有消费者的访问声明。底层 buffer 变化仍会刷新实际绑定。
- 同规格外部资源轮换只刷新已有视图的描述符，继续使用已有访问关系。规格、范围和资源连接变化仍走相应的局部更新路径。
- 外部资源的同步状态未变时，更新已有 barrier、跨队列交接和提交结果中的资源引用。图管理的内存复用变化继续经过原有同步分析。
- GPU 完成值变化与内容、布局、队列状态变化分开。完成值仍参与跨队列等待；只有状态本身变化才通知边界分析。外部资源与持久资源的当前边界均从资源定义读取。

主要实现位于 `FrameInputs.cs`、`FrameResourceBindings.cs`、`FrameExecutor.cs`、`FrameStateCommit.cs` 和 `FrameSynchronization.cs`。没有添加新的公共更新入口，也没有引入 Game world 到渲染器的额外数据通路。

**验证环境与测量范围**

基线是主工作区提交 `aa8d22fe` 的隔离检出。主工作区另有资产格式和不可变数据迁移：现有 cooked assets 无法由新格式读取，重新生成工具又受未完成的 importer 编译错误阻塞。因此，在隔离检出中重新生成默认场景，用同一套可运行应用、资源和 RHI 交替替换基线与修改后的 RenderGraph DLL。

基线默认动态场景会在 `RenderExtractionContext.RequireMirror` 读取 `RenderSource` 时触发 ECS 嵌套存储声明异常。性能采集显式使用 `--static-scene`；不能把以下结果当成动态场景验证。

默认场景为 1024 个实例，1280×720，Direct3D12，异步 compute 开启。每个进程运行 512 帧，舍弃前 256 帧，按 before/after 顺序交替执行三组；测量期间没有同时运行本任务的构建或 GPU 测试。命令参数为：

```text
--frames 512 --hidden-window --no-vsync --static-scene --trace <新文件路径> --trace-detail
```

用 `SomeEngine.Trace analyze <文件> --warmup 256 --json <报告>` 分析。六份记录均完整，无丢失事件或 scope 错误。RG 和 native 数值是各线程的独占区间之和除以帧数，不能相加后当成 GPU 帧时，也不是仅 `ExecuteCommandLists` 的耗时。

| 组 | RG 修改前 ms/帧 | RG 修改后 ms/帧 | native 修改前 ms/帧 | native 修改后 ms/帧 |
|---|---:|---:|---:|---:|
| 1 | 1.5162 | 0.9706 | 1.8046 | 1.8912 |
| 2 | 2.6768 | 1.0881 | 2.6191 | 1.8611 |
| 3 | 2.0974 | 1.0435 | 2.0479 | 1.8788 |

三组 RG 均值的中位数从 2.0974 ms 降到 1.0435 ms。详细 trace 的记录开销也随被删除的工作减少，因此不能把这个约 50% 的数值直接作为未插桩程序的提速比例。

工作量变化在三组中一致：暖机后 `RowsSetViewRow` 从 240 次/帧降到 0，`StateUpdateConstruction` 从 73 次/帧降到 0。视图描述符与 GPU 完成值仍按实际资源更新，未跳过必要的绑定或同步。

单独的低开销控制采用 `--trace-frames-only`。该控制对的平均帧时间为修改前 5.3300 ms、修改后 5.5903 ms，p95 为 6.7917 ms、7.8675 ms。它未证明整帧提速，且不能用于定位工作线程或 RG 分项成本；本次结论限于消除了上述重复工作，并在详细采集中降低了 RG 独占耗时。

后续增加管理员 CPU 调度记录、四次较长的低开销交替对照和 GPU 硬件时间戳，结果见 [帧时间与占空率报告](Frame-Duty-2026-09-20.md)。新对照在静态默认场景中测得平均帧时间 4.1688 → 3.4714 ms；它补充了本段早期单对照的证据，不改变这里原始记录的数值。

测量 DLL 的 SHA-256：

- 基线：`110AB5B4FD5EA82DA9515666FD713F4ACDE02BE482844BAD5FCFB9318DD68BF1`
- 修改后：`7D0EF40AA611568E4620F43C3F539150DC2FDA89861FDDEFB56A12A68563CF63`

**VSM 的依据和适配**

核查的是 UE5 `ue5-main` 提交 `4c0ddcd77157425cc95a0834a945dc505ef3c11d`，日期 2026-09-19。其主路径仍先标记本帧请求，再分配和渲染阴影页，之后供光照使用：[主渲染调用](https://github.com/EpicGames/UnrealEngine/blob/4c0ddcd77157425cc95a0834a945dc505ef3c11d/Engine/Source/Runtime/Renderer/Private/DeferredShadingRenderer.cpp#L3287-L3310)、[页分配与渲染](https://github.com/EpicGames/UnrealEngine/blob/4c0ddcd77157425cc95a0834a945dc505ef3c11d/Engine/Source/Runtime/Renderer/Private/Shadows/ShadowSceneRenderer.cpp#L889-L904)。本引擎保留这一时序。

局部光通过最后一级 mip 提供全图低分辨率数据，方向光通过更粗的 clipmap 提供回退。UE 会传播已映射层级以加速查找：[粗页标记](https://github.com/EpicGames/UnrealEngine/blob/4c0ddcd77157425cc95a0834a945dc505ef3c11d/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPageMarking.usf#L802-L879)、[层级映射](https://github.com/EpicGames/UnrealEngine/blob/4c0ddcd77157425cc95a0834a945dc505ef3c11d/Engine/Shaders/Private/VirtualShadowMaps/VirtualShadowMapPageManagement.usf#L268-L400)。本次适配使用现有页表和 shader 数据：

- 局部光兜底级别由 `VirtualResolution / PageSize` 推导，保证一页覆盖整个投影。`CoarsePageShift` 改为派生的只读属性，不能再单独设置成一个只覆盖中央区域的级别。
- 方向光标记相机投影周围的页，使用现有 clipmap 层级。采样选择同时检查覆盖范围、页是否驻留和缓存代数，缺页时继续查找更粗的 clipmap。
- 维持本引擎现有精细页与兜底页结构、过滤方式和缓存持有方式；没有复制 UE 的完整局部光 mip 策略、静态/动态双缓存或 SMRT 实现。

**功能验证**

- RenderGraph Debug 全套：78 项通过，包括新增的 1 帧与 3 帧并行容量下输入上传、范围变化和资源替换后的 GPU 数据读回。
- VSM GPU 行为测试：15 项通过，包含局部光四角兜底、方向光最近驻留层级和相机投影页请求。
- VSM 公共设置测试：3 项通过，覆盖不同分辨率和页大小下的全图兜底范围。
- 最终修改在隔离检出中完成默认资源重新生成、Release 应用构建，并以 `--frames 24 --hidden-window --no-vsync --static-scene --gpu-validation --verify-frame-output` 正常结束。输出验证通过，图分析复用为 15/24 帧。

临时检出、执行副本、原始 trace 和中间分析文件在核对结果后删除。主工作区原有的资产迁移改动和已有研究材料未作清理或覆盖。

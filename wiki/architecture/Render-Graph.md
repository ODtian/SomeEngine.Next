# Render Graph

RenderGraph 保存 pass、资源和访问声明，推导本帧的数据依赖、队列执行、同步和内存复用。RHI 提供设备事实、资源、命令录制和提交；RG 消费这些能力。下面描述当前代码的职责和数据流，历史性能报告只代表各自测量时的版本。

## 数据与职责

| 位置 | 保存的事实或结果 | 更新入口 |
|---|---|---|
| `GraphStorage.cs` | 持久声明、当前启用状态、绑定资源、范围和资源边界状态 | 图编辑、帧属性更新、成功提交后的状态写回 |
| `GraphStructureIndex.cs` | 声明的反向索引、显式顺序、结构依赖及资源引用关系 | `GraphEditChanges` 指明的结构修改 |
| `FrameExecutor` 的各个 partial 文件 | 当前执行所需的密集行、活跃集合、队列调度、同步、参数连接和提交分组 | 消费结构、属性及边界变更，更新受影响的派生结果 |
| `ResourceAllocators.cs` | 持久资源与每个在途帧的临时 heap、视图和上传页 | 资源需求、生命周期变化和帧完成 |
| RHI 的资源、命令和队列实现 | 原生对象、描述符和录制/提交期间的引用保留 | 对象创建、录制、提交、完成与释放 |

顺序约束只由现有边表维护；`PassOrder` 仅保存执行位置，不再保存另一份依赖关系或执行另一套拓扑修复。

`GraphStorage` 是声明和当前值的来源。索引和执行数组是这些事实的派生表示，不再用另一套属性字典覆盖原行。`BeginEdit` 共享不可变容器，实际编辑的行才复制；编辑失败保留原图。执行期间若有新编辑提交，原执行仍使用自己的结构，帧结束时只向仍指向同一资源的后续结构传递实际边界状态。

每个在途帧需要各自的临时资源和录制存储，复用前等待该帧所有已提交队列。多个执行缓存服务于这些真实生命周期；它们不是另一套可独立修改的图声明。

## 数据依赖与执行顺序

先确定各资源访问之间的值关系，再计算活跃 pass、调度和内存生命周期。一个资源的用户默认按声明顺序选择值；显式先后约束及其传递路径可以改变这些用户的先后。`FrameAnalysis` 直接从现有顺序边表提取该资源用户之间的约束，按声明优先级选择可执行用户。

全图的一种合法拓扑顺序不能直接当作每个资源的版本顺序。例如已有“写入 → 读取”，给写入加入一个晚声明的无关前置任务，不应让读取改成发生在写入之前。全图调度也不能因为某个早声明的读回等待后面的生产者，就把该生产者移到资源初始化之前。

排序与资源状态使用实际范围：buffer 按字节区间，texture 按 mip/layer/aspect，query 按 query 区间。同一个 pass 对同一 texture 子区间的多条声明先合并成一个访问状态，再生成一次前置转换。声明之间没有 GPU 命令，不能把重复 UAV 声明变成相互依赖的前置 barrier；同一子区间声明不同布局会被拒绝。

执行调度随后满足数据依赖、显式先后和实际同步条件。录制与提交分组可以合并独立工作，不能改变这些条件。被裁剪的 pass 不参与提交后的状态更新。

## 内容、同步和完成信号

这三类事实分别回答三个问题：

- 内容状态：哪些范围已经包含有效数据。
- 同步状态：先前访问使用什么阶段、访问方式、布局及队列。
- `QueueCompletion`：哪些已提交 GPU 工作必须完成。

部分写入不会把已有有效子区间变成未定义。状态更新按既有区间保留内容，只把本次实际提交的同步和 completion 写入受影响范围。合并读取多个已经有效的 query 区间，同样不会丢失其有效性。对整个范围显式声明未定义，仍然会使后续读取失败。

`FrameStateCommit` 只在提交成功后写回。生产队列 release 已提交而消费者提交失败时，保留已 release、未 acquire 的状态、目的队列和原范围，后续执行完成这次交接。普通提交和 late release 使用同一组资源边界存储方法；所有成功提交的 completion 都进入帧的回收依赖。

外部完成信号变化只刷新等待。内容、范围、资源对象或访问声明变化时，才重新推导受影响的依赖和同步。回调仍然为当前帧录制命令，复用提交分组不意味着重放旧帧命令。

## RG 与 RHI 的分界

RHI 的 `DeviceLimits` 提供设备对齐和容量事实，RG 分配器据此安排数据。上传到 shader 可读页的起始位置满足 `ShaderBufferAlignment`，同时满足调用方元素步长；常量与复制分别满足其对应对齐。D3D12 提供 raw view 所需的 16 字节安全对齐；Vulkan 提供 storage/texel buffer offset 限制的共同安全对齐。RG 不包含针对具体后端的对齐分支。

RG 的常量绑定可以直接传递 buffer 和范围。D3D12 在已有 buffer 对象中缓存所需 CPU 描述符；Vulkan 将范围写入原生 buffer descriptor。命令录制仍通过原有引用捕获持有资源，录制后释放调用方对象不会提前销毁已录制工作需要的原生存储。Slang 反射是参数布局来源，RG 的连接表只解析图资源到 RHI 参数值。

RHI 声明 `BarrierBatch` 内的 barrier 相互独立；需要顺序的转换必须分开调用。Vulkan 的队列交接两端使用相同的 General → General 转移，布局转换在交接之外安排，遵守 [Vulkan 同步规则](https://docs.vulkan.org/guide/latest/synchronization_examples.html)。D3D12 的 raw view 对齐来源见 [DirectX 资源对齐说明](https://microsoft.github.io/DirectX-Specs/d3d/D3D12TightPlacedResourceAlignment.html)；Vulkan 对齐来源见 [设备限制](https://docs.vulkan.org/spec/latest/chapters/limits.html)。

## 内存复用与首次访问

只有生命周期不重叠的 device-local 资源可以共享 heap 地址。上传/读回数据在 CPU 侧仍可能同时存活，不能仅凭 GPU pass 不重叠而别名。

前一个占用该地址的资源只提供内存可复用的依赖，不提供新资源的访问状态。新资源先激活，再执行自己的首次完整转换；不能以旧资源的最后一个 pass 作为新资源 split barrier 的 Begin。split barrier 的 Begin/End 保留成对创建的相同范围，不能分别合并两端导致配对失真。

上传页、已建视图和临时分配均由现有分配器维护。完全空闲的 heap 可释放；实际生命周期变化时重新核对受影响的重叠关系。排布和提交合并使用启发式，不声称全局最优。

## 验证和迭代

`ValidationLayer` 检查 RHI 调用的设备、资源、范围、布局和提交合法性；RG 内部逐帧声明检查使用现有开发检查。编辑事务删除失效 ID 必须拒绝，不能误删复用同一位置的新对象。资源耗尽、原生调用失败和设备移除仍是实际运行错误。

普通入口是 `SomeEngine.slnx`。保留资源读回、跨帧内容、动态依赖、描述符复用、提交和生命周期测试。移除了旧 harness、源码拼写/私有形状断言和工作流门禁；性能实验留在 benchmarks 和诊断工具中。测试结果不能跨版本继承，短场景通过也不等于性能目标已达到。

实现入口：[GraphStorage.cs](../../src/SomeEngine.RenderGraph/GraphStorage.cs)、[GraphStructureIndex.cs](../../src/SomeEngine.RenderGraph/GraphStructureIndex.cs)、[FrameAnalysis.cs](../../src/SomeEngine.RenderGraph/FrameAnalysis.cs)、[FrameSchedule.cs](../../src/SomeEngine.RenderGraph/FrameSchedule.cs)、[FrameSynchronization.cs](../../src/SomeEngine.RenderGraph/FrameSynchronization.cs)、[FrameStateCommit.cs](../../src/SomeEngine.RenderGraph/FrameStateCommit.cs)、[ResourceAllocators.cs](../../src/SomeEngine.RenderGraph/ResourceAllocators.cs)、[FrameRecording.cs](../../src/SomeEngine.RenderGraph/FrameRecording.cs)。

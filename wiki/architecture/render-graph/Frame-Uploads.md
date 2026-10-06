# Frame Uploads

## Definition

帧上传把变化字节从图管理的映射页复制到持久 device-local 缓冲。目标缓冲由
`CreatePersistentBuffer` 创建，提供 `CopyDestination` 用途，GPU 始终使用这一份持久数据。
上传页和回读页的复用时机由 RenderGraph 帧槽位的完成点统一决定。

## Opening and starting a frame

```csharp
if (graph.TryOpenNextFrame(out RenderGraphNextFrame next, timeout))
{
    using (next)
    {
        bytes.CopyTo(next.GetWriteSpan(buffer, offset, bytes.Length));
        using RenderGraphFrame frame = graph.BeginFrame(next, options);
        frame.Execute(completions);
    }
}
```

`TryOpenNextFrame` 先收集已完成工作，再选定帧槽位。存在空闲帧槽位时直接使用；全部在途时，
选择最早的在途帧，按同一个超时预算等待各队列完成点。超时返回 `false`，完成后返回下一帧对象。

`RenderGraphNextFrame` 为映射写入和结构编辑提供准备阶段。使用者完成写入任务、提交编辑后，
调用 `BeginFrame(next)` 取得编辑后的图版本，并把下一帧的记录交给帧执行数据。
原有 `BeginFrame(options)` 与 `TryBeginFrame` 通过同一开帧路径继续工作。

`GetWriteSpan` 返回上传页中的最终映射空间，使用者填充声明范围。`WriteBuffer` 直接复制给定
字节。`RenderGraphFrame` 在录制阶段提供同样的两个方法，新增记录与下一帧的记录一起参与本帧。
使用者在开始帧前汇合下一帧写入任务，在执行前完成录制阶段的写入。

## Copy data and graph structure

目标缓冲首次写入时，图安装持久复制 pass，并把它放入根部最前面的上传区段。
后续帧通过范围数据更新这个 pass，结构版本保持原值。删除目标缓冲时，一并删除其上传 pass。

开始帧按目标缓冲收集下一帧记录，合并来源和目标均连续的复制。录制阶段继续加入字节，
执行前合并完整范围集合。重叠范围采用最后登记的字节，裁剪较早记录中的重叠部分，
使每个目标字节参与一次复制。相邻目标范围形成一个普通写入声明；来源也连续时使用一次复制，
其余来源各自复制到对应目标范围。所有复制都先于这些目标的读者执行，保留范围外的原有字节。

首次在录制阶段写入的目标仍按已取得的图版本执行；本帧使用即时复制节点，
图在可提交编辑时为后续帧安装持久复制 pass。调用方的编辑作用域保留自己的暂存结构，
本帧字节通过即时复制完成更新，之后的开帧合并写入时安装持久复制。下一次开始帧取得新增结构。

## Allocation and cancellation

页内分配通过原子偏移递增完成。并行任务各自收集写入记录，开始帧时汇合；
页增长时由图创建和映射新页。页内数据由写入者直接填充，复制依据登记的实际字节范围。

`next.BeginBufferWrites()` 返回一组上传写入。组内的 `GetWriteSpan` 与 `WriteBuffer` 直接使用
同一帧上传页，允许并行任务写入各自范围。写入者汇合后调用 `Commit()`，把这组范围交给
帧上传；释放写入组则丢弃该组记录，帧上的其它写入继续生效。页内空间由图随帧资源回收。

使用者可以通过 `edit.AddBufferUploadPass(buffer)` 显式安装持久上传 pass。
`frame.GetBufferUploadRanges(buffer)` 返回下一帧已提交及录制阶段的上传范围，使用者可以
据此在映射页中清零其补集，初始化字节与业务字节各写入和复制一次。后续写入沿用同一持久 pass。

释放下一帧对象会丢弃全部记录。释放录制帧同样取消本帧写入；持久缓冲继续保存之前的 GPU
内容。成功提交的帧保留映射页，直到该帧槽位的完整队列完成点就绪。

上传页记录已接受提交后的实际缓冲边界状态，并在复用时把这些状态交给图。跨队列复用由
图生成相应的释放、取得与等待；帧槽位的完成点控制复用时机，页上的状态描述上一次 GPU 访问。

参数与状态检查使用 `DevelopmentCheck`；GPU 等待失败、图内存预算耗尽和后端失败沿执行
失败路径处理。

## Declared frame uploads

`DeclareExternalBuffer(description, MemoryType.Upload)` 与 `SetBufferData` 表达具有持久图身份的
当帧上传数据。`GetBufferDataWriteSpan` 返回该帧的最终映射跨度，使用者在执行前填充描述中的
完整范围。每个图帧资源持有自己的实际缓冲；参数输入与视图可以保留声明身份和范围，使
当帧值变化沿用原有绑定位置。图负责映射缓冲的复用、提交与取消。

## Direct GPU-read uploads and computed instance values

`frame.Upload` 支持 Constant、ShaderRead、Vertex、Index 和 CopySource 读取用途。
直接读取页的实际缓冲用途包含这些读取能力；纯复制页仍按 CopySource staging 分类。
元素、常量、shader-buffer 与 staging placement 的对齐由同一页分配器维护。
ImGui 顶点/索引与常量沿用图帧的完成、取消及复用边界，不维护另一条 UI 缓冲时间线。
普通公开启动的 UI 图像在 Debug/Release D3D12 上验证；Vulkan Presentation 尚为能力跳过。

`RenderInstanceWriteSlice.GetWriteSpan<T>` 为紧密存储的属性保留最终图映射范围。
生产者须在同步 prepare 内填满范围，不能在发布后保留跨度；分离的 slice 可并行写入。
Cluster roots/expansions 在该范围内直接计算，组件属性按各自变化行连续范围写入。
放弃组合或下一帧仍丢弃复制记录并要求后续重写，不引入 CPU 回滚值或 GPU 回读备份。
实际相邻不同属性的范围回归仍待完成；源码/验证边界见
[[architecture/validation/Review-Fixes-70]]。

## Behavior evidence

`tests/SomeEngine.RenderGraph.Tests/FrameUploadTests.cs` 使用 WARP 和 RenderGraph 公共 API，
以自给的复制读者和持久回读验证映射写入、字节写入、部分更新、取消、并行任务、录制阶段
写入、结构版本与开帧超时，以及写入组的提交、取消和并行范围。
独立队列门控还验证较早 GPU 工作完成时可以开帧，而较晚工作继续在途。并行场景每帧十六个任务
写入不同范围，持续八帧。`BufferDataCommandsTests` 在三种提交模式中，通过公开 GPU 字节
读取验证声明上传的直接映射、数据改变、缓冲增大和命令数据修改。

## Relations

[[Frame-Data]] · [[Readback]] · [[Retained-Graph]] · [[Graph-Versioning]] · [[Resource-Retirement]]

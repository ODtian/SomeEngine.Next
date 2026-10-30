# RHI-QUE-004 — CommandContext, RecordedCommands and bundles 

A `CommandContext` is created for one Queue type/index and may be an ordinary list or a bundle. Its
recording lifecycle is:

```text
idle -> Begin -> recording -> End/EndBundle or Discard -> reusable idle
```

`CommandRecordingDesc` supplies initial capacities for resource descriptors, sampler descriptors and
captured resources. These are preparation hints, not logical limits.

`End` returns a value-type `RecordedCommands` handle with a sequence-protected payload. Its states
cover executable, submitting, submitted, completed, discarded, Device Lost and caller-disposed
outcomes. Copies share the same terminal sequence. Disposing an unsubmitted handle discards its
native payload; disposing after acceptance does not release GPU dependencies early.

D3D12 可以通过 `TryRepeatCommands` 为上一份已完成的命令列表生成新的单次提交句柄。
这条路径沿已关闭列表重新捕获资源、描述符和参数代际；命令存储继续持有其附件描述符、
普通数据与 shader-visible 堆。下一次 `Begin` 开始新的录制后，原生命令匹配同时比较
命令字节、捕获身份，以及临时 RTV/DSV 的资源、子资源范围、格式和视图维度。
每次被接受的提交都沿队列完成边界保持本次捕获有效。

复用和新录制先尝试现有存储。存储仍在使用时，由所属队列读取已完成位置并交付退休，
随后再次取得存储；已结束的 GPU 工作据此及时交还命令列表和描述符堆。

这两条复用路径分别由 [RepeatCommands](../../../../src/SomeEngine.Graphics.Direct3D12/RepeatCommands.cs)
与 [CommandRecordingReuse](../../../../src/SomeEngine.Graphics.Direct3D12/CommandRecordingReuse.cs) 实现；
临时附件内容由 [CommandCopies](../../../../src/SomeEngine.Graphics.Direct3D12/CommandCopies.cs) 随录制收集。

Vulkan 通过同一 `TryRepeatCommands` 入口，为上一份已完成的原生命令缓冲生成新的单次提交句柄。
命令存储保存资源身份与实际绑定的长期参数、bindless descriptor 代际；复用时核对当前代际，
再沿原捕获集合取得本次 GPU 使用的原生引用和当前 sparse backing。每次完成释放本次捕获，
命令存储继续持有临时 descriptor sets、普通数据上传内容与内置 clear 的原生附件 views。
下一次 `Begin` 重置这些命令内容；参数更新或 descriptor 发布后，新录制取得新代际。
业务资源的原生生命周期由原有资源持有者和逐次提交捕获维护，缓存的资源身份属于 CPU 元数据。
实现见 [VulkanCommands](../../../../src/SomeEngine.Graphics.Vulkan/VulkanCommands.cs)、
[VulkanDescriptors](../../../../src/SomeEngine.Graphics.Vulkan/VulkanDescriptors.cs) 与
[VulkanCopies](../../../../src/SomeEngine.Graphics.Vulkan/VulkanCopies.cs)。

Bundles are caller-disposed Device children and may contain only the command inventory permitted by
the backend. `ExecuteBundle` captures the bundle and its native dependencies.
^rhi-que-004


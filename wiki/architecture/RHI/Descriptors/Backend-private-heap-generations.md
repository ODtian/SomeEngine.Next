# RHI-DSC-004 — Backend-private heap generations 

Shader-visible heap growth, copying and generation switching are backend details. D3D12 may keep CPU
staging descriptors, allocate another shader-visible heap generation and copy stable table slots into
it before one no-throw commit.

Public code observes only stable table indices and explicit `PublishDescriptors`. Heap generation,
fragmentation, temporary descriptor ranges and completion retirement do not become public address
spaces or compatibility values.

D3D12 的录制存储持有本次使用的各个 shader-visible 堆及其描述符内容缓存，包括容量增长和后续绑定索引表时选用的堆。录制存储在队列完成后再次使用，按使用顺序选择已有容量足够的堆，并沿用相同发布代际的已复制前缀。原生命令缓存保存实际使用的各个堆身份；资源和发布代际沿录制捕获与队列完成边界保持有效。

发布代际持有其描述符引用的资源，同时保存涉及稀疏资源的描述符清单。每次录制与命令复用只沿这份清单捕获当前稀疏映射，映射沿录制与队列完成边界保持有效。

当前架构与数据职责可由 [Descriptors](../../../../src/SomeEngine.Graphics.Direct3D12/Descriptors.cs)、[CommandDescriptorHeaps](../../../../src/SomeEngine.Graphics.Direct3D12/CommandDescriptorHeaps.cs)、[CommandBindings](../../../../src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs)、[CommandLifecycle](../../../../src/SomeEngine.Graphics.Direct3D12/CommandLifecycle.cs) 与 [CommandRecordingReuse](../../../../src/SomeEngine.Graphics.Direct3D12/CommandRecordingReuse.cs) 核对。
^rhi-dsc-004


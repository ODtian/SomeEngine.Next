# Vulkan Binding ABI

Vulkan pipeline layout creation consumes cooked SPIR-V 与 Slang# detached reflection/layout。Bounded parameters 形成 pipeline-private descriptor-set layouts；unbounded declarations 使用 device-global descriptor indices。

如果 backend 需要对 SPIR-V decoration 做映射，该映射只属于 Vulkan 实现，不产生第二套公开 shader ABI。command hot path 使用 pipeline creation 已预计算的 native placement，不遍历 reflection 或解析名字。

`WriteDescriptor` 只改变 CPU staging；`PublishDescriptors` 统一提交 descriptor 更新。已经录制并绑定的 command 保留它实际使用的 descriptor generation，后续 publish/dispose 不得改变已接受 GPU work。

参见 [[architecture/shader/Detached-Reflection]]。

# Vulkan Binding ABI

Vulkan pipeline layout 创建直接消费驻留 SPIR-V、`IComponentType`、`ShaderReflection` 与所选 `EntryPointReflection`。Bounded parameters 形成 pipeline-private descriptor-set layouts；unbounded declarations 使用 device-global descriptor indices。

SPIR-V decoration 到原生 set/binding 的映射只属于 Vulkan pipeline 创建结果。后端可以保存该原生对象所需的紧凑映射，但不得公开或持久化一棵通用 Shader ABI。command 热路径使用已经计算的 native placement。

寄存器类别前缀用于区分 Slang 源码坐标。管线创建时，每个原生 parameter set 的 binding 从零连续编号，layout、descriptor writes 与 SPIR-V decoration 共用这份编号；`SourceSet` 与 `SourceBinding` 保留重定位前的来源。每种 bindless descriptor 类型使用自己的 set，唯一的 binding 为零。

Transient parameter descriptor sets 由命令录制存储中的 arena 持有。同一份录制的每次绑定各自使用独立 set；完成或取消后，下一次录制按使用顺序匹配 pipeline 布局对象身份与 set 序号，复用已有分配。布局改变时归还其后的旧分配，录制归还时按本次使用量回收多余分配。命令捕获继续保持实际使用的 pipeline 与资源有效；arena 中的分配保留 descriptor-set layouts 及其 static samplers，最后一份分配归还后释放这组依赖。pipeline 与 pipeline layout 继续沿各自的现有生命周期释放。

每个 parameter block 的 uniform-buffer descriptor 与资源 descriptors 通过一次 `vkUpdateDescriptorSets` 提交。Transient 绑定复用 arena 中的原生写入数组与 descriptor infos；长期绑定创建每版数据时使用调用内的数组。提交期间同时固定这两组数组，使 buffer、image、texel-buffer 及 acceleration-structure 扩展指针保持有效；返回后即可复用写入存储。GPU 使用的资源仍由实际录制捕获或参数代际持有。

同步创建当场消费代码；异步创建持有代码副本和 Slang COM 身份直到完成。pipeline 保留直接反射句柄所需的所有者。

`WriteDescriptor` 只改变 CPU staging；`PublishDescriptors` 统一提交更新。已录制命令保留它实际使用的 descriptor generation，后续 publish/dispose 不改变已接受 GPU work。

参见 [[architecture/shader/Shader-Block-Residency-Refactor]]。

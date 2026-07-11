# 基于 C# / .NET 10 的现代 AAA 级 Graphics 设计

**文档版本**：1.1  
**目标运行时**：.NET 10 / C# 14，64 位  
**最低后端**：Direct3D 12、Vulkan、Metal  
**设计定位**：引擎底层 Rendering Hardware Interface，不是示例封装，不是教学 API  
**本次修订**：将“持久 descriptor storage + 每个瞬态绑定快照使用独立 range/offset + Queue timeline 回收”纳入核心 ABI，并明确 Graphics 与 Render Graph 的职责边界。

---

## 1. 核心结论

本设计采用以下不可动摇的架构决策：

1. **Graphics 不拥有 Shader 编译与反射。** Slang 在 Graphics 之外完成编译、链接、反射、参数布局生成和平台变体生成。Graphics 运行时只接收后端专用字节码、显式数值化的 Pipeline Layout 与普通字节数据。
2. **运行时绑定完全数值化。** 不提供 `SetTexture("Albedo")`、`FindBinding("Camera")`、按变量名绑定、按入口名查找等 API。绑定键为 `(group, binding, arrayElement)`；建议由 Slang 工具链在离线阶段生成强类型 C# 常量。
3. **每个字节码对象只暴露一个入口。** D3D12 使用独立 DXIL；Vulkan 使用入口固定为 `main` 的独立 SPIR-V；Metal 使用仅包含一个公开函数、符号固定的 MetalLib。后端需要字符串时只使用后端内部常量或由哈希生成的内部符号，不接受用户字符串。
4. **核心 API 显式管理内存、同步、队列与生命周期。** 不隐藏上传、不隐藏 Queue Idle、不自动生成 Shader fallback、不在渲染线程创建隐式对象。
5. **使用值类型句柄，不为每个 GPU 对象创建托管对象。** `Device`、`Instance` 等少数顶层对象可以是托管对象；Buffer、Texture、View、Sampler、Pipeline、BindGroup 等均为带代数的 64 位句柄。
6. **同步模型采用 Stage + Access + Layout + Queue Ownership。** 直接贴近 Vulkan Synchronization2 与 D3D12 Enhanced Barriers；Metal 后端按 hazard tracking、barrier、fence、event 和 encoder 边界进行降级映射。
7. **绑定模型同时支持 Persistent Bind Group、Transient Bind Group 与 Bindless Descriptor Arena。** 三者都遵守不可变快照语义；Transient Bind Group 使用持久 descriptor storage 中按 Command Buffer 分配的独立 range/offset，并由 Queue timeline 回收；Bindless slot 默认“写一次、退休后重用”，避免跨后端 update-after-bind 数据竞争。
8. **渲染采用 Dynamic Rendering 语义。** 公共 API 不暴露传统 RenderPass/Framebuffer 对象；Load/Store/Resolve、Render Area、Multiview、Rate Map 等放入 `RenderingInfo`。多子通道融合属于 Render Graph 或可选扩展。
9. **高级特性不压成一个真假布尔值。** Ray Tracing、Mesh Shader、VRS、Sparse、Descriptor Indexing、Device Generated Commands 等拆成细粒度能力和限制查询。
10. **没有“最低公分母式假统一”。** 无法精确跨后端映射的能力进入可选扩展；不通过隐藏 Shader 模拟来假装原生支持。

---

## 2. 范围与非目标

### 2.1 Graphics 负责

- 实例、适配器、设备、队列和表面枚举。
- Buffer、Texture、View、Sampler、Heap、Sparse/Reserved Resource。
- 显式资源状态、barrier、跨队列同步和 CPU/GPU timeline。
- Command Pool、Command Buffer、Render/Compute/Copy encoder。
- Pipeline Layout、Persistent/Transient Bind Group、Bindless Descriptor Arena，以及瞬态 descriptor page/range 的 timeline 回收。
- Graphics、Compute、Mesh、Ray Tracing Pipeline。
- Swapchain、HDR、Present Mode、帧延迟和 presentation 状态。
- Query、Timestamp、Pipeline Statistics、Occlusion、性能计数器扩展。
- 外部内存、外部同步、设备原生句柄、设备丢失与崩溃诊断。
- 内存预算、placement、aliasing、sparse、residency 辅助能力。
- Validation、Capture/Replay、对象命名、GPU marker、breadcrumbs。

### 2.2 Graphics 明确不负责

- Shader 源码解析、编译、链接、反射、热重载策略。
- 根据 Shader 变量名查找绑定。
- Render Graph、材质系统、场景系统、资源流送策略；Render Graph 负责瞬态绑定需求的规划与使用，但不管理原生 descriptor heap/pool/buffer offset。
- 自动 mipmap 生成、滤波 blit、格式转换、压缩解码等需要 Shader 的工具操作。
- 文件 I/O、Pipeline Cache 落盘策略、资产数据库。
- 隐式资源重建和设备丢失后的引擎级恢复。
- MetalFX、DLSS、FSR、XeSS、机器学习张量等高层特性。
- 视频编解码。需要时建立独立 `Graphics.Video` 模块，而不是污染图形 Graphics 核心。

---

## 3. 总体分层

```text
┌──────────────────────────────────────────────────────────────┐
│ Engine: Render Graph / Material / Streaming / Frame Scheduler │
├──────────────────────────────────────────────────────────────┤
│ Slang Offline Toolchain                                      │
│ - compile/link/reflect                                       │
│ - DXIL / SPIR-V / MetalLib                                   │
│ - generated numeric bindings / CPU layouts / asset manifest  │
├──────────────────────────────────────────────────────────────┤
│ Graphics Public Contract                                          │
│ - handles, descriptors, command encoders, timelines          │
├──────────────────────────────────────────────────────────────┤
│ Optional Layers                                              │
│ - validation - capture - state tracker - upload utilities    │
├──────────────────────────────────────────────────────────────┤
│ Graphics Runtime                                                  │
│ - slot maps - lifetime - descriptors - memory - pipeline     │
├──────────────────────────────────────────────────────────────┤
│ D3D12 Backend │ Vulkan Backend │ Metal 3/4 Backend           │
├──────────────────────────────────────────────────────────────┤
│ D3D12/DXGI    │ Vulkan/WSI   │ Metal/CAMetalLayer            │
└──────────────────────────────────────────────────────────────┘
```

引擎可以依赖 Slang 生成的数值布局资产，但 Graphics 本身不能链接 Slang 编译器或反射接口。Graphics 接收的是已经确定的 `PipelineLayoutDesc` 和后端字节码。

---

## 4. Shader 与绑定 ABI

### 4.1 字节码契约

每个 `ShaderModule` 满足：

- 只对应一个 Shader Stage 和一个公开入口。
- 没有公共入口名称参数。
- D3D12：独立 DXIL blob。
- Vulkan：独立 SPIR-V，公开入口固定为 `main`。
- Metal：独立 MetalLib，公开函数固定为 Graphics ABI 规定的名称。
- Ray Tracing 中的多个 Shader 仍然是多个 module；Shader Group 通过 module 索引组合，不通过名称组合。
- 字节码哈希由资产系统提供或由 Graphics 在冷路径计算，用于去重和 Pipeline Cache key；Graphics 不解析资源布局。

建议接口：

```csharp
public Status CreateShaderModule(
    ShaderStage stage,
    scoped ReadOnlySpan<byte> bytecode,
    in Hash128 contentHash,
    out ShaderModuleHandle module);
```

### 4.2 显式数值绑定

每个 group 内的 binding 编号在所有 descriptor kind 之间全局唯一，以同时满足 Vulkan/Metal 的统一 binding 命名空间和 D3D12 的 register-space 映射。

```csharp
public readonly struct BindingKey
{
    public readonly ushort Group;
    public readonly ushort Binding;
}
```

后端映射：

- D3D12：`space = Group`，Shader register = `Binding`，descriptor kind 决定 `b/t/u/s`。
- Vulkan：`set = Group`，`binding = Binding`。
- Metal：group 映射到 argument buffer / argument table，binding 映射到 argument id。

禁止公共 API 出现以下模式：

```text
SetTexture("Albedo", ...)
SetUniform("Camera.ViewProj", ...)
FindBinding("Material")
CreatePipeline(entryPoint: "main")
```

建议 Slang 工具链在 Graphics 外生成：

```csharp
public static class MaterialBindings
{
    public static readonly BindingSlot<SampledTextureKind> Albedo = new(2, 0);
    public static readonly BindingSlot<SamplerKind> LinearSampler = new(2, 1);
}
```

`BindingSlot<TKind>` 仅用于 C# 编译期类型安全，运行时仍为两个整数。

### 4.3 常量布局

Graphics 不理解常量字段，只复制字节。CPU 侧结构由 Slang 离线反射结果生成，并使用显式字段偏移和显式 padding。不得依赖 C# 默认布局推断 Shader ABI，尤其要避免未经验证地使用 `bool`、`Vector3` 或平台相关 packing。

Push Constants 使用显式 byte offset、byte size 和 stage mask。跨后端可移植配置建议不超过 128 字节；更大数据使用常量/统一 Buffer。

### 4.4 Vertex Input ABI

公共 API 只使用数值 `Location`。Vulkan 和 Metal 直接使用 location/attribute index；D3D12 后端使用固定内部 semantic 字符串和 `SemanticIndex = Location`，该字符串不进入用户 API。

---

## 5. 工程与程序集布局

```text
Graphics.Abstractions
  公共枚举、句柄、描述符、状态、结果码

Graphics.Runtime
  句柄池、生命周期、命令公共逻辑、缓存、诊断环形缓冲

Graphics.Backend.D3D12
Graphics.Backend.Vulkan
Graphics.Backend.Metal
  平台后端与原生绑定

Graphics.Validation
  独立 validation layer

Graphics.Capture
  版本化命令捕获与回放

Graphics.Utilities.Upload
  显式上传环、readback、copy footprint 帮助器

Graphics.Extensions.*
  RayTracing、Sparse、ExternalInterop、DeviceGeneratedCommands 等

Graphics.Tests.Conformance
Graphics.Tests.Backends.*
```

Shipping 构建通过源生成的静态注册表选择后端，不依赖运行时反射。桌面工具构建可额外支持动态插件，但不能作为 NativeAOT 的唯一方案。

---

## 6. 公共对象模型

### 6.1 顶层对象

仅以下对象建议是托管引用类型：

- `Instance`
- `Device`
- 可选的 `PipelineFuture` 高层适配器
- 可选工具层的 owned wrapper

GPU 高频对象全部为值句柄：

```text
AdapterHandle, QueueHandle, SurfaceHandle, SwapchainHandle
BufferHandle, TextureHandle, BufferViewHandle, TextureViewHandle
SamplerHandle, HeapHandle, ShaderModuleHandle
BindGroupLayoutHandle, BindGroupHandle, PipelineLayoutHandle
PipelineHandle, PipelineHandle, PipelineHandle
CommandPoolHandle, CommandBufferHandle, QueryPoolHandle
AccelerationStructureHandle, DescriptorArenaHandle
```

### 6.2 句柄格式

推荐 64 位句柄：

```text
bits  0..31 : slot index
bits 32..55 : generation
bits 56..63 : device ordinal
```

每种对象类型使用独立 slot map，因此类型由 C# 静态类型表达，不占句柄位。`0` 永远是 Null。Validation 构建检查 generation、device、对象状态和类型池；Release 构建至少检查 Null 与 device ordinal。

Slot map 使用分段数组，避免扩容移动；每个 slot 存放原生句柄、最少元数据、状态、generation 和延迟销毁信息。不得使用 `Dictionary<Handle, Object>` 作为主存储。

### 6.3 生命周期状态

```text
Free -> Alive -> Zombie/PendingDestroy -> Retired -> Free(next generation)
```

`Destroy(handle)` 不直接释放原生对象。Command Buffer 在录制时收集直接引用资源、View、Persistent Bind Group 版本、Transient Bind Group 所占 descriptor range 和 Pipeline；提交时把它们关联到队列 timeline 值。只有相关 timeline 全部完成后，回收器才能真正销毁或复用。Transient Bind Group 不是全局可销毁对象，不进入 slot map；它的 range 随所属 Command Buffer/Command Pool epoch 自动退休。

Bindless 是例外：Graphics 无法知道 Shader 实际读取了哪个 descriptor index。因此 bindless 资源生命周期由 `DescriptorLease` 管理，slot 在显式退休前保持对资源的引用。

### 6.4 错误模型

热路径不抛异常。所有可能失败的操作返回 `Status`：

```text
Success, NotReady, Timeout, Unsupported, InvalidArgument,
InvalidState, OutOfHostMemory, OutOfDeviceMemory,
OutOfTransientDescriptors, DeviceLost,
SurfaceLost, OutOfDate, Suboptimal, Occluded,
PipelineCompileRequired, CacheMiss, BackendError
```

`ErrorInfo` 保存 backend code、object id、message id 和可选诊断文本。工具层可提供 `ThrowIfFailed()`，核心层不依赖异常控制流。

---

## 7. 设备、能力与限制

### 7.1 Feature 查询

不要定义一个巨大的 `bool SupportsModernFeatures`。使用版本化的 `FeatureId`、`LimitId` 和 format query：

```csharp
public FeatureSupport GetFeature(FeatureId id);
public ulong GetLimit(LimitId id);
public FormatSupport GetFormatSupport(Format format);
public QueueProperties GetQueueProperties(QueueHandle queue);
public DescriptorCaps GetDescriptorCaps();
```

`FeatureSupport` 至少包含：

```text
Availability: Unavailable / Native / ExactEmulation
Tier
Revision
```

昂贵或性能不可预测的 Shader 模拟不能标记为 `ExactEmulation`。

`DescriptorCaps` 至少按 persistent、bindless、transient 三类分别公开后端绑定路径（DescriptorSet / DescriptorBuffer / DescriptorHeap / ArgumentBuffer / ArgumentTable）、资源与 sampler 容量、page 粒度、offset alignment、每 group 最大 descriptor 数、每次 reservation 最大 group 数，以及 high-water/overflow 统计是否可查询。该结构用于预算，不向上层暴露可直接运算的原生 heap address。

### 7.2 Feature 必须细分

示例：

```text
DescriptorNonUniformIndexing
DescriptorPartiallyBound
DescriptorVariableCount
DescriptorUpdateAfterBind
DescriptorBuffer
DescriptorHeap
DirectDescriptorHeapIndexing
MetalArgumentTables

RayAccelerationStructure
RayQuery
RayPipeline
RayMotion
RayIndirectDispatch

MeshShader
TaskShader
MeshIndirect

VrsPerDraw
VrsPerPrimitive
VrsAttachment
VrsCombiners

SparseBuffer
SparseTexture2D
SparseDepthStencil
SparsePlacement
ResidencyFeedback
```

### 7.3 Device 创建

`DeviceDesc` 包含：

- required features 和 optional features。
- queue request：类型、数量、优先级、是否要求 dedicated。
- descriptor budget：Persistent Bind Group、Transient Binding pages、Bindless Arena 和 sampler 分开预算；transient 值表示所有同时在途提交可占用的总量，不是“每 CPU frame 可用量”。
- 默认内存分配策略。
- validation/debug 模式。
- backend-specific option blocks。
- Pipeline Cache 初始 blob。

缺失 required feature 时创建失败，并返回缺失列表；optional feature 由 `DeviceCaps` 记录最终结果。不得静默切换到不等价实现。

### 7.4 Queue 模型

公共 Queue Class：

```text
Graphics, Compute, Copy, Sparse, Present
```

`QueueProperties` 必须指出：

- 支持的命令类别。
- 是否 dedicated。
- 是否与另一个 QueueHandle 实际别名。
- timestamp 支持与有效位数。
- queue family / node 信息的抽象形式。

Metal 后端可返回逻辑 Compute/Copy Queue，但必须通过属性说明是否真正并行或是否共享底层执行资源。

---

## 8. 资源与内存模型

### 8.1 Buffer

`BufferDesc`：

```text
Size: ulong
Usage: BufferUsage flags
MemoryClass: Auto / DeviceLocal / Upload / Readback / Unified
AllocationMode: Automatic / Dedicated / Placed / Sparse / Imported
CreateFlags: DeviceAddress / Aliasable / Protected / ConcurrentQueueAccess
```

Format、stride、raw/structured 属性属于 View，不属于 Buffer 本体。

### 8.2 Texture

`TextureDesc`：

```text
Dimension
Width, Height, Depth
MipLevels, ArrayLayers
SampleCount
Format
Usage flags
MemoryClass
AllocationMode
CreateFlags
```

Cube 是 2D array 加 `CubeCompatible`，不是独立内存维度。允许的 view format 列表通过创建函数的额外 `ReadOnlySpan<Format>` 传入，不能在描述符中保存托管数组。

### 8.3 View

- `BufferViewHandle`：raw、structured、typed、constant、storage，含 offset/size/stride/format。
- `TextureViewHandle`：dimension、format、aspect、mip range、layer range、component swizzle、允许用途。
- Render Target、Depth Stencil、Sampled、Storage 使用同一 TextureView 类型，由创建时用途和 Bind/Attachment 位置区分。

### 8.4 Format 模型

- 不暴露 D3D typeless format。
- 使用具体 format + mutable view format 列表。
- 深度与 stencil 分别建模 aspect。
- 包含 BC、ETC2/EAC、ASTC、PVRTC、HDR、packed 和 multi-plane 类别。
- 不允许后端静默替换 format。
- 对每个 format 查询 sampled、linear filter、storage、atomic、color attachment、blend、depth/stencil、copy、sparse、sample count 等能力。

### 8.5 Heap、Placement 与 Aliasing

核心必须提供：

```text
GetBufferAllocationInfo
GetTextureAllocationInfo
CreateHeap
CreatePlacedBuffer
CreatePlacedTexture
CreateDedicatedBuffer/Texture
CreateSparse/ReservedBuffer/Texture
```

Render Graph 可先查询 size/alignment，再把生命周期不重叠的资源放入同一 heap range。重用前必须插入 `AliasingBarrier`。Validation layer 跟踪 heap range 的 alias epoch。

### 8.6 自动分配器

默认分配器按以下维度隔离池：

- memory type / storage mode。
- resource class 与 heap compatibility。
- protected、device-address、MSAA、sparse、RT AS 等特殊要求。
- host visible/coherent/cached 属性。

实现建议：大资源 dedicated；普通资源使用 TLSF/segregated free list；小 Buffer 使用 slab；空 block 可在内存压力下释放。Graphics 不透明移动资源；使用 GPU address、AS 或外部句柄的资源默认不可移动。

### 8.7 Map、Flush 与 Invalidate

核心提供指针级 map，不长期 pin 托管数组：

```csharp
public Status MapBuffer(
    BufferHandle buffer,
    MapMode mode,
    ulong offset,
    ulong size,
    out MappedMemory mapped);

public void FlushMappedRange(BufferHandle buffer, ulong offset, ulong size);
public void InvalidateMappedRange(BufferHandle buffer, ulong offset, ulong size);
public void UnmapBuffer(BufferHandle buffer);
```

`MappedMemory` 保存 `nint Pointer` 与 `ulong Length`；`Span<T>` 只作为小于 `int.MaxValue` 范围的临时视图。

### 8.8 Upload/Readback

资源创建不接受 initial data，以避免隐藏 copy queue 和同步。独立 `Graphics.Utilities.Upload` 提供：

- persistently mapped upload ring。
- texture subresource footprint 计算。
- staging copy batch。
- 提交后返回 `GpuSignal`。
- readback ring 与异步完成查询。

### 8.9 Sparse 与 Residency

Sparse API 必须公开 tile shape、mip tail、page size、mapping region 和 queue bind。不要只提供 `bool IsSparse`。

Residency 采用可选 `ResidencySet`：

- D3D12：显式 make resident/evict 与优先级。
- Metal：residency set、heap declaration 或 batched `useResource/useHeap`。
- Vulkan：通常映射为 allocation/budget 管理和 no-op declaration。

Bindless Arena 在 Metal 上应绑定 ResidencySet 或 heap domain，不能逐 draw 枚举全局 descriptor 中的资源。

### 8.10 外部资源

提供 Import/Export：

- OS handle type。
- ownership transfer 规则。
- 初始 access/layout/queue owner。
- dedicated allocation 要求。
- 可导入/导出 timeline 或 binary sync。

原生句柄默认 borrowed；显式 export 的 OS handle 使用独立 owner 或 `SafeHandle`。

---

## 9. Descriptor 与资源绑定

Graphics 提供三种互补的绑定生命周期：

1. **Persistent Bind Group**：有全局句柄，可跨帧、跨 Command Buffer 使用，显式销毁。
2. **Transient Bind Group**：descriptor storage 本身持久存在，但每个新内容快照占用不同的临时 range/offset；默认只在一个 Command Buffer 内有效，由提交后的 Queue timeline 自动回收。
3. **Bindless Descriptor Arena**：以稳定 `uint` 索引供 Shader 非均匀访问，slot 通过显式 lease 退休。

三种模型共享同一个数值化 `PipelineLayout` ABI。公共 API 不暴露 D3D12 GPU descriptor handle、Vulkan descriptor-set/buffer address、Metal argument-buffer address 或任何可由上层计算的原生 offset。

### 9.1 Pipeline Layout

`PipelineLayout` 完全由用户提供的数值描述构建，不从 Shader 反射：

```text
BindGroupLayout[0..N)
PushConstantRange[]
Optional immutable samplers
Optional bindless arena declarations
```

`BindingDesc`：

```text
Binding: ushort
Kind: ConstantBuffer / SampledTexture / StorageTexture /
      ReadOnlyBuffer / ReadWriteBuffer / Sampler /
      TexelBuffer / AccelerationStructure
Count: uint
Visibility: ShaderStageMask
Flags: PartiallyBound / VariableCount / DynamicOffset /
       ImmutableSampler / UpdateAfterBind
```

Variable-count binding 为获得统一语义，应限制为 group 的最后一个 binding。同一个 `BindGroupLayout` 可以同时用于 persistent 与 transient 实例；layout 不编码分配位置或生命周期。

### 9.2 Persistent Bind Group

Persistent Bind Group 是可跨提交复用的不可变 descriptor 快照，并由 `BindGroupHandle` 标识。更新采用以下之一：

1. 创建新的 Persistent Bind Group。
2. `BeginPersistentBindGroupUpdate` 产生新版本；旧版本由在途 Command Buffer 持有直到完成。

默认禁止对 GPU 可能正在读取的 descriptor 进行原地修改。显式 `UpdateAfterBind` 仅在能力存在且调用方提供同步保证时启用；它不是材质更新的默认路径。

建议使用无托管分配 writer：

```csharp
public ref struct PersistentBindGroupWriter
{
    public void WriteSampledTexture(ushort binding, uint element, TextureViewHandle view);
    public void WriteStorageTexture(ushort binding, uint element, TextureViewHandle view);
    public void WriteBuffer(ushort binding, uint element, BufferViewHandle view);
    public void WriteSampler(ushort binding, uint element, SamplerHandle sampler);
    public void WriteAccelerationStructure(
        ushort binding,
        uint element,
        AccelerationStructureHandle accelerationStructure);

    public Status Commit(out BindGroupHandle group);
}
```

Persistent Bind Group 自身持有所引用 View、Sampler 和 Acceleration Structure 的强引用；销毁 handle 后，实际 descriptor storage 与资源引用仍按最后一次 GPU 使用的 timeline 延迟释放。

### 9.3 Transient Bind Group

#### 9.3.1 规范语义

Transient Bind Group 的核心模型是：

```text
Device-persistent descriptor storage
    ├── persistent region
    ├── bindless region
    └── transient page/range ring
            ├── snapshot A @ range/offset A
            ├── snapshot B @ range/offset B
            └── snapshot C @ range/offset C
```

必须满足以下规则：

- 每个**新 descriptor 内容快照**在提交前获得独立且不会被覆盖的 range；在相关 GPU 工作完成前，该 range 不得复用。
- 同一个 `TransientBindGroup` 可被多次 `SetBindGroup`，重复绑定不再次分配、不复制 descriptor。
- `SetBindGroup` 永远只是状态绑定操作，不能隐式创建 descriptor、增长 heap、等待 GPU 或触发新版本。
- transient 的含义是 range 生命周期短，而不是底层 heap/pool/buffer 每帧创建和销毁。
- 安全回收依据实际 Queue timeline，不依据 `frameIndex % FramesInFlight`、Pass callback 返回、Command Buffer 录制结束或 CPU 帧结束。
- 一个逻辑 group 在后端可能同时占用 resource range 与 sampler range，也可能只是一个 `VkDescriptorSet`；公共 token 因此不是裸 offset。

#### 9.3.2 公共 API 形态

Transient token 不进入全局 handle slot map，也没有 `Destroy`。使用 stack-only 类型阻止装箱、跨 `await`、写入普通对象字段或长期缓存：

```csharp
public readonly ref struct TransientBindGroup
{
    internal readonly nuint Token;
    internal readonly uint Epoch;
    internal readonly uint CommandBufferGeneration;
}

public ref struct TransientBindGroupWriter
{
    public void WriteSampledTexture(ushort binding, uint element, TextureViewHandle view);
    public void WriteStorageTexture(ushort binding, uint element, TextureViewHandle view);
    public void WriteBuffer(ushort binding, uint element, BufferViewHandle view);
    public void WriteSampler(ushort binding, uint element, SamplerHandle sampler);
    public void WriteAccelerationStructure(
        ushort binding,
        uint element,
        AccelerationStructureHandle accelerationStructure);

    public Status Commit(out TransientBindGroup group);
}

public readonly struct TransientBindGroupReservation
{
    public readonly BindGroupLayoutHandle Layout;
    public readonly uint GroupCount;
    public readonly uint MaxVariableDescriptorCount;
}
```

Command recording API 提供：

```csharp
public ref struct CommandEncoder
{
    public Status ReserveTransientBindGroups(
        scoped ReadOnlySpan<TransientBindGroupReservation> reservations);

    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);
}

public ref struct RenderEncoder
{
    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public void SetBindGroup(
        uint groupIndex,
        BindGroupHandle group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);

    public void SetBindGroup(
        uint groupIndex,
        in TransientBindGroup group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);
}
```

`RenderEncoder` 与 `ComputeEncoder` 都提供 `BeginTransientBindGroup` 以及 Persistent/Transient 两组 `SetBindGroup` overload，使 pass callback 可在活动 scope 内构造绑定；所有 writer 都必须绑定到同一个 backend command context。

Dynamic offset 是 bind-time 参数，不要求重写 descriptor。只有 descriptor 内容或不能原生动态表达的 buffer range 改变时，才创建新的 transient snapshot。

#### 9.3.3 生命周期、Command Pool epoch 与 Queue timeline

每次成功 `ResetCommandPool` 开启新的 epoch。Command Buffer 从该 epoch 的 transient allocator 租赁 page 或 page slice，默认由该 Command Buffer 独占其逻辑分配区：

```text
Free
  -> Recording(commandBuffer, epoch)
  -> Sealed
  -> InFlight(queue, timelineValue)
  -> Free
```

流程：

1. `ResetCommandPool` 开启 epoch，并确认旧 epoch 的原生命令内存和 descriptor pages 已可安全复用；若仍有 Pending Command Buffer，返回 `NotReady`/`InvalidState`，不得隐式等待。
2. `ReserveTransientBindGroups` 可提前租赁足够 page；未 reserve 的录制允许按需从预创建的 free-page 池取页。
3. `Commit` 在线性分配器中保留 range、写入 descriptor，并把资源引用登记到当前 Command Buffer。
4. `End` seal 所有已使用 range，禁止后续写入。
5. `Submit` 将 range 或 page slice 与返回的 `GpuSignal` 关联。
6. `GetCompletedValue` 达到 signal 后，回收器推进 ring tail 或把 page 放回 free list。
7. 未提交的 Command Buffer 被 discard/reset 时，可直接回收其未在途 range。

物理 page 可以在内部由多个 Command Buffer 分片共享，但 page 只有在所有 slice 都退休后才能整体 reset；实现也可以选择一 Command Buffer 一组 page 以简化状态机。核心路径要求同一活动 page 的所有 slice 归属同一个 Queue timeline；若要跨 Queue 共享 page，必须显式保存多个 completion signal，不能用单一 FIFO tail 推断安全。任何情况下，上层都不能直接重置原生 descriptor pool 或覆盖 ring offset。

默认 `TransientBindGroup` 只能被创建它的 Command Buffer 使用；跨 Command Buffer、跨 Command Pool epoch、跨 Device 或跨 Queue 使用均非法。确需批次级共享时，应定义单独的可选 `TransientBindingArena` 扩展，并让它显式跟踪多个提交 signal；核心 API 不承担多 Queue timeline 向量的复杂度。

#### 9.3.4 Reservation、容量与溢出

Render Graph 可在编译后按 layout 聚合每个 Command Buffer 的需求，并在录制命令前调用 `ReserveTransientBindGroups`。若 reservation 返回 `Success`，Graphics 必须保证在声明的 group 数和 variable-count 上限内，不会再因为 transient descriptor 容量不足而失败；`DeviceLost` 等外部故障除外。

未 reservation 或超出 reservation 的 `Commit` 可以按需分配，但容量耗尽时必须返回：

```text
OutOfTransientDescriptors
```

禁止以下隐式恢复：

- 等待某个旧 frame 或 Queue idle。
- 在 D3D12 Command List 中途切换到新 shader-visible heap。
- 临时创建不受预算约束的新大 heap/pool/buffer。
- 静默改用功能或性能不等价的绑定路径。
- 覆盖仍被 GPU 读取的旧 offset。

Device 必须提供 transient page 数、resource/sampler descriptor 使用量、字节使用量、reservation 命中率、ring wrap 次数、high-water mark 与 overflow 次数等统计。预算按“同时在途的总 descriptor storage”计算，而不是简单乘以固定 FramesInFlight。Dedicated oversized group 可以使用独立 transient page，但仍受总预算和 timeline 回收规则约束。

#### 9.3.5 资源引用、Metal residency 与销毁竞态

`TransientBindGroupWriter.Commit` 将所有写入的 View、Sampler、Buffer、Texture 和 Acceleration Structure 登记到当前 Command Buffer 的 `ResourceUseSet`：

- 即使应用在提交前调用 `Destroy(view/resource)`，底层对象仍被 Command Buffer 引用保持到 completion。
- Metal 后端从同一记录生成 `useResource/useResources/useHeap` 或 residency-set 信息，并按 encoder 批量去重。
- 只把 View 写入 descriptor 不等价于声明资源状态；Barrier/Render Graph usage 仍是同步规范来源。
- descriptor range 退休和资源对象退休可以共享同一个 completion record，但必须分别统计与验证。

#### 9.3.6 Secondary、Bundle 与可重复执行

可重复执行的 D3D12 Bundle、Vulkan Secondary Command Buffer、Metal ICB/可重用命令对象默认禁止捕获 command-local Transient Bind Group，因为 descriptor range 可能在第一次执行完成后被回收。

允许的模型只有：

1. Reusable secondary/bundle 仅引用 Persistent Bind Group、Bindless Arena 和显式长期存活资源。
2. 标记为 `OneShot` 的 secondary 可以使用 transient group，但其 range 必须转移给最终 primary submission，并且只允许执行一次。
3. 可选 pinned transient arena 明确占用 descriptor budget，直到用户提供的所有 signal 完成；该能力不得作为默认 Render Graph 路径。

### 9.4 Graphics 与 Render Graph 的职责边界

原则：**Render Graph 决定需要什么绑定以及何时录制；Graphics 决定绑定存放在哪里、如何映射到后端以及何时可安全复用。**

Graphics 负责：

- persistent/bindless/transient descriptor storage 创建、分区和预算。
- page、range、set、argument-buffer/table allocation 与后端 alignment。
- descriptor 编码、资源引用保持、Metal residency 信息收集。
- Command Buffer epoch、Queue timeline 退休、discard 和异常路径回收。
- 多线程 page/chunk 分配、overflow、统计、validation 和 capture。

Render Graph 负责：

- 根据 pass、pipeline layout 和 variable descriptor count 计算 reservation。
- 在 pass 录制阶段构造 transient snapshot，并复用同一 snapshot 的多次 bind。
- 验证 descriptor 中的资源已在 pass usage 中声明。
- 按 Global/View/Pass/Material/Draw 更新频率拆分 group。
- 选择持久、瞬态或 bindless 生命周期，避免不必要的 per-draw descriptor 复制。
- 可选地在同一 Command Buffer 内对完全相同的 snapshot 做 hash 去重。

Render Graph 不得保存原生 heap address、GPU descriptor handle、`VkDescriptorSet`、descriptor-buffer offset、argument-buffer offset，也不得按 frame index 调用原生 pool reset。

### 9.5 Bindless Descriptor Arena

Bindless 使用 typed arena：

```text
SampledTextureArena
StorageTextureArena
BufferArena
SamplerArena
AccelerationStructureArena
```

GPU 侧索引为 `uint`。CPU 侧 `DescriptorLease<TKind>` 包含 arena、index、generation。

默认语义：

- slot 分配后写入一次。
- slot 被 Shader 使用期间不可改写。
- 替换资源时分配新 slot，并在上层数据中切换 index。
- 调用 `RetireDescriptor(lease, signals)` 后进入 quarantine。
- 所有关联 Queue timeline 完成后才可重用 index。
- descriptor lease 持有底层资源引用，因此资源不会先于 descriptor slot 销毁。

该模型比“随时 update-after-bind”更容易在 D3D12、Vulkan 和 Metal 上得到确定行为。Transient Bind Group 不应通过反复借用 bindless slot 模拟，两者的索引稳定性和回收协议不同。

### 9.6 后端映射

#### 9.6.1 D3D12

Device 建立长期存活的 shader-visible CBV/SRV/UAV heap 和 sampler heap，并在内部划分 persistent、bindless 与 transient page。D3D12 同时只能绑定一个 CBV/SRV/UAV heap 和一个 sampler heap；因此正常录制期间不得靠切换 heap 解决 transient overflow。

Transient Commit 的典型实现：

1. 从 resource page 分配连续 range；如 layout 含非 immutable sampler，再从 sampler page 分配第二个 range。
2. 从 CPU staging descriptor 复制，或直接在目标 CPU descriptor handle 上创建 descriptor。
3. 在 backend token 中保存 heap generation、resource GPU handle、sampler GPU handle 和 layout id。
4. `SetBindGroup` 只设置 root descriptor table；同一 token 重复 bind 不复制 descriptor。
5. Fence 完成前禁止改写相应 slot。

Sampler heap 容量通常远小于 resource heap，因此：

- Sampler 对象全局去重。
- Immutable sampler 优先放入 Pipeline Layout/Root Signature。
- 常用 sampler 使用 persistent slot。
- transient sampler table 可按内容在 Command Pool epoch 内去重。

Root CBV/SRV/UAV、root constants 或直接 heap indexing 可以作为 layout 编译期 fast path，但不能改变 Persistent/Transient Bind Group 的公共快照语义。

#### 9.6.2 Vulkan

Vulkan 后端至少支持以下路径：

1. **Descriptor Set 基线路径**：`VkDescriptorPool` page 长期存在；每个 Command Buffer/epoch 线性分配 transient `VkDescriptorSet`，不做单 set free。所有引用该 page 的提交完成后才整体 reset/recycle。该路径没有公开字节 offset，但满足相同的“新快照占独立 storage、timeline 后复用”语义。
2. **`VK_EXT_descriptor_heap` 优选路径**：支持时使用长期 resource/sampler descriptor heap，并为 transient snapshot 分配独立 index/range；公共 token 记录后端 heap mapping，不向上层暴露 index 计算规则。
3. **`VK_EXT_descriptor_buffer` 兼容 fast path**：使用长期映射的 descriptor buffer；缓存 set layout size、每个 numeric binding 的实现定义 byte offset、descriptor size 和 required alignment，随后按 setOffset 分配 byte range，并在 Command Buffer 中设置 offset。

Graphics 必须把普通 descriptor set、descriptor heap 与 descriptor buffer 的差异封装在 backend payload 中。Dynamic offsets 可以映射到原生 dynamic descriptor、push data/device address，或在需要时烘焙进新 transient snapshot；公共 API 不因为后端路径变化而改变。

Descriptor pool/page reset 必须基于实际 Queue completion。按 CPU frame 固定轮转只可作为 page 选择启发，不能作为安全证明。

#### 9.6.3 Metal 3/4

Metal 3 使用长期存活的 `MTLBuffer`/heap page 作为 argument-buffer storage。每个 transient snapshot 在 page 中取得满足 layout alignment 的不同 offset，通过 `MTLArgumentEncoder.setArgumentBuffer(..., offset)` 或 Metal 3 的直接结构写入方式编码；绑定时使用相同 buffer 的对应 offset。

Backend token 同时保存：

- argument buffer/table 对象与 offset/index。
- layout generation。
- resource/heap usage 汇总或 residency-set 引用。
- 必要的 stage visibility 信息。

Metal 4 支持时可把同一公共语义映射到 argument table allocation，并把 allocator reset 与 GPU completion 绑定。Argument table、command allocator、residency set 均是后端优化，不进入公共 ABI。

对 argument buffer/table 间接引用的资源，后端必须在相关 encoder 上批量发出 `useResource/useResources/useHeap` 或 residency-set 声明。只写入 argument buffer 而不处理 residency 是无效实现。

### 9.7 Null Descriptor

只有 `NullDescriptor` 能力存在时才允许真正空绑定；否则 Device 创建一组格式兼容的 dummy resource。Slot 0 建议保留给 Null/Dummy。Transient writer 写入 Null 时仍必须进行 layout/type validation，但不建立普通资源强引用。

### 9.8 更新频率与容量设计指南

推荐按更新频率拆分 Pipeline Layout：

```text
Group 0: Global / bindless，持久
Group 1: Frame / view，帧版本或 submission transient
Group 2: Pass，transient
Group 3: Material，persistent 或 bindless
Group 4: Draw，少量 transient buffer/address/constants
```

不要因提供 transient API 就为每个 draw 复制完整材质表：

- 只有 buffer offset 改变时，优先 dynamic offset、root descriptor、device address 或 push constants/data。
- 材质纹理通常使用 Persistent Bind Group 或 bindless index。
- Sampler 尽量 immutable、persistent 和去重。
- 完全相同的 transient snapshot 可以在同一 Command Buffer/epoch 内去重，但去重不是语义要求。
- Variable-count 大表应使用 dedicated page 或 bindless arena，而不是挤压小 group ring。

---

## 10. 同步与 Queue Timeline

### 10.1 状态模型

资源状态不使用单一“大枚举”。Texture 状态由以下维度组成：

```text
PipelineStageMask
AccessMask
TextureLayout
Queue ownership
SubresourceRange
```

Buffer 没有 layout，但有 stage/access/queue owner/range。

建议 Stage 至少覆盖：

```text
DrawIndirect, IndexInput, VertexInput,
Vertex, TessControl, TessEvaluation, Geometry,
Task, Mesh, Fragment,
EarlyDepthStencil, LateDepthStencil, ColorOutput,
Compute, RayTracing, AccelerationStructureBuild,
Copy, Resolve, Clear, Host,
AllGraphics, AllCommands
```

Access 至少覆盖：

```text
IndirectRead, IndexRead, VertexRead, ConstantRead,
ShaderSampledRead, ShaderStorageRead, ShaderStorageWrite,
ColorRead, ColorWrite,
DepthStencilRead, DepthStencilWrite,
TransferRead, TransferWrite,
HostRead, HostWrite,
AccelerationStructureRead, AccelerationStructureWrite,
ShaderBindingTableRead, MemoryRead, MemoryWrite
```

Texture Layout 至少覆盖：

```text
Undefined, General, ShaderReadOnly,
ColorAttachment,
DepthStencilAttachment, DepthStencilReadOnly,
DepthReadOnlyStencilAttachment, DepthAttachmentStencilReadOnly,
TransferSource, TransferDestination,
ResolveSource, ResolveDestination,
Present, ShadingRate, Feedback
```

### 10.2 Barrier 类型

```text
GlobalBarrier
BufferBarrier
TextureBarrier
AliasingBarrier
```

Barrier 支持：

- 精确 Buffer range。
- texture aspect/mip/layer range。
- Discard。
- split begin/end。
- cross-queue release/acquire。
- alias before/after resource。

UAV barrier 不作为特殊公共概念；它是 `ShaderStorageWrite -> ShaderStorageRead/Write` 的 memory dependency。

### 10.3 显式与自动状态

核心 Graphics 永远显式。可选 `Graphics.StateTracking` layer 可根据声明式 resource usage 生成 barrier，但它属于 Render Graph/帮助层，不改变核心语义。

Swapchain acquire/present token 的平台二进制同步由后端内部处理，但 Texture 从/到 `Present` layout 的转换仍由调用方显式发出。

### 10.4 Timeline

每个 Queue 有单调递增 `ulong` timeline。每次成功提交自动 signal 新值并返回：

```csharp
public readonly struct GpuSignal
{
    public readonly QueueHandle Queue;
    public readonly ulong Value;
}
```

提交接口：

```csharp
public Status Submit(
    QueueHandle queue,
    scoped ReadOnlySpan<CommandBufferHandle> commandBuffers,
    scoped ReadOnlySpan<TimelineWait> waits,
    out GpuSignal completion);
```

后端：

- D3D12：Fence。
- Vulkan：Timeline Semaphore。
- Metal：Shared Event / Event value。

CPU 提供 `GetCompletedValue`、`Wait(value, timeout)` 和冷路径 `WaitAsync`。取消等待不取消 GPU 工作。Queue timeline 同时是 deferred destruction、Bindless lease quarantine 和 Transient Bind Group range/page 回收的唯一规范完成依据；逻辑 frame 编号只能用于统计和容量规划。

### 10.5 跨 Queue

资源跨 Queue 写后读必须同时满足：

1. 源 Queue release barrier。
2. 源 Queue signal timeline。
3. 目标 Queue wait timeline。
4. 目标 Queue acquire barrier。

同一底层 Queue 的逻辑别名可以由后端合并，但公共语义不变。

### 10.6 Metal 映射规则

- tracked resource 可以让 Metal 自动完成部分 hazard tracking，但 Graphics barrier 仍是规范来源。
- aliasable/transient heap 推荐 untracked + 显式同步。
- 同 Queue encoder 间使用 barrier/fence。
- 跨 Queue 使用 event/shared event。
- 无法在 active render encoder 内精确表达的 dependency 通过结束并重开 encoder 实现。
- 核心 API 默认禁止在 Rendering scope 内发出任意通用 barrier；attachment feedback/local-read 通过独立扩展开启。

---

## 11. Command Buffer 与 Encoder

### 11.1 生命周期

统一采用 one-shot primary Command Buffer：

```text
Initial -> Recording -> Executable -> Pending -> Recyclable
                 |
                 +----------> Discarded ----> Recyclable
```

Metal 原生 Command Buffer 本身不可重用，因此公共 API 不允许 primary Command Buffer 被重复 submit。完成后通过 Command Pool 回收并创建/重置新的后端对象。

`CommandPool` 与 Queue Class 绑定，录制期间 thread-affine；不同 Pool 可并行录制。Queue submit 在每个 Queue 上序列化。每次成功 `ResetCommandPool` 开启一个新的 epoch，并同时重置该 pool 的 command scratch、barrier scratch 和**已经安全退休**的 transient descriptor pages。

Transient Bind Group 的默认所有者是 Command Buffer：录制时分配 range，`End` 时 seal，`Submit` 时附着 completion signal，completion 后回收。Command Buffer 被 discard 时，其未提交 range 可直接归还。Command Pool 不得在任一关联 Command Buffer 仍为 `Pending` 时 reset，也不得因为开始了新 CPU frame 就覆盖旧 epoch 的 descriptor offset。

### 11.2 C# API 形态

Encoder 使用 `ref struct`，不能装箱、不能跨 `await`、不能逃逸到堆：

```csharp
public ref struct CommandEncoder
{
    public Status ReserveTransientBindGroups(
        scoped ReadOnlySpan<TransientBindGroupReservation> reservations);

    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public void Barriers(
        scoped ReadOnlySpan<GlobalBarrier> globals,
        scoped ReadOnlySpan<BufferBarrier> buffers,
        scoped ReadOnlySpan<TextureBarrier> textures,
        scoped ReadOnlySpan<AliasingBarrier> aliases);

    public RenderEncoder BeginRendering(scoped in RenderingInfo info);
    public ComputeEncoder BeginCompute(scoped in ComputePassInfo info);
    public CopyEncoder BeginCopy(scoped in CopyPassInfo info);

    public void WriteTimestamp(QueryPoolHandle pool, uint query, PipelineStageMask stage);
    public Status End();
}
```

`RenderEncoder`、`ComputeEncoder`、`CopyEncoder` 使用 pattern-based `Dispose()` 结束 scope，不依赖接口装箱。Validation 使用 scope generation 防止复制后 double-end。Render/Compute encoder 分别为 Persistent 与 Transient Bind Group 提供 overload；前端 state cache 保存 transient token 时必须同时保存 Command Buffer generation，不能让 token 逃逸到下一个 epoch。

### 11.3 RenderingInfo

最多 8 个 color attachment，使用 C# inline array，避免托管数组：

```text
ColorAttachment[8]
DepthAttachment
StencilAttachment
RenderArea
LayerCount
ViewMask
Optional shading-rate attachment / rasterization-rate map
Optional programmable sample positions
```

每个 attachment 包含：

```text
View
LoadOp: Load / Clear / DontCare
StoreOp: Store / DontCare
ClearValue
ResolveView
ResolveMode
ReadOnly flag where applicable
```

Attachment 状态必须在 `BeginRendering` 前完成转换；Rendering scope 本身不隐式改状态。

### 11.4 Render 命令清单

- SetRasterPipeline / SetMeshPipeline。
- SetBindGroup / SetBindlessArena / PushConstants。
- SetVertexBuffers / SetIndexBuffer。
- SetViewport(s)、Scissor(s)、BlendConstants、StencilReference、DepthBias、DepthBounds。
- SetPrimitiveTopology；更广泛动态状态按能力开放。
- Draw、DrawIndexed。
- DrawIndirect、DrawIndexedIndirect、IndirectCount。
- DrawMeshTasks、MeshIndirect。
- Begin/End Occlusion Query。
- SetPredication / Conditional Rendering，能力受限。
- Execute Secondary/Render Bundle。
- SetShadingRate、SetSamplePositions。
- Transform Feedback/Stream Output 作为可选扩展。

### 11.5 Compute 命令清单

- SetComputePipeline。
- SetBindGroup / Bindless / PushConstants。
- Dispatch、DispatchIndirect。
- Build/Update Acceleration Structure。
- Device Generated Commands 扩展。
- Encoder 内受限 memory barrier，具体能力查询。

### 11.6 Copy 命令清单

- CopyBuffer。
- CopyBufferToTexture / CopyTextureToBuffer。
- CopyTexture。
- ResolveTexture。
- FillBuffer。
- 小数据 `UpdateBuffer`，大小上限由 limit 查询。
- ClearColorTexture / ClearDepthStencilTexture，仅在后端可提供原生等价语义时支持。

不提供核心 `GenerateMips`、filtered blit、arbitrary format conversion；这些属于显式工具 Pipeline。

### 11.7 并行录制

提供两种模型：

1. 多个独立 primary command buffer，由 Render Graph 排序并批量 submit。
2. `SecondaryRenderCommandBuffer` / `RenderBundle`，只能记录 rendering scope 内允许的 draw/state 命令，使用 `RenderingInheritanceInfo` 描述 attachment formats/sample count/view mask。

后端可映射为 D3D12 Bundle、Vulkan Secondary Command Buffer、Metal Parallel Render Encoder 或 ICB。可重复执行能力必须单独查询，不能把所有 secondary 都宣称为 reusable。Reusable secondary/bundle 默认禁止引用 command-local Transient Bind Group；OneShot secondary 只有在其 descriptor range 随最终 primary submission 一起退休时才可使用。

---

## 12. Pipeline 系统

### 12.1 Pipeline Layout 独立存在

所有 Pipeline 显式引用 `PipelineLayoutHandle`。Pipeline Layout 可以缓存和复用；绑定兼容性按 group 前缀中相同 layout handle 判断。切换 Pipeline 时，兼容 group 可保留，其他绑定失效。

### 12.2 Graphics Pipeline

描述符完整包含：

```text
PipelineLayout
Vertex/Fragment modules
Optional TessControl/TessEvaluation/Geometry modules
VertexLayout
Primitive topology class / patch control points
Rasterizer state
Multisample state
Depth-stencil state
Blend state[8]
Color formats[8]
Depth/stencil format
Sample count
View mask
Dynamic state mask
Pipeline flags
```

Geometry Shader 在 Metal 不可用，必须 feature gate。Tessellation 和 Mesh Pipeline 分别建模，不通过空字段猜测。

### 12.3 Compute Pipeline

只包含 layout、compute module、可选数值 specialization 和 flags。Graphics 不查询 threadgroup size；dispatch group 计算由资产 manifest/引擎负责。

### 12.4 Mesh Pipeline

Task/Amplification module 可选，Mesh module 必需，Fragment module 可选。提供独立 `PipelineHandle` 或统一 `PipelineHandle` 加 pipeline kind；推荐独立 kind，减少非法状态组合。

### 12.5 Specialization

Specialization Constant 仅使用数字 ID。由于 D3D12 不提供等价运行时 specialization，跨后端生产配置应优先由 Slang 离线生成 permutation bytecode。Graphics 的 specialization API 属于可选能力，不得在 D3D12 上偷偷触发运行时 Shader 编译。

### 12.6 Pipeline Cache

Cache key 必须由规范化数据构成：

```text
Graphics ABI version
Backend and device fingerprint
Driver/runtime version
Shader content hashes
Pipeline layout hash
Normalized pipeline descriptor
Enabled feature variant
```

禁止直接哈希带未初始化 padding 的 C# struct。所有 key 使用显式序列化。

统一 Cache API 接收和导出 opaque bytes；文件读写在引擎层。后端映射：

- D3D12 Pipeline Library / shader cache。
- Vulkan Pipeline Cache，支持时使用 Pipeline Binary。
- Metal Binary Archive，Metal 4 可使用 pipeline dataset serialization。

### 12.7 异步 Pipeline 创建

Graphics 可提供 value-handle future：

```text
RequestRasterPipeline -> PipelineFutureHandle
PollPipeline
WaitPipeline
GetPipelineResult
```

D3D12/Vulkan 在 Graphics worker pool 创建 PSO；Metal 使用原生异步接口或 worker。`ValueTask` 只是冷路径适配器。Render thread 不等待未知 Pipeline；上层使用预热、fallback pipeline 或跳过 draw。

---

## 13. Ray Tracing

### 13.1 公共部分

所有后端共享：

- BLAS/TLAS 对象。
- Triangle、AABB、Instance geometry。
- Build size query。
- Build、Update/Refit。
- Scratch buffer。
- Clone、Compact。
- Post-build compacted size query。
- AS descriptor 绑定。
- Ray Query 能力。

### 13.2 Pipeline 部分

Ray Pipeline 不使用字符串 export。每个 shader module 是单入口，`RayShaderGroupDesc` 用 module index 组合：

```text
General
TrianglesHitGroup
ProceduralHitGroup
```

Pipeline 显式声明：

```text
MaxRecursionDepth
MaxPayloadSize
MaxAttributeSize
Shader groups
Pipeline layout
```

Graphics 提供 `RayDispatchTableBuilder`，输入 group index 和 local record bytes，后端生成：

- D3D12 Shader Binding Table。
- Vulkan Shader Binding Table。
- Metal compute/render pipeline 所需的 function/intersection table 与调度数据。

`TraceRays` 只接受构建完成的 dispatch table 和维度。Metal 是否支持完整 portable ray pipeline 由 `RayPipeline` 能力决定；只支持 Ray Query 的设备仍可使用公共 AS API。

### 13.3 高级 RT 能力

Motion、indirect trace、opacity micromap、serialization、shader execution reordering 等全部独立 feature gate，不合并为一个 RT tier。

---

## 14. 其他高级能力

| 能力 | 核心策略 |
|---|---|
| Mesh Shader | 独立 Pipeline 与 draw 命令，按 task/mesh/indirect 分项查询 |
| VRS | 拆分 per-draw、per-primitive、attachment/map、combiner；Metal rate map 不强行等同所有 D3D/Vulkan 模式 |
| Multiview | `ViewMask` + capability；映射 Vulkan multiview、D3D view instancing、Metal layered/vertex amplification |
| Conservative Raster | Rasterizer optional feature |
| Programmable Sample Positions | Pipeline/RenderingInfo 关联，按格式与 sample count 查询 |
| Depth Bounds | 动态状态 optional feature |
| Raster Order / Fragment Interlock | 独立能力，不隐藏模拟 |
| Transform Feedback | D3D12/Vulkan 可选，Metal 无统一实现，放扩展 |
| General Device Generated Commands | 独立扩展；固定 Draw/Dispatch indirect 留在核心 |
| Work Graphs | Backend extension，不把 D3D12 Work Graph 强行映射为 Vulkan DGC 或 Metal ICB |
| Sampler/Residency Feedback | 独立扩展，D3D sampler feedback、Vulkan/Metal 稀疏反馈语义分别暴露 |
| Protected Resources | 独立设备/队列/资源能力，不能与普通资源随意混用 |
| Multi-GPU | DeviceGroup 扩展；D3D linked adapter、Vulkan device group、Metal multi-device 不假定等价 |
| Native Tile/Imageblock | Metal native extension，不进入 portable core |

---

## 15. Query、Profiling 与诊断时间线

### 15.1 Query 类型

```text
Timestamp
Occlusion / BinaryOcclusion
PipelineStatistics
StreamOutputStatistics
AccelerationStructureCompactedSize
AccelerationStructureSerializationSize
PerformanceCounter extension
```

提供 Query Pool、Begin/End、WriteTimestamp、ResolveQueries。Timestamp frequency/period 与有效位数按 Queue 查询。

### 15.2 CPU/GPU 时间校准

提供 `GetCalibratedTimestamps` 能力，返回 CPU monotonic clock、GPU timestamp 和最大误差。不同 Queue 时间域不能默认相同。

### 15.3 Marker 与 Breadcrumb

渲染热路径使用 `DebugLabelId` 和 `BreadcrumbId`，不在每条命令上传递字符串。字符串在初始化或调试线程中 intern；Capture/PIX/RenderDoc/Metal Capture 开启时后端将 ID 映射为文本。

---

## 16. Surface、Swapchain 与 Presentation

### 16.1 Surface

`NativeSurfaceDesc` 是平台句柄判别联合：

```text
Win32 HWND
Xlib/Xcb
Wayland
Android NativeWindow
CAMetalLayer / NSView / UIView
NativeOnly
```

Graphics 不拥有窗口生命周期。

### 16.2 SwapchainDesc

```text
Width, Height
Format
ColorSpace
BufferCount
PresentMode
Usage
CompositeAlpha
Transform
AllowTearing
MaxFrameLatency
AcquireMode
```

ColorSpace 与 Format 分离，支持 sRGB、Display P3、scRGB、HDR10/PQ 等查询结果。HDR metadata 单独设置。

### 16.3 Acquire/Present

`AcquireNextImage` 返回：

```text
Borrowed TextureHandle
Image index
Opaque acquire token
Status
```

Swapchain Texture 由 Graphics 所有，调用方不能 Destroy。Recreate 后旧 handle generation 失效。Metal drawable 的短生命周期被统一建模为 borrowed image。

`Present` 接受完成渲染的 `GpuSignal`，后端完成必要的 binary/WSI bridging。状态包括 `Success/Suboptimal/OutOfDate/Occluded/SurfaceLost/DeviceLost`。

### 16.4 帧节奏

可选能力：present id、present wait、目标时间/最小持续时间、waitable frame latency。Graphics 不固定 frames-in-flight 数量。

---

## 17. 后端映射

| Graphics 概念 | D3D12 | Vulkan | Metal |
|---|---|---|---|
| Instance | DXGI Factory + D3D12 runtime | VkInstance | Metal device enumeration |
| Adapter | IDXGIAdapter | VkPhysicalDevice | MTLDevice |
| Device | ID3D12Device | VkDevice | MTLDevice context |
| Queue | Command Queue | VkQueue | MTLCommandQueue / Metal 4 queue |
| Command Pool | Command Allocator pool | VkCommandPool | Graphics pool，创建 MTLCommandBuffer/encoder |
| Command Buffer | CommandList | VkCommandBuffer | MTLCommandBuffer + encoders |
| Timeline | ID3D12Fence | Timeline Semaphore | MTLSharedEvent/Event value |
| Barrier | Enhanced Barrier，resource-state fallback | PipelineBarrier2 | barrier/fence/event/encoder boundary |
| Dynamic Rendering | Render Pass 或 OM path | Dynamic Rendering | Render Pass Descriptor |
| Pipeline Layout | Root Signature | VkPipelineLayout | Argument buffer/table ABI |
| Persistent Bind Group | Persistent descriptor table range | Persistent Descriptor Set/Heap/Buffer range | Persistent Argument Buffer/Table |
| Transient Bind Group | Ring/page descriptor table range | Transient Descriptor Set page or Heap/Buffer range | Argument Buffer offset / Argument Table allocation |
| Heap | ID3D12Heap | VkDeviceMemory | MTLHeap/placement heap |
| Sparse | Reserved/Tiled Resource | Sparse Binding | Sparse/placement sparse，按能力 |
| Swapchain | DXGI SwapChain | KHR Swapchain | CAMetalLayer drawable ring |
| Pipeline Cache | Pipeline Library | Pipeline Cache/Binary | Binary Archive/Dataset |
| Crash Diagnostics | DRED/InfoQueue | Validation/Device Fault/Checkpoints | CommandBuffer error/Capture |

### 17.1 D3D12 策略

- 优先 Enhanced Barriers；不支持时保守映射 resource-state fallback、UAV 和 aliasing barrier。
- 优先 Render Pass API；不支持时使用 OMSetRenderTargets + clear/discard/resolve。
- Pipeline Layout 生成 Root Signature，不从 DXIL 反射或反序列化 Root Signature。
- 全局 shader-visible resource/sampler descriptor heap 在 Device 创建时定额，并划分 persistent、bindless 与 timeline-retired transient pages；录制中途不以 heap switch 处理溢出。
- Pipeline Library key 由 pipeline hash 生成内部字符串。
- GPU 失效通过 HRESULT、GetDeviceRemovedReason 和 DRED 归一化。

### 17.2 Vulkan 策略

- 生产最低建议 Vulkan 1.3 语义：Synchronization2、Dynamic Rendering、Timeline Semaphore。
- Vulkan 1.4 为优选 fast path，不把 1.4 作为所有设备的硬要求。
- bindless profile 要求对应 descriptor indexing 细项；`VK_EXT_descriptor_heap` 为可用时的优选显式 heap 路径，descriptor buffer 为兼容 fast path，普通 descriptor set/page 为基线路径。
- Queue family ownership 显式映射。
- Pipeline Cache 与可用时的 Pipeline Binary 并存。
- WSI binary semaphore 生命周期完全封装在 Swapchain backend。
- transient descriptor pool/page、heap range 或 descriptor-buffer range 只在相关 Queue timeline 完成后 reset/reuse。

### 17.3 Metal 策略

- Metal 3 为兼容路径，Metal 4 为优选路径。
- Metal 4 可使用 argument tables、command allocators、command barriers、decoupled queues/buffers、pipeline dataset serialization 等；公共 API 不依赖这些对象存在。
- Metal 3 使用显式 argument descriptor 创建 encoder，不使用 Shader reflection；长期 argument-buffer page 中按 offset 分配 transient snapshot。
- Worker thread 必须管理 Objective-C autorelease pool。
- 对 argument buffer/table 间接引用的资源执行 `useResource/useResources/useHeap` 或 residency set 声明；transient writer 负责收集并去重所需元数据。
- tracked 与 untracked hazard mode 由资源策略决定，但公共 barrier 是唯一规范来源。
- 无法在 encoder 内表达的依赖通过 encoder split 实现。

---

## 18. .NET 10 / C# 14 实现策略

### 18.1 热路径规则

以下路径必须做到 steady-state **0 B GC allocation**：

- Command Buffer 录制。
- Persistent/Transient Bind Group descriptor 写入预分配 arena/page。
- Barrier 提交。
- Queue Submit。
- Map/Unmap。
- Query/Timestamp。

禁止热路径使用 LINQ、`IEnumerable<T>`、闭包、`params`、boxing、临时字符串、每调用 delegate。

### 18.2 语言特性

- `readonly struct`：句柄、状态、轻量 descriptor。
- `ref struct`：Command/Render/Compute/Copy encoder、Persistent/Transient BindGroupWriter、TransientBindGroup token、临时 builder。
- `scoped` + `ReadOnlySpan<T>`：变长输入立即消费，不保存引用。
- `[InlineArray]`：8 个 color attachment、固定小型状态数组。
- `nint/nuint`、unsafe pointer、function pointer：原生调用和后端 dispatch。
- static abstract interface members：冷路径生成 backend table，不在热路径做接口虚调用。
- C# 14 extension members：只用于可选 ergonomics 包，不作为 ABI 基础。
- `ValueTask`：Pipeline 异步创建、timeline 异步等待等冷路径。

### 18.3 原生互操作

- 稳定导出函数使用 `[LibraryImport]` 源生成 P/Invoke。
- Vulkan 从 `vkGetInstanceProcAddr/vkGetDeviceProcAddr` 得到函数指针。
- D3D12 直接调用 COM vtable；使用无托管分配的 `ComPtr` 风格结构。
- Metal 使用生成的 Objective-C runtime 调用或极薄 C/ObjC ABI shim。
- Callback 使用静态 `[UnmanagedCallersOnly]` 方法和长生命周期 context，不创建 per-call delegate。
- Shipping backend assembly 可启用 runtime marshalling 禁用模式，但必须保证所有签名 blittable 并经过 ABI 测试。

### 18.4 Backend Dispatch

公共 Device 保存只读函数表指针。Cold path 可使用普通接口组织代码，Command hot path 使用固定函数表：

```text
CommandDispatch*
BackendCommandContext
Front-end state cache
ResourceUseSet
```

每条命令最多一次 Graphics 间接调用和一次原生 API 调用。前端 state cache 消除重复 Pipeline、BindGroup、Viewport 等设置。对 Transient Bind Group，cache key 必须包含 command-buffer generation/epoch；`SetBindGroup` 命中 cache 时不得产生 descriptor 分配。

### 18.5 NativeAOT

设计必须：

- 不依赖运行时反射发现 backend。
- 不依赖 `Reflection.Emit` 或动态代码生成。
- 所有 backend/extension 静态注册或源生成注册。
- 字符串 marshalling 不出现在热路径。
- NativeAOT 与 JIT 使用同一公共 ABI。

### 18.6 托管与非托管内存

- 后端 slot、command scratch、descriptor metadata 优先放入分段托管数组或 `NativeMemory` block。
- 长期映射使用原生指针，不 pin 大型托管数组。
- 大型冷路径临时数组可使用 `ArrayPool<T>`。
- 每线程 command arena、persistent descriptor chunk、transient descriptor page slice 和 barrier scratch 避免全局锁。
- transient storage 使用长期 block/page；每个录制 epoch 只推进 allocator 游标并追加 timeline retire record，不创建托管对象或原生 heap。

---

## 19. Thread Safety

| 对象/操作 | 规则 |
|---|---|
| Instance/Adapter query | thread-safe |
| Device resource creation/destruction | thread-safe，内部 sharded pool |
| Pipeline creation | thread-safe，可异步 |
| Persistent BindGroup creation | thread-safe |
| Bindless Descriptor Arena allocation | thread-safe 或 thread-local chunk |
| Transient BindGroup allocation | 随所属 CommandBuffer/CommandPool thread-affine；全局 page 获取使用 sharded/lock-free free list |
| CommandPool | 单线程录制，完成后可转移所有权 |
| CommandBuffer recording | 单线程 |
| Queue Submit | 每 Queue 序列化，跨 Queue 并行 |
| Map 同一资源 | 调用方同步 |
| Swapchain Acquire/Present | 按 SurfaceCaps 指定线程限制 |
| Debug callback | 任意线程，不允许重入关键 Graphics 调用 |

不得存在全局 device mutex 覆盖 command recording。

---

## 20. Validation、Capture 与 Device Lost

### 20.1 Validation Layer

独立层检查：

- handle generation 和跨 device 使用。
- 对象生命周期与 zombie 使用。
- Command Buffer 状态机和线程所有权。
- rendering scope 合法性。
- Pipeline/attachment format/sample count 兼容。
- Persistent/Transient BindGroup layout、descriptor kind、array range、dynamic offset alignment。
- transient token 的 Command Buffer identity、epoch、generation、seal 状态、跨 Queue/跨 pool 使用和 use-after-reset。
- transient range 是否在 GPU 完成前被覆盖、page 是否过早 reset、reusable bundle 是否捕获了 command-local token。
- resource barrier、subresource state、queue ownership。
- alias heap range 生命周期。
- bindless lease 是否已退休或被重用。
- sparse mapping 与 residency。
- Copy region、compressed block 对齐和 integer overflow。

### 20.2 Native Validation

- D3D12 debug layer、GPU validation、InfoQueue。
- Vulkan validation layers、debug utils。
- Metal API validation、Shader validation、capture。

这些由 `ValidationMode` 控制，不改变公共语义。

### 20.3 Capture/Replay

Capture 层记录：

- 资源与 Pipeline 创建描述。
- Shader bytecode hash/blob。
- Persistent/Transient Descriptor 的逻辑内容与版本；不把原生 heap address/offset 作为 capture ABI。
- Command opcode 和 POD payload。
- Queue submit、timeline、present。
- 外部资源替代/注入信息。

正常执行不经过中间命令流；Capture 开启时镜像记录。Transient Bind Group 在 Commit 时获得 capture-local logical id，replay 时重新分配 backend range，因此 ring offset、descriptor-set handle 和 argument-buffer address 不要求保持一致。跨后端 replay 只有在 capture 包含对应 backend bytecode 且未使用不可序列化 native section 时才保证。

### 20.4 Device Lost

Device 状态机：

```text
Active -> Lost -> DrainingDiagnostics -> Disposed
```

Device Lost 为 sticky 状态；后续 GPU API 立即返回 `DeviceLost`。Graphics 收集：

- backend reason/code。
- 最后提交 timeline。
- breadcrumb ring。
- 最近 Pipeline/Pass/Object IDs。
- page-fault/resource 信息（可用时）。

Graphics 不自动重建设备和资源；引擎通过资源 recipe 和资产系统恢复。

---

## 21. Native Escape Hatch

AAA 引擎需要原生扩展，但必须限制污染范围：

- `GetNativeHandle` 返回 borrowed handle，明确 backend/type/lifetime。
- 不提供任意托管 callback 插入 command stream。
- 使用 `BeginNativeSection/EndNativeSection`：调用方声明将访问的资源、进入状态、退出状态和被破坏的绑定缓存。
- 结束 native section 后 Graphics 使 Pipeline/BindGroup/vertex state cache 失效，并按声明恢复状态跟踪。
- Capture 若没有对应 serializer，则标记该段不可回放。

---

## 22. API Contract Skeleton

```csharp
public sealed class Device : IDisposable
{
    public DeviceCaps Caps { get; }

    public Status CreateBuffer(in BufferDesc desc, out BufferHandle buffer);
    public Status CreateTexture(in TextureDesc desc,
        scoped ReadOnlySpan<Format> viewFormats,
        out TextureHandle texture);

    public Status CreateBufferView(in BufferViewDesc desc, out BufferViewHandle view);
    public Status CreateTextureView(in TextureViewDesc desc, out TextureViewHandle view);
    public Status CreateSampler(in SamplerDesc desc, out SamplerHandle sampler);

    public Status CreateHeap(in HeapDesc desc, out HeapHandle heap);
    public Status CreatePlacedBuffer(HeapHandle heap, ulong offset,
        in BufferDesc desc, out BufferHandle buffer);
    public Status CreatePlacedTexture(HeapHandle heap, ulong offset,
        in TextureDesc desc, scoped ReadOnlySpan<Format> viewFormats,
        out TextureHandle texture);

    public Status CreateBindGroupLayout(
        scoped ReadOnlySpan<BindingDesc> bindings,
        out BindGroupLayoutHandle layout);

    public Status CreatePipelineLayout(
        scoped ReadOnlySpan<BindGroupLayoutHandle> groups,
        scoped ReadOnlySpan<PushConstantRange> pushConstants,
        out PipelineLayoutHandle layout);

    public PersistentBindGroupWriter BeginPersistentBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public PersistentBindGroupWriter BeginPersistentBindGroupUpdate(
        BindGroupHandle source,
        uint variableDescriptorCount = 0);

    public Status CreateShaderModule(ShaderStage stage,
        scoped ReadOnlySpan<byte> bytecode,
        in Hash128 hash,
        out ShaderModuleHandle module);

    public Status CreateRasterPipeline(in RasterPipelineDesc desc,
        out PipelineHandle pipeline);
    public Status CreateComputePipeline(in ComputePipelineDesc desc,
        out PipelineHandle pipeline);

    public Status CreateCommandPool(QueueHandle queue,
        in CommandPoolDesc desc,
        out CommandPoolHandle pool);

    public Status ResetCommandPool(CommandPoolHandle pool,
        in CommandPoolResetInfo info);

    public Status AllocateCommandBuffer(CommandPoolHandle pool,
        CommandBufferLevel level,
        out CommandBufferHandle commandBuffer);

    public CommandEncoder BeginCommandBuffer(CommandBufferHandle commandBuffer,
        in CommandBufferBeginInfo info);

    public Status Submit(QueueHandle queue,
        scoped ReadOnlySpan<CommandBufferHandle> commandBuffers,
        scoped ReadOnlySpan<TimelineWait> waits,
        out GpuSignal completion);

    public ulong GetCompletedValue(QueueHandle queue);
    public Status Wait(in GpuSignal signal, TimeSpan timeout);

    public void Destroy(BufferHandle handle);
    public void Destroy(TextureHandle handle);
    public void Destroy(TextureViewHandle handle);
    public void Destroy(BufferViewHandle handle);
    public void Destroy(SamplerHandle handle);
    public void Destroy(BindGroupHandle handle);
    public void Destroy(PipelineLayoutHandle handle);
    public void Destroy(PipelineHandle handle);
    public void Destroy(PipelineHandle handle);

    public void CollectGarbage();
}
```

该 skeleton 表示边界与对象关系，不表示最终命名必须逐字照搬。

绑定相关的 Command API：

```csharp
public readonly ref struct TransientBindGroup
{
    internal readonly nuint Token;
    internal readonly uint Epoch;
    internal readonly uint CommandBufferGeneration;
}

public ref struct CommandEncoder
{
    public Status ReserveTransientBindGroups(
        scoped ReadOnlySpan<TransientBindGroupReservation> reservations);

    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public RenderEncoder BeginRendering(scoped in RenderingInfo info);
    public ComputeEncoder BeginCompute(scoped in ComputePassInfo info);
    public Status End();
}

public ref struct RenderEncoder
{
    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public void SetBindGroup(uint groupIndex, BindGroupHandle group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);

    public void SetBindGroup(uint groupIndex, in TransientBindGroup group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);
}

public ref struct ComputeEncoder
{
    public TransientBindGroupWriter BeginTransientBindGroup(
        BindGroupLayoutHandle layout,
        uint variableDescriptorCount = 0);

    public void SetBindGroup(uint groupIndex, BindGroupHandle group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);

    public void SetBindGroup(uint groupIndex, in TransientBindGroup group,
        scoped ReadOnlySpan<uint> dynamicOffsets = default);
}
```

`ReserveTransientBindGroups` 必须在第一次 transient Commit 前调用。返回成功后，声明范围内的 Commit 获得容量保证；超额 Commit 仍可尝试按需分配并返回 `OutOfTransientDescriptors`。


---

## 23. 性能与质量门槛

### 23.1 强制指标

- Release steady-state Graphics hot path：0 B/frame GC allocation。
- Command recording 不使用全局锁。
- 资源句柄 lookup 为 O(1)。
- Submit 不做隐式 queue idle、pipeline creation 或 descriptor heap 扩容。
- `SetBindGroup` 不分配 descriptor；只有 Persistent/Transient writer Commit 可以写入新快照。
- transient reservation 成功后，预算内 Commit 不得因 descriptor capacity 失败。
- Pipeline creation 不在 render thread 阻塞。
- Graphics CPU 开销在 command-heavy microbenchmark 中，相对直接原生 backend 的目标上限为可配置阈值；建议首版门槛为 p95 不超过 10% 额外 CPU 时间。
- Validation 关闭时不保留完整字符串、堆栈和高成本状态历史。

### 23.2 内存门槛

- 每资源常驻元数据按对象类别预算并可统计。
- Descriptor heap/arena/page 容量在创建时报告，并区分 persistent、bindless、transient resource 与 sampler budget。
- transient allocator 公开 high-water、ring wrap、page churn、reservation miss 和 overflow 计数。
- Pipeline bytecode、native module、cache blob 去重。
- deferred destruction 队列有高水位与泄漏诊断。
- 任何增长型 arena 都有最大容量和明确失败码。

---

## 24. 测试体系

### 24.1 单元测试

- 句柄 generation、并发分配、ABA 防护。
- descriptor quarantine、transient range/page ring wrap 和 timeline 回收。
- transient token 的 epoch/generation、discard、early reset、跨 CommandBuffer/Queue 非法使用。
- reservation 容量保证、超额 Commit 的确定性失败以及重复 `SetBindGroup` 不增加分配计数。
- subresource range、format/aspect、copy footprint。
- barrier 合并与状态验证。
- heap allocator、alias epoch、OOM。
- pipeline descriptor 规范化和 hash 稳定性。
- C#/native ABI size、alignment、calling convention。

### 24.2 后端一致性测试

每个后端必须跑同一 conformance suite：

- Buffer/Texture/View/Sampler。
- upload/readback/copy/resolve。
- rendering load/store/clear/resolve。
- persistent/transient descriptor arrays、dynamic offsets、bindless。
- D3D12 resource/sampler 双 range、Vulkan set/page 与 heap/buffer 路径、Metal argument-buffer offset/table 路径。
- transient descriptor 在多 submit、async compute、跨帧 GPU 延迟和 CommandBuffer discard 下均不提前复用。
- multi-queue timeline 和 ownership transfer。
- pipeline cache 命中/失效。
- query/timestamp。
- sparse、RT、mesh、VRS 等按能力条件运行。
- swapchain resize、occlusion、out-of-date、HDR。
- device lost 和 OOM 注入。

### 24.3 图像与压力测试

- 跨后端 golden image，使用格式相关容差。
- 1M persistent/bindless descriptor churn 与高频 transient snapshot ring wrap。
- 数十线程资源创建和 command recording。
- 大量短命 transient resource 与 transient bind group，包含 sampler-heavy 和 variable-count 大 group。
- Pipeline storm 与 cache miss。
- 多窗口和持续 resize。
- 长时间运行 generation wrap/retire queue 压力。
- Capture/Replay 确定性。

### 24.4 硬件矩阵

至少覆盖：

- Windows：NVIDIA、AMD、Intel，D3D12 与 Vulkan。
- Linux：NVIDIA、AMD、Intel，Vulkan。
- macOS：多个 Apple GPU family，Metal 3/4 路径。
- iOS/iPadOS：至少一个较低目标 family 和一个当前 family。

---

## 25. 实施阶段与退出条件

### Phase A：ABI 与基础设施

完成公共类型、句柄池、结果码、能力模型、原生绑定生成、backend registry。退出条件：NativeAOT/JIT 均可初始化三个后端并枚举设备。

### Phase B：核心渲染

完成 Buffer/Texture/View/Sampler、Command Buffer、Graphics/Compute、barrier、timeline、swapchain。退出条件：三个后端通过基础 conformance 和多队列测试。

### Phase C：内存与绑定

完成 heap/placement/aliasing、默认 allocator、Persistent/Transient Bind Group、全局 descriptor heap/arena、layout-aware reservation、timeline page recycle 和 Metal residency。退出条件：Render Graph 可在编译期预算 transient resource 与 descriptor，录制路径无隐藏同步；D3D12/Vulkan/Metal 的 transient ring/pool/argument-buffer 压力测试和 bindless 压力测试全部通过。

### Phase D：Pipeline 与生产化

完成 pipeline cache、异步 creation、validation、capture、device lost、crash diagnostics。退出条件：冷启动和 warm-cache 数据达标，崩溃包可定位最后 pass/pipeline/resource。

### Phase E：高级特性

按独立 capability 交付 Mesh、RT、VRS、Sparse、DGC、External Interop、Multi-GPU。每项必须有三后端支持声明、明确 unsupported 路径和独立 conformance。

---

## 26. 最终不可破坏的 Invariants

1. Graphics 核心永远不通过名称绑定资源。
2. Graphics 核心永远不调用 Shader 编译器或 Shader 反射 API。
3. Pipeline Layout 永远显式提供，且独立于 Shader Module。
4. 任何隐藏 GPU 工作、隐藏 Queue Wait、隐藏 Shader fallback 都必须禁止或放入显式 Utility API。
5. Persistent Bind Group、Transient Bind Group 与 Pipeline 默认不可变；新内容产生新对象或新 snapshot。
6. 每个新的 Transient Bind Group snapshot 使用独立 range；`SetBindGroup` 不分配，range 只在所属提交完成后复用。
7. Bindless descriptor slot 在在途期间不可复用。
8. Destroy 不等于立即释放；GPU timeline 决定对象、bindless slot 和 transient descriptor storage 的实际回收。
9. Primary Command Buffer 是 one-shot；可重复命令使用显式 Bundle/Secondary 模型，reusable secondary 默认不得捕获 command-local transient token。
10. Barrier 语义以 stage/access/layout/queue ownership 为准，不以某个后端的枚举为准。
11. 高级功能必须细粒度查询；不能用“支持 DX12 Ultimate”之类标签替代实际能力。
12. 热路径必须无 GC 分配、无字符串、无 LINQ、无闭包、无全局锁。
13. 三个后端共用同一 conformance suite；任何 backend-specific fast path 都不能改变公共结果。

---

## 27. 参考依据

本设计以以下官方资料的当前能力模型为依据：

- Microsoft Learn：.NET 10、C# 14、NativeAOT、LibraryImport 与原生互操作指南。
- Microsoft Direct3D 12 文档：Enhanced Barriers、Render Pass、Descriptor Heap、Root Signature、Fence、DRED、Pipeline Library；其中 transient descriptor 设计直接参考 [Descriptor Heaps Overview](https://learn.microsoft.com/en-us/windows/win32/direct3d12/descriptor-heaps-overview) 与 [Shader Visible Descriptor Heaps](https://learn.microsoft.com/en-us/windows/win32/direct3d12/shader-visible-descriptor-heaps)。
- Khronos Vulkan 1.4 Specification 与 Vulkan Documentation：Synchronization2、Dynamic Rendering、Timeline Semaphore、Descriptor Indexing、Descriptor Set/Pool、[Descriptor Heap](https://docs.vulkan.org/spec/latest/chapters/descriptorheaps.html)、[Descriptor Buffer](https://docs.vulkan.org/refpages/latest/refpages/source/VK_EXT_descriptor_buffer.html)、Pipeline Cache/Binary。
- Apple Metal Feature Set Tables（2026-05-21）与 Metal 文档：Metal 3/4、Argument Buffer/Table、Heap、Event、Fence、Sparse、Ray Tracing、Mesh、Pipeline Archive/Dataset；transient offset 与 residency 直接参考 [`MTLArgumentEncoder.setArgumentBuffer(_:offset:)`](https://developer.apple.com/documentation/metal/mtlargumentencoder/setargumentbuffer%28_%3Aoffset%3A%29)、[`useResource/useResources/useHeap`](https://developer.apple.com/documentation/metal/argument-buffer-resource-preparation-commands) 和 [Metal 4 core API](https://developer.apple.com/documentation/metal/understanding-the-metal-4-core-api)。
- Slang 官方文档：DXIL、SPIR-V、Metal/MetalLib target，SPIR-V 入口命名和 ParameterBlock 映射。

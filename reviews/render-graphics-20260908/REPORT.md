# Render / Graphics 全量静态审阅与公共 API 复核

> 联合审查更新：请以 [COMBINED_REVIEW.md](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/COMBINED_REVIEW.md) 为合并阅读入口。该文逐项整合新提供的架构审查与最初两份具体审查，校正旧版本结论，并补充 S01–S16。本文保留原 A/B/M/G 编号和复现证据，避免既有引用失效；下文 54 项是此版主清单数量，不包含后续补充项。

审阅日期：2026-09-08。目标工作树：`F:/SomeEngine.Next-render-enhancement`，分支 `codex/render-enhancement`，审阅时 HEAD `a787000034e7eba7559d20299a2c704216444011`。审阅对象包含该工作树现有未提交和未跟踪源码；不是仅审阅 diff，也没有把主工作树旧报告当成本工作树事实。

## 结论

当前实现存在足以阻止可靠交付的问题。优先处理 GPU 数据写保护、上传结果确认、资源依赖保留和失败后的状态恢复。报告列出 **54 项主要问题（12 项架构、42 项实现）、12 项维护性问题、5 项门禁/证据问题**。编号按主题编排，不表示每条都属于独立根因：例如 A07 与 M04 都涉及重复结构状态。不要把这些数字相加当成互不相关缺陷总数。

其中 **20 项行为已通过 Release 生产程序集的公共 API 复现**；其余明确标为静态确认或数学反例，没有冒称已经运行 GPU 图像对比。P1 表示可能导致错误结果、失去同步保护或正常功能无法继续；P2 表示受特定能力、输入或长期使用触发的正确性/维护问题。

现有 Game world → Rendering world 分离应保留。此次未获得足以确认额外场景数据通路的证据，不把共享资产或独立完成条件本身当成第三数据通路。修正 shader/材质问题也不需要在管线中默认顶点属性布局，或引入运行时顶点元数据多态。

## 覆盖范围与证据界限

清单共 948 个文件：**694 个非测试文本文件全文读取；57 个非测试二进制文件仅建清单；197 个测试相关文件按用户要求排除**。覆盖 RenderGraph、Render、RHI、Validation、D3D12、Vulkan、Cluster、Cluster Pipeline、Features、Runtime、相关资产/导入器/生成器、shader、benchmark 和工具。最终重新核对清单全部 SHA-256，无变化、无丢失。清单不是“整个仓库所有业务目录都已全文审阅”的声明。

- [逐文件覆盖清单](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/coverage.csv)
- [公共 API 复现 1](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/public-api-reproductions.txt) / [完整源代码](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/reproduction-source.md)：1–7。
- [公共 API 复现 2](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/public-api-reproductions-2.txt) / [完整源代码](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/reproduction-source-2.md)：8–14。
- [公共 API 复现 3](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/public-api-reproductions-3.txt) / [完整源代码](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/reproduction-source-3.md)：15–19。
- [公共 API 复现 4](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/public-api-reproductions-4.txt) / [完整源代码](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/reproduction-source-4.md)：20。

实际执行过 `dotnet build SomeEngine.slnx -c Release --no-restore --nologo -v minimal`，结果 0 warnings、0 errors。在收到测试排除要求之前，还执行过 RenderGraph.Tests 62 项和 Graphics.Benchmarks.Tests 70 项，全部通过；这仅记录已有行为证据，不属于测试代码审阅，也不能抵消本报告复现的问题。随后没有继续审阅测试或建议恢复主工作树已删除测试。

探针只调用公共 API，没有反射或内部接口。临时探针项目、运行产物均已删除；保留的是交付所需的源代码文本与结果记录。未改产品代码。

## 架构问题

### A01 [P1] 实例并行写权限与底层映射生命周期不一致

位置：[RenderInstanceResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceResources.cs)。证据：已复现 18。

公共 API 允许多个互不重叠的切片并行写入，批次构建也使用这一能力；每次写入却对同一 Buffer 重新 Map，而 D3D12/Vulkan 每个 Buffer 只有一个活动映射。16 个并行切片出现 7 次 active mapping 异常。

修正方向：在一次 prepare 写入期间统一持有映射，把互不重叠的数据跨度交给写入者；不要给每个元素写入加锁掩盖模型冲突。

### A02 [P1] 等待失败提前解除 GPU 读者保护

位置：[RenderFrameCoordinator.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Frame/RenderFrameCoordinator.cs)。证据：已复现 14。

WaitForTrackedSubmissions 在等待完成前清空读者 fence。人为阻塞 GPU 并让等待超时后，submission 仍未完成，但 TryBeginPrepare 已允许独占写入。

修正方向：只有已确认完成的读者才能移除；超时应保留仍在使用数据的提交。

### A03 [P1] 用三种队列类型代替真实队列身份

位置：[RenderTimeline.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Frame/RenderTimeline.cs)。证据：已复现 13。

协调器与 timeline 以固定 3 个完成项建模，但 RHI 允许多个同类型队列。观察四个被阻塞的图形队列后，prepare 抛出 IndexOutOfRangeException。

修正方向：按真实 Queue 身份记录完成条件，统一协调器与 timeline 的表示。

### A04 [P1] 上传完成状态由录制或失败执行提前确认

位置：[RenderDevice.Textures.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/RenderDevice.Textures.cs)。证据：已复现 5、6。

MaterialBindless.Upload 在帧成功前清除 dirty 并置 initialized，取消帧后重试没有上传 pass；纹理路径虽然处理显式取消，却把录制失败收到的 executed=true 当成上传完成，下一帧只剩消费和 finalize。

修正方向：图执行结果必须区分未提交、部分提交和已提交的具体工作；上传状态只能由对应提交事实推进。

### A05 [P1] D3D12 资源依赖在多条捕获路径中重复且不完整

位置：[CommandLifecycle.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandLifecycle.cs)。证据：静态确认。

直接资源、瞬态 view、持久 descriptor 各自枚举依赖。瞬态 BufferUav 捕获主 buffer 和 descriptor，遗漏 counter；view 路径也遗漏直接 sparse resource 路径保留的映射代。释放 view/旧映射后，录制命令缺少仍需要的依赖。

修正方向：由同一份资源依赖描述生成命令保留集合，覆盖 counter 与映射代；不要继续逐入口补名单。

### A06 [P1] 稀疏映射替换过早释放旧 heap 引用

位置：[VulkanSparse.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanSparse.cs)。证据：静态确认。

QueueBindSparse 返回成功后便释放旧映射字典保留的 heap；该返回只表示工作已入队。队列工作持有新映射，普通 resource 捕获也没有保留旧映射代，旧 GPU 使用尚未完成时旧内存即可释放。

修正方向：把被替换映射和旧 heap 引用保留到相关 GPU 工作完成。

### A07 [P2] 维护了没有执行消费者的另一套生命周期与放置模型

位置：[GraphStructureIndex.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/GraphStructureIndex.cs)。证据：静态确认。

ResourceLifetimes、NextPlacementToken、NonTransientResourceCount 仅在该索引内部维护；TransientPlacements 的外部使用只取 Keys 做资源压缩。实际帧另行计算生命周期和分配。所谓 movedAllocations 统计 token 替换，不代表 GPU 分配移动。

修正方向：删除无消费者的生命周期/放置状态；保留必要的编辑校验，并共享真正需要一致的算法。

### A08 [P2] 校验层自己的状态缺少完整释放入口

位置：[ValidationLayer.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Validation/ValidationLayer.cs)。证据：静态确认。

录制状态在 End 加入、Submit 移除，已录制但直接 Dispose 的命令没有相应清理；query 状态也没有 pool 释放清理。诊断模型因此与实际对象生命周期脱节。

修正方向：让校验状态跟随对象与命令代的真实结束事件清理；不要以扫描全部历史对象替代生命周期。

### A09 [P2] descriptor pool 回收结果没有返回分配搜索

位置：[VulkanDescriptors.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanDescriptors.cs)。证据：静态确认。

Free 将 set 归还原 pool，而 Allocate 只尝试当前 active pool。重复超过单 pool 容量的相同峰值工作负载，即使每轮全释放，也会持续建立新 pool。

修正方向：分配器应再次使用仍有空闲容量的旧 pool，让保留量收敛到并发峰值。

### A10 [P2] 纹理复制 footprint 有重复权威，上传端为错误估计补填充

位置：[CommandScopes.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/CommandScopes.cs)。证据：静态确认。

Graph 以 RowPitch×Height×Depth 估算复制字节，忽略压缩块行；纹理上传 Repack 又按这个估计增加填充以通过 Graph 校验。压缩纹理可因像素行与块行混淆产生约四倍的行存储需求。

修正方向：Graph 统一消费 RHI 的复制 footprint，并移除上传端为错误估计添加的填充。

### A11 [P1] 无法证明 GPU 静止时仍销毁提交资源

位置：[VulkanDevice.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanDevice.cs)。证据：静态确认。

DeviceWaitIdle 返回非 device-lost 失败后记录 teardown failure，但仍继续 drain children 并销毁 command pools/捕获对象，之后才处理失败。普通等待失败不等于设备已丢失，也不证明 GPU 停止。

修正方向：区分完成、设备实际丢失与完成未知；未知状态不能释放仍可能被 GPU 使用的数据。

### A12 [P2] D3D12 父设备销毁顺序晚于最后一次回收等待产生新工作

位置：[Device.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/Device.cs)。证据：静态确认。

NativeDevice.Release 先 quiesce queues，再释放 children；Swapchain.Release 此时又 signal 并加入 PresentationRetirement。随后直接释放 native queues，没有再次处理新产生的退休项，保留的 backbuffer/swapchain 引用没有排空。

修正方向：让 child teardown 产生的回收工作进入最后一次 drain，再释放队列。

## 架构可以保留、需要修正的实现

### B01 [P1] Execute 参数错误后丢失帧清理入口

位置：[RenderGraphFrame.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrame.cs)。证据：已复现 1。

向 Execute 传入过小输出数组会在实际执行清理区之前抛出；外层仍清除自己的 state。Dispose 无法结束 Graph 的活动帧，下一次 BeginFrame 和 Graph.Dispose 都被阻止。

修正方向：参数拒绝必须保留可清理帧，或在同一个 finally 中结束帧。

### B02 [P1] 已提交编辑的副本仍能修改发布结构

位置：[RenderGraphEdit.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphEdit.cs)。证据：已复现 2。

复制 edit，提交原值后，通过旧副本 SetPassLabel 能修改已发布诊断，StructureVersion 却不变。

修正方向：每次编辑操作验证实际编辑代仍有效，关闭后不能再访问已发布的 staging。

### B03 [P1] 旧 pass 定义能写进复用后的新帧

位置：[PassDefinition.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/PassDefinition.cs)。证据：已复现 3。

保留已结束帧的 PassDefinition，下一帧复用容器后旧定义能向新帧增加 access；探针观察到新帧多出一次访问。

修正方向：定义句柄携带并验证取得它时的帧身份。

### B04 [P2] 放弃编辑会复用已经发出的资源身份

位置：[RenderGraphEdit.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphEdit.cs)。证据：已复现 4。

在被放弃的编辑中取得的 GraphBufferId，与下次编辑新建资源的 ID 相等。外部保留的失败编辑句柄可误认新对象。

修正方向：已发出的身份代不能随编辑回滚而重新发放。

### B05 [P1] 同步解析失败污染后续帧的缓存

位置：[FrameSynchronization.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameSynchronization.cs)。证据：已复现 8。

冲突 layout 被正常拒绝后，部分 WaitPasses 已修改但反向索引尚未注册；下一次合法帧清理索引时 KeyNotFoundException。

修正方向：同步结果整体成功后再安装，或失败时完整撤销涉及的索引。

### B06 [P2] 动态 pass 声明失败留下范围读取

位置：[RenderGraphFrameState.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphFrameState.cs)。证据：已复现 9。

声明回调先 ReadRanges 再抛异常，撤销路径未清除 buffer range reads。替代的空 pass 复用身份并继承读取，执行报 RG4001。

修正方向：声明失败应撤销该声明生成的全部数据。

### B07 [P2] 添加已有 pass 的 rendering region 未标记结构变化

位置：[RenderGraphEdit.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraphEdit.cs)。证据：已复现 10。

仅对已有 pass DefineRenderingRegion 后 Commit，版本不变；取得的新 region 随后执行越界。AddRenderingRegion 没有 TouchPass。

修正方向：region 变更必须进入正常的编辑变化记录。

### B08 [P1] 分配回滚留下已释放资源引用

位置：[ResourceAllocators.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/ResourceAllocators.cs)。证据：已复现 11。

创建非法 typed+structured view 导致 Commit 失败后，移除该 view 并重试同一编辑，执行使用已 Dispose 的 NativeLease。回滚释放对象却未清除 staging 引用。

修正方向：回滚同时撤销引用和分配记录，重试必须重新创建资源。

### B09 [P1] 帧绑定的外部 buffer 完成查询漏报

位置：[RenderGraph.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/RenderGraph.cs)。证据：已复现 12。

DeclareExternalBuffer + BindExternalBuffer 的 GPU 访问尚被阻塞时，GetRangeCompletionCount 返回 0。收集只匹配 PersistentResource/RegisteredResource，漏掉 BoundResource。

修正方向：完成查询统一解析该帧真正绑定的资源。

### B10 [P1] 提交数组被拒绝后命令停留在 Submitting

位置：[VulkanCommands.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanCommands.cs)。证据：已复现 7。

Submit([A,A]) 第二次 claim 失败；调用者只在整个 ClaimCommands 返回后得到计数，因此未回滚第一个 A，后续合法提交也失败。

修正方向：逐项记录已 claim 数量，异常时只撤销已经成功取得的项目。

### B11 [P1] 写入 scope 的旧副本能释放新 scope 的锁

位置：[RenderInstanceBuffer.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceBuffer.cs)。证据：已复现 15。

复制 column write scope 并结束原值，打开新 scope 后再 Dispose 旧副本，会放开新 scope 的锁；当前 scope 仍存在时 Get 成功，当前 Dispose 再报 SynchronizationLockException。

修正方向：结束操作必须识别自己的写入代且最多执行一次。

### B12 [P1] 实例第二代改写不满足 Vulkan 映射规则

位置：[RenderInstanceResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceResources.cs)。证据：已复现 19。

CopyBatchState 对 Upload buffer 使用 MapType.Read；Vulkan 明确拒绝。实际首轮发布成功，第二轮 rewrite 在该调用失败。

修正方向：明确跨后端的上传内存读取语义，或用可移植的数据复制方式；不要改成 ReadWrite 绕过检查。

### B13 [P2] 相同排序字段得到不同布局哈希

位置：[ScalarLayout.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Materials/ScalarLayout.cs)。证据：已复现 16。

FromFields 在字段排序前计算 hash。字段输入顺序相反时，最终 Fields 相同而 Hash 不同。

修正方向：按最终字段顺序计算身份。

### B14 [P2] 正交投影的非零抖动完全无效

位置：[TemporalJitter.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Frame/TemporalJitter.cs)。证据：已复现 17。

ApplyNdc 仅修改 M31/M32 并乘 M34；正交投影 M34=0，非零 jitter 后矩阵完全不变。

修正方向：根据齐次投影关系实施偏移，使正交和透视投影均成立。

### B15 [P1] 派生材质属性覆盖继承属性的存储位置

位置：[MaterialGen.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Generators/MaterialGen.cs)。证据：已复现 20。

生成器每个类型从 slot 0 开始，并仅为本地属性生成 CreateSlots。SurfaceMaterial.Roughness=.25、CoatedMaterial.Coating=.75 后两者都读到 .75，SlotCount=1。

修正方向：继承布局必须包含基类的 slots，派生属性从继承布局末尾编号。

### B16 [P1] 变化日志截断后把不完整变化标成已覆盖

位置：[RenderInstanceSource.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceSource.cs)。证据：静态确认。

固定 128 项日志淘汰旧记录后，CollapseAfter 把剩余记录重新归到 committed revision，先前被淘汰的字段变化不再可得，却失去要求完整刷新所需的历史缺口。

修正方向：截断要保留历史不完整的边界，让读取方执行完整刷新。

### B17 [P1] 实例化实体的材质变化读取不存在的 MeshInstance

位置：[MeshRenderExtractor.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Systems/MeshRenderExtractor.cs)。证据：静态确认。

InstancedMesh + WorldTransform + 材质绑定可以触发 materialsChanged，后续却无条件读取 meshes[row]；没有 MeshInstance 的合法实体对应空 span。

修正方向：按实体实际渲染组成选择提取分支。

### B18 [P2] 物体停止移动后 previous transform 不前进

位置：[RenderExtractionSchedule.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs)。证据：静态确认。

增量提取只为变化行推进 Previous；下一帧无变化便返回，Previous 持续保留移动前值。完整 reconcile 又会推进它，因此运动历史取决于无关拓扑变化。

修正方向：将每帧历史推进与本帧属性变化分开处理。

### B19 [P1] 窗口 resize 后视图尺寸与渲染资源尺寸分离

位置：[RuntimeApplication.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Runtime/RuntimeApplication.cs)。证据：静态确认。

resize 只重建 swapchain 并更新宽高，视图使用新尺寸；cluster、lighting、temporal、debug 和 presentation pass 仍保留启动尺寸。lighting 按新 viewport 计算网格，却使用旧尺寸分配的存储。BindExternalTexture 本身不会因尺寸变化立即拒绝。

修正方向：窗口尺寸变化应驱动整套依赖尺寸的资源和 pass 数据更新。

### B20 [P1] 索引 BLAS 用顶点数计算三角形数

位置：[VulkanAccelerationStructures.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanAccelerationStructures.cs)。证据：静态确认。

CreateTriangleGeometry 把 geometry.Count/3 用作 primitive count，而 Count 是顶点数。4 个顶点、12 个索引应有 4 个三角形，此处只构建 1 个。

修正方向：索引几何使用 index range 和 index size 计算 primitive count。

### B21 [P1] 逻辑混合操作直接转换到不同顺序的原生枚举

位置：[VulkanPipelines.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanPipelines.cs)。证据：静态确认。

RHI 为 Clear、Set、Copy…，VkLogicOp 为 Clear、And、AndReverse、Copy…。直接 cast 让 Copy 变成 AndReverse，Set 变成 And。

修正方向：使用逐项显式映射。

### B22 [P1] 渲染区域内的查询先执行禁止在区域内调用的 reset

位置：[VulkanQueries.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanQueries.cs)。证据：静态确认。

BeginQueryCore 紧邻 BeginQuery 调用 CmdResetQueryPool；occlusion query 在 rendering 内开始时，该 reset 的原生位置非法。区域内 timestamp 同样受影响。

修正方向：在渲染区域外安排查询 reset，并保留区域内开始查询的公共行为。

### B23 [P2] shader table 的记录数被误限于 shader group 数

位置：[VulkanRayTracingPipelines.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanRayTracingPipelines.cs)。证据：静态确认。

Hit/Miss/Callable record count 不得超过 GroupCount 的检查，把可重复引用的记录实例数量与唯一 group 数量混为一谈。大量对象共享同一 hit group 的正常表被拒绝。

修正方向：逐条验证引用的 group，不限制记录总数为 group 数。

### B24 [P1] 普通非方形数组纹理被错误标成 cube compatible

位置：[VulkanResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanResources.cs)。证据：静态确认。

所有 2D 且层数至少 6 的 image 都增加 CubeCompatible；宽高不等的普通 Texture2DArray 因而违反 Vulkan cube image 的宽高相等要求。

修正方向：仅在合法且实际需要的 cube 能力条件下添加该 flag。

### B25 [P1] predication buffer 缺少原生 conditional rendering usage

位置：[VulkanResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanResources.cs)。证据：静态确认。

BufferUsages.Predication 只变成 IndirectBufferBit，没有 ConditionalRenderingBitEXT，但 SetPredication 会把该 buffer 交给 CmdBeginConditionalRendering。

修正方向：正确表达该 buffer 的原生用途。

### B26 [P1] 普通 shading 同步掩码无条件包含光追 stage

位置：[VulkanBarriers.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanBarriers.cs)。证据：静态确认。

AllShading/NonPixelShading 无条件加入 RayTracingShaderBitKHR；未启用相应光追能力的设备会收到不合法 stage mask。

修正方向：按已启用能力形成完整的 shading stage 集合。

### B27 [P2] D3D12 整子资源复制仍传入 source box

位置：[CommandCopies.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandCopies.cs)。证据：静态确认。

CopyTexture 始终传 &sourceBox。MSAA 或 depth/stencil 整子资源复制要求 pSrcBox 为 NULL，即使数值恰好覆盖整个子资源也不等价。

修正方向：按原生要求为这些整子资源复制传空 box。

### B28 [P2] 可选队列专用 COMMON 模式破坏跨队列类型交接

位置：[CommandBarriers.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandBarriers.cs)。证据：静态确认。

UseQueueSpecificCommonLayouts=true 时，release 的 after 和 acquire 的 before 也被转换为各自队列专用 COMMON，而交接需要双方均可使用的中性 COMMON。默认 false 不触发。

修正方向：队列交接保持中性布局，仅队列内使用专用布局。

### B29 [P2] D3D12 单 cube view 忽略起始数组层

位置：[DescriptorStorage.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/DescriptorStorage.cs)。证据：静态确认。

合法的 FirstArrayLayer=6、ArrayLayerCount=6 在 TextureCube SRV 中无法表达起始层，当前创建仍选择该形状，实际采样第一个 cube。

修正方向：选择能表达起始 cube 的 TextureCubeArray 描述。

### B30 [P2] bindless 空值被跳过写入而非形成空 descriptor

位置：[VulkanBindlessDescriptors.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanBindlessDescriptors.cs)。证据：静态确认。

新 generation 的 PopulateGeneration 跳过 IsNull，留下未初始化 descriptor；partially bound 与 null descriptor feature 不会把该位置自动写成可读取的 null。

修正方向：与其他 descriptor 路径一致，写入合法的有类型空值。

### B31 [P2] candidate 容量末尾的线程先越界读再检查

位置：[cluster_cull.slang](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_cull.slang)。证据：静态确认。

LoadAndFilterCandidate 在检查 MaxCandidates 前读取 CandidateClusters[threadId]。计数可溢出容量，dispatch clamp 后再向上取整为 64 线程；非 64 整数倍容量的最后一组可越界读。默认容量整除 64 不触发。

修正方向：在第一次读取前完成容量检查。

### B32 [P1] 允许的遍历容量可产生超出上限的一维 dispatch

位置：[cluster_bvh_traverse_seed.slang](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_bvh_traverse_seed.slang)。证据：静态确认。

节点按每组 4 个生成 Dispatch.X，未拆分二维 dispatch；超过 262140 个待遍历节点会超过 65535 组，而默认 MaxCandidates 为 1048576，现有容量检查不能排除此工作量。

修正方向：让容量约束与每个实际 dispatch 的组大小一致，或支持多维/分段分发。

### B33 [P2] 离轴球体光源的屏幕范围不是保守包围

位置：[clustered_light_grid.slang](F:/SomeEngine.Next-render-enhancement/assets/Shaders/clustered_light_grid.slang)。证据：数学反例，未做 GPU 图像复现。

投影范围以 x/z 为中心、r/sqrt(z²-r²) 为半径。x=8、z=10、r=1、投影比例 1 时右界约 .900504，真实切线右界约 .937040，遗漏可受光 tile。

修正方向：使用保守球体投影边界，并明确支持的投影类型。

### B34 [P2] 非均匀 stretch 后法线仍只做旋转

位置：[vertex_evaluate.slang](F:/SomeEngine.Next-render-enhancement/assets/Shaders/vertex_evaluate.slang)。证据：静态确认。

位置使用 Scale×Stretch，normal/tangent 只旋转；非均匀缩放需要法线逆转置处理，负缩放还涉及切线手性。

修正方向：让用户材质的默认顶点解释正确处理其公开支持的变换。

### B35 [P2] 一次加载失败永久占住资产缓存项

位置：[ResidentAssetTable.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Assets/ResidentAssetTable.cs)。证据：静态确认。

CompleteLoadAsync 保留 faulted entry，之后 LoadAsync 重用同一个失败 task；ReloadAsync 又要求已有 ready Value。临时 I/O 恢复后仍无法在同一个 loader 中重试。

修正方向：可恢复加载失败允许重新发起，区别于已证实损坏的不可变内容。

### B36 [P2] 所有页面读取失败都被永久拉黑

位置：[PageStream.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster/PageStream.cs)。证据：静态确认。

包括 SourceReadFailed 在内的失败全部加入 permanently failed 集合，后续 GPU page fault 也不再重试，必须重建整个 epoch。

修正方向：区分临时读取失败和永久无效页面。

### B37 [P2] 显式 shader 源码与磁盘缓存身份不一致

位置：[SlangShaderImporter.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Assets.Importers/Importers/SlangShaderImporter.cs)。证据：静态确认。

可选 source 参数参与编译，但依赖/缓存身份仍从磁盘计算，且允许写缓存；传入不同源码可覆盖磁盘源码对应缓存。

修正方向：编译输入必须参与同一缓存身份，或禁用该路径的磁盘源码缓存写入。

### B38 [P2] 构造失败后已转入字段的 GPU 对象没有清理

位置：[VirtualShadowFeature.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Features/Shadows/VirtualShadowFeature.cs)。证据：静态确认。

shadowMap/dispatchLayout 转入字段并把局部置空后，才开始 graph edit 和配置；catch 只释放局部。已有活动 edit 等确定性失败即可让这些资源失去清理入口。

修正方向：把所有可能失败的初始化纳入同一构造清理边界。

### B39 [P2] 稀疏映射原生坐标与托管记录不一致

位置：[VulkanSparse.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanSparse.cs)。证据：静态确认。

boxed native bind 使用 XYZ/宽高深，托管 replacement/copy 却沿 X+index 走；Reused 只在字典里重复一个 heap offset，原生 bind 仍映射连续 N tiles，且要求 N tiles 的 heap；opaque texture offset 又忽略 subresource 与 mip-tail 起点。

修正方向：以同一个 tile 地址展开算法生成原生绑定和保留记录，正确处理 boxed、reused、packed tail。

### B40 [P2] 多层附件被固定为单层 rendering

位置：[VulkanRendering.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanRendering.cs)。证据：静态确认。

VkRenderingInfo.LayerCount 固定为 1，没有使用附件 view 的多层范围。合法数组附件不能按描述向后续层渲染。

修正方向：从公开附件范围生成正确的层数及一致性约束。

### B41 [P2] shading rate image 的设置晚于实际生效时点

位置：[VulkanMeshAndShading.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanMeshAndShading.cs)。证据：静态确认。

SetShadingRateImageCore 只记录供下一次 BeginRendering 使用的 view；Graph RasterScope 在当前 BeginRendering 之后调用它，因此当前 draw 不生效，状态还可能影响下一个区域。

修正方向：让该状态在当前渲染区域创建时进入原生描述，并保持公共 API 的时序语义。

### B42 [P2] 公开允许的空场景和小尺寸不能经过完整流水线

位置：[ClusterPipelineSystem.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs)。证据：静态确认。

材料集为空时 reconcile 拒绝；1×1 output 可以构造，但 HiZ 录制要求至少两个 mip；启用 metrics 时固定复制 64 像素，不适用于更窄输出。

修正方向：统一公开输入范围与整条流水线要求，空场景应有明确正常行为。

## 维护性、冗余状态与可删除代码

以下按可核实的重复维护成本提出，避免把所有不同生命周期对象混为一谈。已有类名为定位现有代码而引用，不是建议采用这些命名。

### M01 删除 D3D12 未再入队的 descriptor 退休链

位置：[Descriptors.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/Descriptors.cs)。

CommitRetirements 已直接 recycle；_retiredHead 没有 enqueue，ReusableAfterGeneration 仅被写 0。链表、退休状态和代阈值保留了已不存在的算法。

### M02 删除没有数据的 FrameResourceScratch

位置：[ClusterPipelineSystem.Resources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.Resources.cs)。

类型只维护 Begin/End 的 bool；FrameResources._scratch 构造后从未读取。没有实际 scratch 数据需要保护。

### M03 删除重复且未被调用的 descriptor category 辅助函数

位置：[RootSignatures.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/RootSignatures.cs)。

外层 GetDescriptorSubObjectCategorySpace 有调用，builder 内同名副本没有调用。

### M04 缩减流水线镜像状态与手工回滚

位置：[ClusterPipelineSystem.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineSystem.cs)。

大量字段以及二十余组 pass/resource 列表在 ReconcileStageRegion 中手工镜像、恢复，同时 Graph 已管理相同对象的事务。应明确 Graph 持有的结构和流水线真正需要的外部对象，避免两套 rollback 清单。

### M05 把 binding 身份在编译产物中定清楚

位置：[VulkanPipelineLayouts.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanPipelineLayouts.cs)。

Vulkan 根据类型、名字/后缀和缺省排序重新整理 binding，另有 SPIR-V 改写；资产编译端已有反射。建议由编译产物提供完整目标 binding 映射。此项是重复解释与维护风险，不声称已复现任意同类型资源绑定错误。

### M06 明确 BVH CPU 副本的必要数据范围

位置：[ClusterPageStorage.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster/ClusterPageStorage.cs)。

上传后仍保留整份 CPU BVH 字节和按节点索引数组，页面分配信息也在多处映射维护。补丁需要的数据应有明确持有者，能从主要记录导出的数据不应各自维护。

### M07 Remove 同步释放材质的请求记录

位置：[MaterialBindless.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.MaterialBindless/MaterialBindless.cs)。

_textureRequests 直到整体 Dispose 才清空，Remove(material) 不移除该材质项，导致已移除材质继续被强引用。释放 descriptor 索引也需明确是否保留旧绑定引用。

### M08 去掉只有一个实现的泛型 dispatch 层

位置：[RhiDispatch.cs](F:/SomeEngine.Next-render-enhancement/benchmarks/SomeEngine.Graphics.Benchmarks/RhiDispatch.cs)。

IRhiDispatch<TReceiver> 当前只有 InterfaceReceiverDispatch 一个实现，却横穿 benchmark runner。保留真正对比的 direct/native 执行路径即可。

### M09 统一重复的屏幕 bounds/HiZ 算法

位置：[cluster_hiz_shared.slang](F:/SomeEngine.Next-render-enhancement/assets/Shaders/cluster_hiz_shared.slang)。

cull 与 shared 文件重复实现屏幕 AABB、近深度和 HiZ 采样，目标纹理版本又有一份过滤变体。共享数学逻辑，资源与区域作为明确参数。

### M10 统一代际读写完成条件的表示

位置：[RuntimeUiRenderer.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Runtime/RuntimeUiRenderer.cs)。

UI、Cluster 上传和实例存储各有写入代、可读端点及完成数组。它们的数据用途不同，可以各自持有数据；相同 GPU 完成语义不应分别用不同规则实现。

### M11 收窄分类系统销毁时的清理范围

位置：[RenderEntityClassificationSystem.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Systems/RenderEntityClassificationSystem.cs)。

OnDestroy 删除全 world 的 TMembership，而不是仅该系统生成的部分。要么明确一个 world/type 只有一个生产者，要么清理自己的实际贡献。

### M12 减少自制基础设施与无收益缓存

位置：[PipelineCache.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/PipelineCache.cs)。

D3D12 包含手写 SHA-256；state-object replay 缓存核对完整 cooked DXIL 后仍返回当前 libraries，并未省掉原生创建。应根据实际避免的成本判断缓存价值，不以 hit 计数替代收益。

## 门禁与 benchmark 的证明范围

按照用户最新要求，本节只审阅非测试的 gate/benchmark/tool 代码，不评价已经排除的测试质量。

### G01 [P2] 恢复结果没有证明当前 shader 是同一批

位置：[BenchmarkController.cs](F:/SomeEngine.Next-render-enhancement/benchmarks/SomeEngine.Graphics.Benchmarks/BenchmarkController.cs)。

CanReuseProcess 比较 runner/payload、adapter 与协议形状，没有与当前 shader manifest 比较。聚合使用 selected[0] 的 manifest；即使跨 variant 首项一致，同一 variant 后续进程也可能来自其他 shader。应逐进程核对当前 shader 身份。

### G02 [P2] 编译器版本证据写死为旧值

位置：[BenchmarkShaders.cs](F:/SomeEngine.Next-render-enhancement/benchmarks/SomeEngine.Graphics.Benchmarks/BenchmarkShaders.cs)。

manifest 的 Compiler 为 Slang 2026.4.2，而当前 Directory.Build.props 是 2026.14。此项影响结果可追溯性，应记录真正使用的编译器版本。

### G03 [验证范围] ThreeQueuePresent 输出 hash 没有依赖计算与复制内容

位置：[BenchmarkGate.cs](F:/SomeEngine.Next-render-enhancement/benchmarks/SomeEngine.Graphics.Benchmarks/BenchmarkGate.cs)。

该 workload 的 compute shader 为空，复制到 sink 的内容不进入最终图像或 readback 校验。它能证明这些命令路径被调用及呈现结果符合当前基准，不能证明计算→复制→消费数据链同步正确。

### G04 [验证范围] 预热平台未达到不阻止 Passed

位置：[RenderGraphCpuBenchmark.cs](F:/SomeEngine.Next-render-enhancement/benchmarks/SomeEngine.Graphics.Benchmarks/RenderGraphCpuBenchmark.cs)。

通过条件使用 CPU P95，WarmupPlateauReached 只记录。没有找到当前标准明确要求 plateau 才通过，因此这是统计解释边界，不计作违反既有门禁要求。

### G05 [验证范围] RuntimeAllocationTrace 不运行渲染帧

位置：[Program.cs](F:/SomeEngine.Next-render-enhancement/tools/RuntimeAllocationTrace/Program.cs)。

该工具循环消费 RuntimeStartupOptions 并 Sleep；报告中的零分配仅适用于该 consumer，不能证明 renderer 稳态帧零分配。工具已有相应 Scenario 名称，不能把它宣传为整条渲染流水线门禁。

## 原生 API 依据

B21 的枚举顺序已核对 [Vulkan VkLogicOp](https://docs.vulkan.org/refpages/latest/refpages/source/VkLogicOp.html)。B22 的 reset 时点依据 [Vulkan queries 规范](https://docs.vulkan.org/spec/latest/chapters/queries.html)，要求 CmdResetQueryPool 位于 render pass 之外。

B24 的 cube-compatible 宽高要求见 [VkImageCreateInfo](https://docs.vulkan.org/refpages/latest/refpages/source/VkImageCreateInfo.html)，VUID 08865。B25 的 buffer usage 要求见 [VkConditionalRenderingBeginInfoEXT](https://docs.vulkan.org/refpages/latest/refpages/source/VkConditionalRenderingBeginInfoEXT.html)，VUID 01982。B26 的光追 stage 启用要求见 [VkMemoryBarrier2](https://docs.vulkan.org/refpages/latest/refpages/source/VkMemoryBarrier2.html)，VUID 07946。

B27 的整子资源复制限制见 [D3D12 CopyTextureRegion](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12graphicscommandlist-copytextureregion)。B28 的队列布局约束见 [DirectX Enhanced Barriers](https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html)。

## 没有沿用的旧结论与未升级候选

当前 QueryRange 已被复制，同队列分段录制继承前序依赖，未使用 view 可跳过物化，raster 使用完整 Begin/End，buffer 未触及区间的边界状态得以保留，Read+UAV 已允许；旧报告对应指控不再照搬。软件光栅已使用组合 depth+ID 的 64 位原子写，shade bin 已实际创建并执行 dispatch；旧“未真正 shading”结论不适用。当前构建成功，旧构建失败结论也不适用。

以下候选没有足够证据，本报告不据此宣称确定错误：任意 Vulkan 同类型 descriptor 必然绑错；共享实例源必然违反两 world 架构；Mesh.Bounds.Radius 已造成当前 cluster culling 丢失；observer 自定义 Dispose 直接导致 frame cancel 卡死；窗口 resize 在 BindExternalTexture 处立即抛兼容错误；没有 GPU 复现的 shader 画面异常已经被实际观察。Vulkan presentation 相关实现当前能力入口未启用，其内部潜在问题不能描述为当前公开呈现路径的已复现回归。

## 修复次序建议

先统一实例写入期间的映射与 GPU 读者完成条件，修正上传确认和命令资源依赖；这些是后续优化与零分配指标可信的前提。随后修正 Graph 编辑/帧的失败恢复和生成材质继承，并处理原生枚举、查询、BLAS、数组纹理等可局部验证的映射错误。重复状态与死代码应随主要状态持有者明确而删除，避免再添加另一层标志位或镜像缓存。

验证以可观察行为和公共 API 为边界。现有探针可作为复核输入，不要求恢复被用户删除的测试，也不要求针对内部字段和具体实现结构编写测试。

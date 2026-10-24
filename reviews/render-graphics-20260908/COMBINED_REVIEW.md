# Render / Graphics 联合审查结论

本文件是合并阅读入口，补充并校正 [本轮完整报告](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/REPORT.md)。架构判断结合本次新提供的独立架构审查；具体问题结合最初两个粘贴附件。只修改审阅交付物，没有修改产品代码。

## 来源与判定规则

- **架构独立审查**：[本次附件](F:/.codex/attachments/91693a0f-4d32-4b2a-b0b0-8029cd728b18/pasted-text.txt)。12 项架构建议按下表逐项处理。附件内引用的 sandbox 完整文档不在本地，本次使用实际提供的全文，不声称读取了其链接后的额外文档。
- **最初附件一**：[原始文本](F:/.codex/attachments/6ccb2edf-9977-4f50-9ed7-6d021afc1c59/pasted-text.txt)。这个附件实际连续包含 133 项审查摘要与另一份 48 项摘要，两者以主工作树 HEAD b1d59a82 为基础。133 项本地索引及 48 项 FINAL-REVIEW 已核对；不能把它们的数量与当前问题相加。
- **最初附件二**：[原始文本](F:/.codex/attachments/b6b61531-65e8-4de9-a726-773ceedfbfbe/pasted-text.txt)。目标与本轮同为 render-enhancement、HEAD a7870000，但它明确是未完成全部阅读的阶段审查。
- **当前事实**：本轮 694 个非测试文本文件的阅读记录和当前源码，以及 20 项公共 API 复现。旧报告的实测属于其自己的工作树和当时状态，不自动提高当前结论的证据等级。
- **测试范围**：继续遵守“测试部分不看，以主工作树删减结果为准”。外部审查关于私有结构测试、旧编译失败及恢复测试的建议不自动进入当前任务。非测试 benchmark/gate/tool 仍纳入。

“合并保留”表示当前证据支持所指子项；“旧指控不适用”给出实际变化依据；“未采纳为当前确定缺陷”表示证据不足以支持原结论的全部条件，不等于宣布修复完成。不会把没有重新确认的旧指控换一个编号后直接入账。

## 架构：接受责任边界，修正已经过时的现状描述

独立审查最值得采纳的标准是：**两种表示可以存在，不能让两种可变表示各自决定同一个事实。** 可编辑图与执行数组、宿主参数与 GPU 字节、CPU 数据与 GPU 驻留都有不同用途；只有必要的主要持有者有权修改相应事实。

| 独立架构条目 | 当前判定 | 合并后的范围 |
| --- | --- | --- |
| A01 实例批次生命周期 | 保留并补充 | StorageSystem 的 _batches 与 Resources 的批次状态仍需协调；本轮 A01/A02/A03、B11/B12 和补充 S05/S06 提供具体后果。internal RenderInstanceSet 仍无外部产品消费者，可以删其替代路径；公开 RenderMeshInstanceSet 不能连带删除。 |
| A02 全量／增量扩展机制 | 保留；补入此前摘要遗漏的架构主题 | RenderExtractionSchedule 仍有 _systems.Count != 2，Mesh/Light 的增量路径特殊化，RenderWorld 仍手列 hook 类型。统一类型化变更应用机制；第三个提取器不应自动永久退回全量。历史推进还需独立于“组件是否变化”。 |
| A03 图声明—编译—执行边界 | 方向保留，旧错误证据收窄 | 当前 BuildWaves 已继承同队列前驱，不能再以旧 RG-005 声称实际顺序已经反转。仍应减少声明到执行的重复语义处理，删除 A07 所列无消费者状态；编译内部有多个转换步骤不等于存在多个互相冲突的调度权威。 |
| A04 图复用与第二条图路径 | 旧具体依据已不适用；保留设计约束 | 当前没有 ClusterGraphAuthoring，也没有旧 IsStableExecutionEligible 方法。不能再要求删除该文件或修旧资格分支。结构、影响分析的资源描述、纯绑定应区别处理；是否扩展缓存必须针对当前编译输入与真实帧测量。 |
| A05 GPU 状态与功能历史 | 保留，作为主要架构主题 | 对象存活、内容能否重写、CPU 借用结束、历史有效性分别决定。图输出实际资源状态与提交事实，功能负责历史失效；部分提交不能用一个成功/失败布尔值假装回滚。对应本轮 A02/A03/A04/A06/A11、B09、S09。旧 ClusterRenderHistory/旧视图位置索引例子不直接沿用。 |
| A06 shader 编译来源 | 旧“运行时读仓库源码”的主指控已不适用 | LiveShaderProgram 已不存在，LinkedComputePipeline 从 Shader.RequireEntry 取得 cooked Program/EntryPoint，Cluster 也消费用户 shader 的目标入口。剩余问题是 binding 解释与转换重复（M05）、输入/缓存身份（B37）、生成与标量编码错误；不应把当前体系重新改回源码依赖，亦未必要为此强制采用特定 Slang 预编译方案。 |
| A07 C# Type 与执行身份 | 旧 Type 主指控已不适用；映射边界保留 | ClusterMaterialTypeExecutionResolver 已不存在。当前 raster compatibility 使用实际 pipeline，shade compatibility 使用 Program/EntryPoint，并区分 command bindings。宿主类型继续服务参数 API；继承 slots（B15）和参数编码（B13/S07）仍需修。 |
| A08 Cluster 承担整套 renderer | 已部分完成，收窄剩余问题 | 旧 ClusterRendererSystem 已不存在，Lighting/Shadow/Temporal/后处理已进入 Features。不要再要求把已拆功能拆一次。ClusterPipelineSystem 仍有大量几何、材质、图变更和回滚状态，继续处理 M04；不需引入新插件总线。 |
| A09 descriptor 物化与保留 | 保留并明确两层处理 | counter 漏保留是具体实现遗漏；多条入口独立枚举依赖是架构维护问题。可以局部修复遗漏，同时收敛依赖规则。不可变 generation 的在途保护要保留；不能为减少对象数而删掉它。对应 A05/A09、B30、M01。 |
| A10 streaming 三类存储 | 保留，反对一概删 CPU 副本 | 区分长期 CPU 消费数据、上传暂存、GPU 驻留；BVH 的 CPU GetRange 等消费者必须考虑。M06 是确定必要范围与主要持有者，不是把整份 CPU 数据直接删掉。Sourcing 失败重试另见 B35/B36。 |
| A11 validation 的角色 | 保留并限定 | 独立验证模型有价值；真实生命周期由产品决定，校验观察所有终止事件。A08 的直接放弃命令清理缺口要修；公共 validation 对具体后端的依赖方向也应收窄。不能为了去重，让验证与产品共享同一个复杂判定从而失去独立性。 |
| A12 死模型与测试阻力 | 按当前消费者拆分采纳 | descriptor 旧退休链、state-object 重复 payload 仍成立。ClusterGraphAuthoring 已删除。internal RenderInstanceSet 无产品调用；但 ScalarLayout 当前确实被 ClusterMaterialStages 消费，不能列为无人使用。MaterialPass/公开 MaterialState 与 MaterialBindless 内部同名 MaterialState 必须区分。测试部分按用户要求排除。 |

关键当前定位：[实例存储登记](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceStorageSystem.cs:21)、[第三提取器触发全量](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Systems/RenderExtractionSchedule.cs:182)、[hook 名单](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Systems/RenderWorld.cs:38)、[同队列提交前驱](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameRecording.cs:146)、[cooked compute 入口](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Assets/LinkedComputePipeline.cs:70)、[材质实际执行组合](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs:79)、[ScalarLayout 实际消费者](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render.Cluster.Pipeline/ClusterMaterialStages.cs:1124)。

### 最终应形成的责任边界

| 主要持有者 | 唯一决定的事实 | 合理的派生数据 |
| --- | --- | --- |
| Game World | 游戏侧可编辑状态与资源引用 | 提取输入范围 |
| Render World | 渲染组件、关系与视图身份 | 查询结果、分组 |
| 实例存储 | 批次身份、属性内容、当前代、可写条件 | 上传范围、批次视图 |
| RenderGraph | 访问依赖、执行安排、瞬态复用、实际执行后的资源状态 | 紧凑执行数组与诊断输出 |
| Graphics 后端 | 原生对象保留、命令依赖、提交接受和完成 | 各后端不同的 descriptor/内存物化 |
| 渲染功能 | 自己的数据、历史有效性和失效规则 | 历史纹理、灯光网格、阴影缓存 |
| Runtime | 功能组合、窗口与帧循环连接 | 尺寸变化驱动的重建请求 |

这些是责任边界，不是要求新增七个管理类。Game World → Render World → GPU 的数据通路保持不变；GPU 数组不是第三个可编辑 World。用户 shader 继续解释顶点，不增加 CPU 顶点布局解码通路。

## 具体问题：原始第二份审查逐项合并

本表的“二”指最初附件二。架构 A1–A5 也保留其具体症状，避免因这次按架构/实现分工阅读而漏项。

| 来源条目 | 本轮对应 | 联合判定 |
| --- | --- | --- |
| 二 A1 validation 放弃命令 | A08 | 保留 |
| 二 A2 上传提前确认 | A04 | 公共 API 已复现，纹理显式取消已有处理，但录制失败仍错误确认 |
| 二 A3 固定三个队列 | A03 | 四个真实队列已复现越界；不能只把数组改成 4 |
| 二 A4 state-object cache | M12 | 保留，限该缓存路径 |
| 二 A5 重复 HiZ | M09 | 保留为维护性，重复本身不是画面错误证明 |
| 二 I1 Execute 参数失败 | B01 | 已复现 |
| 二 I2 旧 edit | B02 | 已复现，证据强于原阶段报告 |
| 二 I3 范围下溢 | S08 | 修正位置及 DEBUG 生效范围 |
| 二 I4 UAV counter | A05 | 具体遗漏可局部修；重复依赖收集归架构，两种层次不冲突 |
| 二 I5 Vulkan 前缀 claim | B10 | 已复现 |
| 二 I6 日志 128 上限 | B16 | 当前静态保留；旧主工作树的实测不记为本轮实测 |
| 二 I7 等待失败丢记录 | A02 | 已复现；漏清理时序是实现问题，完成条件的主要持有者是架构边界 |
| 二 I8 resize | B19 | 保留；已排除“BindExternalTexture 必然立即拒绝”的说法 |
| 二 I9 Vulkan layout 清理 | S02 | 补入此前主报告遗漏项 |
| 二 I10 candidate 先读后查 | B31 | 保留非 64 整数倍容量的触发限定 |
| 二 I11 离轴球体投影 | B33 | 数学反例成立，不冒称 GPU 图像复现 |
| 二“暂不确定”上传 backing | 不升级为确定缺陷 | 局部 copy completion 与外层协调须合看，没有证据就不宣称正常帧覆盖 GPU 数据 |
| 二“暂不确定”null descriptor | B30 | 仅确认 bindless 跳过空 descriptor 写入的子项 |
| 二“暂不确定”previous transform | B18 | 当前静态路径支持；区别全量和增量行为 |
| 二“暂不确定”D3D copy/恢复 | B27 等 | 只保留本轮已明确验证的子项，不整体升级 |

## 具体问题：第一份附件及其内附 48 项的完整追溯

[逐项对应表 CSV](F:/SomeEngine.Next-render-enhancement/reviews/render-graphics-20260908/independent-review-crosswalk.csv) 包含 133 项索引和内附 48 项索引，共 181 个来源条目。来源编号独立命名，不与本轮 A/B/M/G 或补充 S 编号混用。相同问题会有多个来源，不能计成多个缺陷。

133 项来源中，49 项按当前证据合并或收窄，11 项旧指控已有明确的不适用依据，16 项按测试排除要求处理；其余 57 项没有直接采纳为当前确定缺陷，逐项保留原题目和理由。**这 57 项不等于确认已修复，也不等于仍有 57 个已确认 bug。** 该清单防止把历史指控无依据升级为当前事实；如以后要求逐条关闭所有历史条目，需要为这些条目补足对应证据。

最重要的冲突已按当前代码解决：

- 旧“没有实际材质 shading”“depth 与 visibility 分开竞争”不适用：当前已有真实 shade dispatch 和 64 位组合 winner。
- 旧 QueryRange 漏复制、未物化 view 仍创建、默认 suspend/resume、同队列前驱缺失等均不能继续沿用。
- 旧 shader-visible descriptor 复制源已改为 CPU descriptor 来源，不能照抄 D3DP-001/C25；当前 descriptor 生命周期遗漏是另一组问题。
- 旧 SlotMap.Contains 不检查所属图的描述已变化；Release 路径是否跳过关键检查是另一个问题，不能以旧函数体当证据。
- 旧测试编译阻断和旧 shader 测试失败不是当前状态。本轮 Release 构建成功；测试删除不自动判作回归。

## 补入此前摘要遗漏的当前具体问题

以下 16 项均为当前源码静态证据，**没有增加本轮公共 API 实测数量**。旧审查如有旧版本探针结果，只作为来源记录。S08 的可关闭校验问题与 Release 功能检查明确区别。

### S01 [P1] swapchain 初始化失败存在同一 native 引用的双重释放

类型：实现。来源：DL-002 / C28。当前位置：[Presentation.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/Presentation.cs)。

CreateRegisteredSwapchain 把 native 交给 wrapper；初始化/注册失败时 Dispose wrapper，外层 native 仍未置空，finally 再 Release。同一份拥有权有两条失败清理路径。

### S02 [P2] Vulkan 管线构造的清理区间晚于取得 layout

类型：实现。来源：VK-012 / C32 / 独立审查二 I9。当前位置：[VulkanPipelines.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Vulkan/VulkanPipelines.cs)。

取得 native layout 后，编译和绑定归一化仍在清理 try 之外；失败时遗漏 layout。layout builder 的早期 static sampler 创建也需纳入同一失败清理边界。

### S03 [P2] 集合句柄缺少所属集合身份

类型：实现。来源：RENDER-005 / C03。当前位置：[RenderMeshInstanceCollection.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderMeshInstanceCollection.cs)。

句柄只包含 Index/Generation；两个集合分配同位置同代次时，另一个集合的句柄能命中无关对象。

### S04 [P2] 共享源捕获失败又被不完整回滚覆盖

类型：实现。来源：RENDER-006 / C04。当前位置：[RenderMeshInstanceCollection.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderMeshInstanceCollection.cs)。

同一 buffered set 可重复加入集合；捕获同源两次会遇到不可递归读锁。destination++ 在 Capture 成功前推进，catch 对尚未构造的条目执行清理，覆盖原异常。当前任意-source 构造入口已收窄，不能继续以旧公开构造签名举例。

### S05 [P2] 同一代原地改写的取消不恢复已发布字节

类型：实现。来源：RENDER-008 / C13。当前位置：[RenderInstanceResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceResources.cs)。

CancelBatchUpdate 恢复 ready，却没有恢复同一 generation 已写入的内容。“取消”若保留其事务语义，必须隔离未发布数据或具备实际恢复；关闭写入口不能充当回滚。

### S06 [P2] metadata-only 编码仍被无条件要求线性属性存储

类型：实现。来源：RENDER-009。当前位置：[RenderInstanceResources.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Instances/RenderInstanceResources.cs)。

允许零 StorageStride 的元数据编码时不分配属性 buffer，但 ResetRows、GetBatchView、CommitReadEndpoints 仍要求 PropertyData。公开允许的编码无法经过正常使用路径。

### S07 [P2] 有符号整数先转 uint 再转 float

类型：实现。来源：RENDER-010 / C11。当前位置：[ScalarLayout.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Render/Materials/ScalarLayout.cs)。

负 int 的浮点编码因此成为大正数。它与 B13 的字段顺序/hash 错误是同一类中的不同缺陷，应分别验证结果。

### S08 [P2] 范围包含的无符号减法仍可下溢

类型：实现／可关闭校验。来源：RG-009 / C17 / 独立审查二 I3。当前位置：[FrameCommandsValidation.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameCommandsValidation.cs)。

子范围起点超过父范围末尾时，剩余长度减法回绕。当前位置是 Graph 命令声明校验，受 DEBUG 条件控制；独立审查二 I3 所写 Graphics.BufferRange.Contains 并不存在。不能把它描述成 Release 总是开启的保护。

### S09 [P1] 纹理初始完成条件没有覆盖每个子资源的首次使用

类型：实现。来源：RG-006。当前位置：[FrameSynchronization.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameSynchronization.cs)。

初始等待挂到 accesses[0]；另一个子资源在不同但同类型队列首次使用时，ownership-transfer 路径又因队列类型相同跳过，不能由整纹理首用推导其已等待。

### S10 [P2] General 内部区域之间的依赖未进入实际屏障位置

类型：能力表达／实现。来源：RG-007。当前位置：[FrameRecording.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameRecording.cs)。

访问和转换仍以 pass 为单位组织，多个区域之间需要的不同 layout 无法在中间位置表达，冲突可能在声明解析时被拒绝。不得把静态表达缺口一概宣称为已运行的 GPU 数据破坏。

### S11 [P2] rendering region 的位置和数组层范围未被执行消费

类型：实现。来源：RG-025。当前位置：[FrameRecording.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph/FrameRecording.cs)。

保存 X/Y/FirstArrayLayer/ArrayLayerCount，构造实际 rendering 主要只消费 Width/Height。修正应贯通实际原生描述，而不是仅在声明中保留字段。

### S12 [P2] 诊断序列化不能表示 query/shader-table 资源

类型：实现。来源：RG-027。当前位置：[RenderGraphSnapshot.Validation.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.RenderGraph.Diagnostics/RenderGraphSnapshot.Validation.cs)。

资源行只有 Buffer/Texture，QueryPool/ShaderTable access 对应资源数量为 0，合法图诊断无法按自身格式 round-trip；BufferContentScope 也未完整保留。

### S13 [P2] 不同 hit group 复用同一 hit shader 被重复名检查拒绝

类型：实现。来源：D3DP-003 / C29。当前位置：[RayTracing.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/RayTracing.cs)。

shader export 身份与 hit-group record 身份混在一个名字检查中。Validation 同样有跨组成员去重问题。应分别判断 group 名称唯一性和可复用 shader 引用。

### S14 [P2] 多个运行选项解析成功却不被 Runtime 消费

类型：实现。来源：RENDER-012 / 48项审查 M07。当前位置：[RuntimeStartupOptions.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Runtime/RuntimeStartupOptions.cs)。

PresentSyncInterval、FPS/UPS、PipelineWarmup 等字段进入选项但帧循环未据此改变行为。只应保留真正支持的选项，或接入实际消费者。

### S15 [P2] 相机右方向与右手视图的屏幕右方向相反

类型：实现。来源：RENDER-011 / C37。当前位置：[RuntimeCamera.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Runtime/RuntimeCamera.cs)。

Right 使用 Cross(up, forward)。朝向 -Z 时得到 -X，而 CreateLookAt 的屏幕右方向为 +X，横移符号相反。

### S16 [P2] 持久参数创建在取得 descriptor 引用后仍有未保护区间

类型：实现。来源：DL-010 / C27。当前位置：[CommandBindings.cs](F:/SomeEngine.Next-render-enhancement/src/SomeEngine.Graphics.Direct3D12/CommandBindings.cs)。

CreateDescriptorRecords 已取得保留引用，CaptureSwapchainBindings、CreateOrdinaryDataResource 的失败发生在对应释放 try 之前，遗留 descriptor 依赖。

## 对此前报告的分类修正与最终使用方式

此前报告 A05 把“漏保留 counter”与“重复依赖枚举”放在同一条，容易让人误以为必须重构后端才能修一个依赖遗漏。联合结论将其拆成两个层次：当前具体使用必须正确保留 counter；架构上收敛各入口依赖规则，防止下一次遗漏。A02 的等待前清空同理：局部错误先保证未完成记录不丢，完成集合的表示和主要持有者再统一。两者不是互相排斥的审查意见。

工作入口以本文件为准，详细原始位置和 20 项复现见 REPORT.md，历史条目取舍见 crosswalk.csv。本轮原 54 项及 M/G 编号保持不变，避免引用失效；补充项使用 S01–S16，架构表补入提取扩展和批次状态等主题。不要将不同层次或不同来源的条目直接相加作独立根因数量。

修正结果应同时满足：失败后可继续合法使用；GPU 正在读的数据不可被提前改写；实际被接受的工作保留其资源和完成义务；功能历史只在符合自身有效条件时发布；新增提取器和材质组合不要求修改通用核心的具体类型名单。删冗余以减少独立修改点为标准，不以类数量、行数、零分配或文件形状作架构验收。

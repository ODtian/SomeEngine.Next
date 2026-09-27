# 渲染整体架构落实与闭环核查

**交付状态：已完成本轮审查范围的实现与验证。** 逐项 B/S/M/G 结论、公共行为验证及适用限制见 [最终交付记录](Render-Delivery-2026-09-09.md)。下文保留架构审查过程，较早段落中的“待核查”由文末结论及最终交付记录更新。

本文记录当前交付的架构证据，不以构建或测试通过代替架构判断。已有审查报告说明问题来源；本表说明最终责任边界及当前仍须落实的范围。历史报告中的旧类名、旧测试结果不自动成为当前证据。

执行约束：先落实并逐项核查全部整体架构，确认整体无误后再集中处理具体实现问题。只有某一大架构整体闭环后，才运行该架构相关测试。最终清理内部实现测试，补齐必要公共 API 与功能测试。

## 闭环标准

每项都必须找到真实的代码入口，并沿同一事实追踪：创建、正常读取、值修改、结构修改、取消、提交前失败、部分提交、等待失败、重试、尺寸或容量变化、移除与销毁。不适用的路径要说明理由，不能留空后标为完成。

主要持有者只能有一个。索引、GPU 字节、执行数组、在途代次可以派生，但不得独立决定源数据、可写条件或已提交事实。已知变更直接更新实际受影响范围；非结构数据变化不重建图，局部结构变化不全量比较或重建无关结构。GPU 已接受的工作不可通过恢复 CPU 字段假装撤回。

## 整体边界与当前核查状态

下表保留开始本轮核查时的范围，具体落实证据见后续段落及文末责任边界结论。架构责任边界的静态核查已经完成；实现验收与公共行为验证仍未完成。

| 项目 | 唯一事实与主要持有者 | 当前代码入口和已有结构 | 待落实或核查的完整边界 |
| --- | --- | --- | --- |
| 产品组合与依赖方向 | 宿主选择功能；功能决定自己的数据和输出；通用层不反向选择功能或后端 | `SomeEngine.Runtime/RuntimeApplication.cs`；各项目的 ProjectReference；`RenderExtractionSystems` 的显式组合 | 核查没有 Mesh、Light、Cluster 或 presentation 的组合；Features 对 Cluster 的依赖；通用 Validation 已拆出 D3D12 原生访问适配；Render 程序集文档所述领域边界与当前 Graphics、Graph 引用需要统一 |
| Game World → Render World | 游戏数据归 Game World；发布后的渲染组件归 Render World；提取系统只产生候选变更 | `Render/Systems/RenderExtractionSchedule.cs`、`RenderWorld.cs`、`RenderPrepareSystems.cs` | 已有各提取器的增量入口和输出组件声明；逐条核查自定义提取器、关系与删除、hook、取消和重试；共享可变 Material 与 asset reload 的可见时机必须服从同一更新边界 |
| 实例数据与批次 | 实例存储唯一决定批次身份、属性内容、代次、行分配和可写条件；system 只协调 | `Render/Instances/RenderInstanceResources.cs`、`RenderInstanceStorageSystem.cs`、`RenderInstanceBatchBuilder.cs` | system 已不维护独立批次表；还须核查公开 batch 的修订数据、写入备份、共享映射、取消、等待失败和销毁；核对 `RenderInstanceSet.cs` 的实际消费者，删除没有产品用途的另一条路径 |
| 持久图声明与变更 | GraphStorage 保存唯一声明与当前值；反向索引和执行数据消费明确变更 | `RenderGraph/GraphStorage.cs`、`GraphEditChanges.cs`、`GraphStructureIndex.cs`、`RenderGraphEdit.cs` | 已有共享不可变根和直接变更索引；逐项核查所有编辑 API、输入变更、外部绑定、范围变化、取消、ID 复用和并行在途图；纯值变化不能走结构编辑 |
| 持久执行与资源推导 | Graph 根据实际访问事实决定依赖、顺序、队列、同步和瞬态复用 | `RenderGraph/FrameAnalysis.cs`、`FrameSchedule.cs`、`FrameSynchronization.cs`、`FrameSubmissions.cs`、`ResourceAllocators.cs` | 核查每一种变更到裁剪、顺序、队列邻居、参数连接、分配与提交组的传播闭包；不能只核查新增局部路径而遗留全量入口；各在途执行必须消费变更，不能逐帧重建语义 |
| 提交、完成与历史 | RHI 决定是否接受提交和完成；Graph 保留实际资源边界；功能决定自己的历史是否有效 | `RenderGraph/FrameRecording.cs`、`FrameStateCommit.cs`、`RenderGraphFrameExecutionObserver.cs`；`Render/Frame/RenderFrameCoordinator.cs`、`RenderTimeline.cs`；`ClusterCullingHistory.cs`、`TemporalFeature.cs` | 已有执行结果观察和各功能历史处理；沿未提交、部分提交、观察回调失败、release 成功而 acquire 失败、等待失败、关闭和重试逐项核查；完成信号不能被一个帧成功布尔值替代 |
| Shader、材质与参数 | cooked artifact 决定代码和布局；宿主类型只提供参数；实际 shader 组合与资源绑定决定执行分组 | `Assets/Schema/Shader.cs`、`Material.cs`、`MaterialData.cs`；`Render/Assets/LinkedComputePipeline.cs`、`Render/Materials/ScalarLayout.cs`；`ClusterMaterialStages.cs` | 材质通过提取后的独立值和属性修订号进入 Render World；管线不再订阅游戏材质事件。继续核查 cooked 构建产物、缓存身份、reload 和资源寿命；不引入运行时顶点元数据解释。结构变更仍有全量解析入口，尚未闭环 |
| Cluster 的图持有范围 | Cluster 仅持有几何功能数据；Graph 持有其声明；按具体分组更新实际 Pass 与资源 | `ClusterPipelineSystem.cs` 及 Raster、Shading、Traversal、Resources partial；`ClusterMaterialAssignments.cs`、`ClusterFrameUploads.cs` | 已有按 bin 的光栅组、共享细分组和 shade 组；实例分配现在保留序列偏移，分组直接消费已变更地址；材质缓冲登记和移除已与使用它们的 Pass 共用图编辑；继续核查提交后上传退休失败、各组与图取消的一致性 |
| 功能、视图与尺寸变化 | Lighting、Shadow、Motion、Temporal 等只持有自己的资源与历史；宿主连接显式输入输出 | `SomeEngine.Render.Features`；`RuntimeApplication.cs` 的组合与 resize 路径 | 核查输入依赖、禁用/启用、历史视图身份、尺寸变化和功能销毁的影响闭包；不能重建无关功能或由 Cluster 驱动整套 renderer |
| CPU 数据、上传与 GPU 驻留 | 资产/流送持有有 CPU 消费者的数据；上传系统持有传输暂存；驻留系统持有 GPU 页和完成条件 | `Render.Cluster/ClusterRenderResources.cs`、`ClusterResidency.cs`、streaming 相关入口；`Render.MaterialBindless/MaterialBindless.cs`；`ClusterFrameUploads.cs` | 核对每份副本的实际读取者、保留期限和释放依据；上传确认必须来自实际执行，取消与录制失败不能提前确认；流送失败重试、feedback 容量和驻留发布都须跟踪 |
| RHI 原生依赖与生命周期 | 后端对象持有原生资源；同一规则收集命令、descriptor、sparse 等引用；队列完成释放在途引用 | D3D12 的 `Descriptors.cs`、`DescriptorStorage.cs`、`SparseResidency.cs`；Vulkan 的 `VulkanDescriptors.cs`、`VulkanBindlessDescriptors.cs`、`VulkanSparse.cs`；两端 Queue 与 Command 入口 | 已收拢部分引用与退休路径；逐条核查直接绑定、bindless、UAV counter、sparse、state object、录制失败、提交失败、设备移除及销毁；旧退休链和重复 payload 需以实际消费者判定 |
| Validation 与失效观察 | 产品决定生命周期；可关闭 validation 独立观察合法性及所有终止事件 | `Graphics.Validation/ValidationLayer.cs` 与各 partial；Graphics 公共对象事件 | 核查直接 Dispose、放弃录制、失败、设备移除都撤销观察记录；拆除通用层对具体后端的依赖；功能必需检查保留在产品，可关闭检查不维护第二套产品状态 |

## 已确认的架构缺口

1. `ClusterMaterialAssignmentSystem.OnUpdate` 已不再通过 AssignmentBuilder、HasSameTopology 和 HasSameEntityMappings 重建/比较全场景分配。候选共享不可变映射；修改只更新对应实体、材质序列引用和地址。移除序列不重新编号其他序列，同长度空闲范围可重用。ECS 现在提供归实体存储根持有的 EntityTableChanges：创建、销毁和迁表记录实体身份；读取后的批次必须显式接受，事务取消不发布候选通知。提取和材质分配已消费这些已知成员变化，不再依靠全局拓扑修订扫描成员。还须核查多次取消、实体代次重用和提取失败的完整传播。
2. `ClusterRasterStages.Resolve` 与 `ClusterShadeStages.Resolve` 已沿 changed offsets 和 material→offset 反向索引定位实际需要解析的 shader 组合。bin 索引按地址维护成员及共享不可变组，未变化组不重新解析或构造；变更集合持续保留到图接受。已删除全量 HasSameTopology 比较。标量区按受影响地址保留或回收分配，不再全量编码。标量字节、标量偏移和 bin 索引使用共享不可变字节页，局部变更只复制涉及的页；上传代次分别保留合并后的变化区间，不逐次追加重复范围。容量增长创建新的 native backing 时仍须写满新 backing，这是物化所需，不重新解析未变材质。仍须核查整个持久图传播与功能验证，不能据此宣布整体全部完成。
3. 材质缓冲登记与引用计数已按 changed material ids 更新；共享引用归零时，只有本功能登记的缓冲才随消费者在同一图编辑内移除。候选登记表只在图提交成功后切换。标量编码移到图提交前，避免普通编码错误发生在图已接受之后。已读 `RetireAfterSubmittedFrames → PersistentResourceAllocator.Retire`：该路径只登记退休项，不执行 Dispose 回调；完成后的回收另由 CollectCompleted 执行。仍需核查图编辑本身的所有失败边界及上传回收失败重试。
4. 通用 `SomeEngine.Graphics.Validation` 已取消 D3D12/DXGI 产品依赖；原生指针访问、导入选择和队列锁适配位于新的可选项目 `SomeEngine.Graphics.Direct3D12.Validation`。后者单向引用后端和通用 Validation。通用层仅观察 RHI 对象登记、命令资源与能力，内部适配入口不公开为第二个资源管理 API。原生对象和锁仍由后端持有，适配中的队列校验记录通过弱表关联，避免校验记录保持队列存活。仍须核查所有终止事件及功能测试；编译通过只证明引用和类型连接可用。
5. 空场景不再要求存在材质。单组实例构建器在零实体时也通过实例存储发布零行批次；零行分配不占用或归还行范围，批次元数据与释放仍走同一条存储路径。bin 容量至少为 1，但没有伪造材质或执行组。分桶、输出清除、空场景→有内容→空场景和 GPU 在途状态仍待功能闭环核查。

## 本次续接的验证范围

已进行产品编译连接检查：D3D12 Validation 适配项目、Cluster Pipeline 及 Runtime 的 Release 编译成功。没有运行测试；没有将编译成功或测试通过数记作任何整体架构的完成证据。现存内部实现测试尚未执行最终删改，公共 API/功能测试尚未补齐，整个交付仍未完成。

## Game World 材质与共享资产的当前证据

- `MeshMaterialBinding` 持有游戏侧 `Material`；`RenderMaterialBinding` 只持有 `MaterialId`。Rendering world 用独立的 `RenderMaterialValue` 实体持有每份提取所得 `MaterialData`，通过 `RenderWorld.ReadMaterial(id)` 读取。共享材质值只更新一次，不重写引用它的实例绑定；数据与绑定仍在原有提取边界提交。
- 材质通知只由 `MeshRenderExtractor` 订阅。源实体与材质的正反索引都属于提取输入关系，解绑和销毁撤销订阅。脏通知在已发布 Render World 确实包含对应值后才消费，因此提取或应用失败不会丢失通知。材质缓冲写入使用 ECS 事务。
- Cluster 和 MaterialBindless 消费 `MaterialData`。Cluster 的标量变化沿已变更实体行、材质身份和属性修订号传递，保留绑定偏移；图接受的分配根与最新提取根分别代表两个不同的有效版本，不另建一份可编辑材质表。
- `AssetLoader.ReloadAsync` 只排队，`ApplyUpdatesAsync` 在宿主的共享内容更新边界执行。宿主现在先等待后台 Game World 提取退出，再应用替换；之后才进入渲染准备。纹理 GPU 替换由 `RenderDevice.Textures.cs` 按资产修订处理，具体在途资源与失败路径仍属于下列未闭环项。

本项仍未整体闭环：Game World 结构变化的收集路径、共享资产更新失败与旧资源寿命、自定义提取器失败和销毁路径，以及绑定结构变更到分组/Pass 的完整增量传播仍需核查。以上证据只说明已落实的边界，不替代这些剩余路径。

## 本次继续核查：图失败与资源回收

- 图编辑仍以候选声明为边界。持久资源创建失败后，候选中本次创建的资源和视图指针清空；无资源的预留范围立即归还；已创建对象进入同一退休表，只有 Dispose 成功才释放对应范围。失败编辑新建的堆被标记为不可再分配，范围全部释放后回收堆与预算。原生清理失败保留记录供重试，并同时报告原始创建失败。
- 图关闭等待真实完成状态；WaitCpu 未返回 Completed 时不释放。进入清理后阻止新操作，但只有所有资源清理结束才记录销毁完成，因此失败后再次 Dispose 继续清理。命令池和帧附属对象在成功释放后逐个移除记录。
- 瞬态视图与资源在 Dispose 成功后才撤销索引。显式压缩与失败后的重试逐项移除已回收内容，堆预算在堆释放成功后归还。失败使该瞬态分配器必须完成清理后才能再次使用；下一次帧开始将相关瞬态资源标记为需要重新实例化。帧开始失败会归还 CPU 帧占用，避免留下无法取消的帧。
- 上传页在映射前已经有明确清理记录；映射失败时释放该页，清理失败保留页及预算且阻止继续上传和执行。销毁过程不会重复归还已成功回收页的预算。
- 以上是已落实的失败边界，仍需继续核查瞬态分配失败后的变更传播、所有提交路径的状态推导、原生对象自身的终止事件，以及对应公共行为测试。

## 本次继续核查：网格与视图

- ClusterMeshPrepareSystem 已按 Render World 成员批次和 RenderMesh 行变化维护引用表；注册失败的对象保留在注册集合，成功项逐个移除，不再凭全局拓扑修订跳过同一实体上的组件替换。
- `RenderMesh` 现在持有不可变 MeshData，Cluster 按内容对象登记驻留根；同一资产 reload 产生新内容身份。资产保留仍被内容版本引用的旧数据源，流送登记再保留自己的读取引用；认证页描述和摘要在打开源时固定。尚未发布的提取继续使用其已捕获版本，下一次提取消费 reload 通知。公开资产 Dispose 结束其内容获取能力，已经登记的流送引用独立完成自己的寿命。
- Cluster 视图历史现在保存 Render World 实体身份；同一层换成另一视图实体、CameraCut 或没有提供身份时，不沿用前一个视图的 HiZ。阴影图按实际 Graph 执行结果保存每个资源的边界状态；部分提交保留已发生的队列状态但失效内容历史，未提交不推进历史。空光源/实例允许零语义视图，原生最低容量不伪造光源或材质。

## 资产、纹理和实例的进一步核查

- `AssetUpdate.Accept()` 是加载器的内容接受点。内置重载先验证/构造，再替换内容并接受；通知和旧资源清理放在接受之后。所有通知接收者都会收到事件，单个观察者抛错不跳过提取器。加载器不通过回滚修订号掩盖已替换内容；失败清理保留候选或清理回调，在后续更新/关闭时重试。关闭完成以前保留底层 storage。
- `MaterialData` 中的纹理值和 `RenderLightCookie` 使用 TextureData。每个版本固定元数据及 tile 选择；提取器维护实际 material→texture、cookie→texture 关系，纹理通知只更新对应共享材质或 cookie 的源实体。材质捕获版本包含嵌套纹理的版本，属性版本只对实际变化属性推进。Game World 原材质与纹理对象不作为 render material 的可编辑值暴露。
- RenderDevice 的纹理缓存按资产身份和内容修订索引，旧内容使用弱关联，创建后续版本时回收同一资产下已无内容引用且无活动 CPU 帧的旧缓存。每次导入纹理均登记 Graph 结束观察，保留实际子资源状态和队列完成条件，删除默认 Graphics 队列/ShaderResource 状态猜测。未提交、部分上传、完整上传分别处理；上传事实由最终相关 Pass 是否接受决定。
- 实例存储的可用性查询不再固定写入代次。只有取得 prepare timeline 并打开映射的写入范围才持有该代次。刷新失败保留该代次未刷新状态；渲染读取先重试刷新，成功后才绑定 GPU 资源。批次候选取消恢复原字节和可见状态。没有产品消费者的 internal RenderInstanceSet 已删除；公开 RenderMeshInstanceSet 保留。
- Validation 的身份登记现在订阅 GraphicsObject 的 Dispose 事件，不再通过新建对象时全表扫描来发现终止。Queue 的观察表随所属 Device 终止清理；可执行命令独立订阅放弃/设备终止事件，已提交观察记录由提交结果移除。底层终止通知逐个派发，观察者失败不能阻止其他观察者或原生收尾。
- 阴影页表的尺寸候选保留旧资源和旧有效状态，未提交取消恢复旧页表并交回未使用的新表供回收；部分提交接受新表及实际边界，同时使内容历史失效。取消也恢复阴影帧计数和候选重置标志。零视图始终上传一个 native 最小元素，语义 ViewCount 仍为零。
- HiZ 的候选可复用标志与已发布相机历史分开，取消不抹掉旧相机状态；Temporal 使用明确视图实体，未提供身份时不复用前一帧历史。宿主使用实际 Render World 相机实体。

这些条目只描述已核查、已落实的责任边界。目前仍在继续整体图传播、流送/原生依赖和功能组合的核查，没有开始测试与最终实现项验收。

## 最终交付判定

每个大架构在本页补充实际入口、所有适用变更/失败路径的核查结果和剩余项，才可以标为闭环。所有大架构闭环后统一处理仍成立的具体实现问题。最后依据用户可观察行为补齐测试并清理内部实现测试；测试只验证功能后果，不反过来定义持有者、依赖方向或架构正确性。

## 图结果与可选组合的补充证据

- Runtime UI 已移除宿主手动 Commit 和按 Graphics 队列猜测缓冲状态的路径。UI 登记 Graph 结束观察；部分提交也先保留实际完成条件，再保存图给出的各缓冲范围状态，最后结束 CPU 候选。执行前失败仍由宿主 Discard 清理尚未登记的 UI 数据。
- RenderPrepareSystems 与 RenderFrameSystems 可以不提供实例存储。只有请求实例数据的功能才要求该存储；空提取组合不登记源查询或成员观察。prepare scope 自身跟踪 system 操作，避免无实例组合绕过更新中的关闭检查。RenderDevice 的缺省纹理按实际请求创建。
- 同步推导先在独立工作数据中完成全部范围和队列关系，成功后才替换单资源的旧屏障及反向索引；失败保留原缓存和待消费变化。纹理每个区域的首个使用者分别继承输入完成条件，不能由第一个整体访问代表所有子资源。
- Cluster 的驻留候选由 ClusterResidency 持有，部分上传继续保留该候选，全部相关上传 Pass 被接受才提交；页退休查询 Graph 的实际范围完成条件。CPU BVH 继续服务范围读取，页传输暂存仅保留到上传被接受，GPU 页范围保留到旧读者完成。这三种数据有不同消费者，不能相互取代。
- 功能与持久图采用显式借用：安装的 Pass 借用功能的管线及录制数据，组合宿主须先结束图使用再销毁功能。当前示例保持固定渲染尺寸，窗口 resize 仅更新呈现目标和缩放；没有隐含的全功能重建。Temporal 与阴影都拒绝活动图帧内销毁。动态卸载整组功能不是现有 API 所承诺的操作，不能通过提前销毁借用对象模拟卸载。

## 责任边界核查结论

本轮按入口、更新、取消、提交和结束追踪后，以下责任划分已落实，转入集中实现问题审查。这里的确认不表示每一实现细节已经正确，也不以编译结果替代后续功能验证。

| 架构 | 已核查的适用路径与结论 |
| --- | --- |
| 可选组合 | 无提取器、无实例存储、无默认纹理的核心组合成立；具体功能显式请求自己的依赖，宿主组合固定尺寸输出与呈现。通用 validation 不引用具体后端。 |
| 双 World | 成员变更归 EntityStore，候选接受保留版本检查；镜像和共享 transform 由提取上下文统一维护。结构事务、值事务和 hook 使用同一发布边界，失败不消费输入通知。MeshData、MaterialData、TextureData 固定本次内容，reload 仅影响后续提取。 |
| 实例 | 存储集中持有代次、行分配、内容备份和读者；prepare 获取时间线后才选择写代次。取消恢复数据，刷新失败保留重试义务，等待失败保留读者。删除无产品消费者的重复实例路径。 |
| 图声明 | GraphStorage 是声明来源，编辑候选共享不可变根，提交后编辑句柄失效。变更索引按明确身份传播，外部绑定和参数值不转化为声明重建。删除的身份不会重用为同一公开身份。 |
| 图执行 | FrameAnalysis 按受影响资源重推依赖与裁剪；FrameSchedule 修复受影响顺序与队列邻居，传播 frontier；FrameSubmissions 更新受影响提交依赖与波次。初次建立和全局调试配置变化是全局入口，普通局部变更不走全场景 diff。GraphStructureIndex 中没有消费者的第二套 hazard 模型已删除。 |
| 提交与回收 | 真实队列身份和已接受完成条件先于状态收尾保存。同步候选失败不覆盖有效缓存；release 与 acquire 分别留下真实边界。Graph、时间线和资源回收在等待失败时保留记录；未完成清理阻止继续使用并允许重试。 |
| shader 与材质 | cooked Slang 数据同时提供入口代码和布局；功能级接口解释顶点及材质，通用管线不读取顶点布局。实际 shader/管线组合决定分组。后端绑定物化仍消费同份 cooked 布局，其名称匹配和排序细节列入 M05 实现审查。 |
| Cluster 图范围 | 图持有声明，功能持有分组和上传内容；分组、标量页、bin 索引直接消费已知变化。资源登记和消费者同一图编辑接受，CPU 标量编码在接受前完成，取消保留上一版。 |
| 功能历史 | HiZ 和 Temporal 按视图实体身份；阴影、纹理与 UI 保存图的实际边界。功能内容仅依据相关 Pass 是否接受推进；取消、部分提交分别处理。安装的静态组合借用功能对象，活动帧内不得销毁。 |
| 流送 | 资产源、CPU BVH、传输暂存、GPU 页各有实际消费者和保留期限；同一驻留候选在未完全上传时重试，已接受上传不回滚，页退休依据实际范围读者。暂时读取错误的分类仍有 B36 实现缺陷，统一修复。 |
| 原生依赖 | D3D12 统一资源依赖包含 counter 和 feedback 辅助资源；descriptor 的固定依赖与提交时 sparse 代次分别保留。Vulkan sparse 替换在接受后切换，旧 heap 随队列完成释放；descriptor 释放回到池分配搜索。设备无法证明静止时保留对象。 |
| Validation | 身份、队列和命令观察随实际终止事件撤销；单个回调异常不跳过其他观察者。产品必需的生命周期状态由产品持有，validation 只观察且可关闭。 |

后续集中实现与验收已经完成：B01–B42、S01–S16 及仍成立的 M/G 项已逐项处理；渲染、图、后端和 Runtime 测试已清理内部访问与实现形状断言。Release 全解决方案与 Debug 全解决方案测试通过，测试清理后的相关套件已再次复验；D3D12/Vulkan 无窗口几何、D3D12 实际应用、独立设备移除与呈现路径均有执行证据。结果及不支持的能力详见最终交付记录。

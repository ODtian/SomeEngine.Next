# 完整存档与检查点预算

分析时间：2026-10-22 22:50 +08:00。状态：S-E/S-W/S-B 为源码内核；D 的版本、身份写入、引用映射和恢复完成规则为设计预算。

分析方法：对照 EasyEcs World.Serialization/World.Snapshots 与 SomeEngine WorldSerializer/WorldCheckpointCodec、类型注册和拓扑 codec；保存与读回按相同内容、相同 codec、相同版本规则分别核算，口径见 [[Cost-Model]]。

## 能力、格式与归属

| 用途 | 主要数据 | EasyEcs 等价参照 | D 核心入口 |
|---|---|---|---|
| 同 World 内存检查点 | 实体记录、主数组、缓冲元素、资源；普通托管字段按值/引用规则复制 | Capture/Restore，buffer 深复制、普通 managed 引用浅复制 | WorldCheckpoint.Capture / Restore |
| 同构建二进制检查点 | 版本/布局标识、实体身份、列与缓冲；非托管列连续写 | 相同布局条件的 Save/Load；附加能力采用适配 | WriteCheckpoint / ReadCheckpoint |
| 当前 schema 的长期存档 | 类型 ID/名称/schema、显式 codec、拓扑和引用 | 相同 codec/字节语义的 Save/Load 或 E* 严格类型适配 | WriteSave / ReadSave |
| 文件代数与原子提交 | 文件头、代数、完整性、提交路径 | 使用相同文件提交服务的 E* | 文件服务组合上述编码入口 |

Serialization 持有注册表、codec、类型绑定、格式与重映射。Core 提供各主数组；Relationships 提供主 endpoints/Parent 与顺序数据。反向邻接、places、Sparse 映射、值索引与查询缓存分别由自己的持有者重建。SourceGen 生成字段编解码和 Entity 字段处理。

序列化实例显式绑定 World、注册表及参与保存的 Hierarchy/关系存储；调用者提供这些对象一次。后续保存从已绑定存储取得主数据，类型级 codec 执行实际编码。核心 World 的依赖方向继续指向基础数据存储。

拟议 World 格式按“类型清单 → 实体身份 → Shared主值 → 表列 → Sparse类型 → 资源 → Hierarchy/关系”的分段顺序写。表列按类型描述一次；Sparse 每类型写 Entity[] 与值；拓扑只写主值及所选局部顺序。该组织为 World 容器版本5；字段 codec 的二进制语义保留。固定宽度 raw 列由长度推导字节数；durable 列使用列级长度 footer。运行时读取当前容器版本和当前 schema；既有版本转换遵循 ADR 0005 的独立离线工具规则。

序列化绑定把受控 endpoints 列交给关系段，普通 payload/附加组件交给表列段；每份主值输出一次。内存检查点直接复制 Core endpoints 列，关系额外复制部分只含自己的主策略/顺序数据；恢复从 endpoints 重建邻接与 places。两类复制范围各自固定。

同步保存占用显式读/结构范围，输入在编码期间保持可读。需要 World 继续变化时使用显式检查点复制，再对复制的数据编码；该能力承担完整复制成本。现有根分离/COW 保存属于另一种隔离方法，其首次写分离成本列在源路径内。

采用理由：列编码把类型查找移到每列一次；Sparse 分段直接使用已有紧密行；拓扑主数据由其实际持有者输出；显式复制的存储和调度成本在请求该隔离能力时发生。

## 候选：同一完整数据与观察时点

固定 N 个活实体、C 个保存列、V 个组件值、相同 schema/codec/引用规则，最终返回相同主值、身份、顺序及可用派生索引。每种候选都计保存、读取和恢复。

| D 编码组织候选 | 保存定位与分派 | 读取到最终数据 | 首次/增长与额外工作 |
|---|---|---|---|
| 按实体逐 item | 每值 R+V=〈4,3,4,0,0〉，codec 绑定〈2,1,0,0,0〉，合计 V·〈6,4,4,0,0〉、外层 I=V | 每值 SetCore/首次附加 M(k)；若批次先建最终表，可省迁移但仍逐 item 定位 | 组件列部分footer V次；Sparse逐item另S次；类型映射按实际item处理 |
| 按表列与侧存储类型 | 每列〈5,2,2,0,0〉、外层 I=C；类型/表绑定冷成本另加 | 最终列绑定一次，codec 直接填 N_t 项；元数据逐实体一次 | 组件列durable footer C次，全档footer J次；raw列footer0；绑定数组按新表建立 |
| 先构造完整对象图/字节区再输出 | 同样遍历所有主值，另写 V 个中间值/引用；若每实体一个容器，再 L N | 中间图读取后逐项填 World，或再 Restore 复制全部主数组 | 中间字节区 L1/Zero_byte(zcap)，输出再 Copy_byte(Z)；对象/数组各自增长 |

选定按列/类型编码，并直接写最终输出/最终列。Encode_T(N_t) 的实际字段处理在三项里都执行 N_t 次；生成的列入口把动态类型分派提到列外。用户 codec 以每值委托工作时，其 I 次数仍按 N_t 计入，闭合泛型 codec 按编译的实际调用形态计。

保存隔离单独选型，和输出容器分别组合：

| D 隔离候选 | 保存入口 | 与保存并发的普通写 | 读取旧值的复制量 |
|---|---|---|---|
| 同步读范围 | 已完成 Job 依赖 + 读范围检查 B1/进入退出 W2 | 写入在读范围结束后进行 | 主值复制0，实际编码照常 |
| 显式 Capture 后编码 | 完整检查点复制与派生读取对象构建 | 普通写沿基础存储路径 | 捕获全部实际主值 D_bytes；buffer 深复制 |
| 共享存储、首次写分离 | 共享根/数组标记各 W1，保留实际 backing | 每次可写入口共享判断 B1/状态读取；首次写时克隆所触及 backing | Σ被触及 backing 的容量字节；buffer 需要自己的隔离 |

选定同步读范围与显式 Capture 两个入口。后台隔离在 Capture 时支付 D_bytes 的读写、数组分配和 Buffer 深复制；COW 候选在低改动量时可能少复制，承担平时的可写检查、共享状态和各 backing 首次分离。检查点的普通 managed 引用复制规则沿同一能力配对。

## 源码依据

S-E：[World.Serialization.cs:62–89、186–285、352–429、475–602](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Serialization.cs#L62)、[World.Snapshots.cs:23–137](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/World.Snapshots.cs#L23)、[Archetype.cs:933–1020](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/Archetype.cs#L933)、[SavedTypes.cs:189–223](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/SavedTypes.cs#L189)、[DynamicBuffer.cs:247–285](https://github.com/JasonXuDeveloper/EasyEcs/blob/0eadbc43a4a5973286fa352a19711968b2852b87/src/EasyEcs.Core/DynamicBuffer.cs#L247)。

S-W：[WorldSerializer.cs:147–198、284–323、437–605](../../../../src/SomeEngine.ECS.Serialization/WorldSerializer.cs)、[AdmittedWorldWrite.cs:62–132、290–475](../../../../src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs)、[TopologyCodec.cs:61–159](../../../../src/SomeEngine.ECS.Serialization/TopologyCodec.cs)、[WorldCheckpointCodec.cs:36–125](../../../../src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs)、[DurableSaveStore.Write.cs](../../../../src/SomeEngine.ECS.Serialization/DurableSaveStore.Write.cs)。当前 schema 的能力边界来源于 [ADR 0005](../../../../docs/adr/0005-ecs-save-contracts.md)；本页持有拟议存储与计数方法。

## 输入参数与共同内核

I：使用过的实体 ID 数；F：空闲实体数；A：表数；N_t：表 t 活跃行数；K_t：保存列数；C=ΣK_t；V=ΣN_tK_t；T：不同保存类型数；S：Sparse 成员数；B：全部 buffer 元素字节数；R：资源数；Z：完整输出字节数。每列的启用位长度U_t=ceil(N_t/64)。当前逐item源码的组件项总数记作V_items=V+S；关系拓扑包装独立计。完整新容器的durable数据段数J=C+T_sparse+T_shared+R+T_topology，T_topology包含实际Hierarchy/关系类型段；每段footer一次，共4J B。表列部分的footer为4C B。

`copy(b)` 同时包含实际读写 b B；数组增长和清零按 [[Cost-Model]]。codec(T,n,b) 按具体实现拆为 n 次值处理、实际字段读写与 b B 输出；非托管 raw 列用 copy(n·sizeof(T))。任意用户 codec 的工作由其实际算法决定，账本保留调用次数、字段与字节参数。

D 输出使用段内 DataWriter：Output、可选外部键集合引用、当前 Span、Offset、CommittedBytes、字节上限、限额段原Span长度；Position 从CommittedBytes与Offset计算。x64值布局为64 B，其中限额状态占用相对原48 B布局增加的16 B栈空间。构造七字段 W7、L0；默认段切换提交当前Offset、取新Span，CommittedBytes/Offset/Span三写；末次Flush更新这三字段，另计W3。固定宽度写 Y(w)=〈1,1,1,2,0〉：取得当前字节基址 P1，容量判断 B1，写 w B 的元素 A1/W1，Offset W1。普通IBufferWriter的raw写 Yraw(b)=〈3,2b,3,b+1,0〉+Kcopy：输出类型与两字节基址、上限/Stream路径/段余量三判断、Copy_byte(b)、Offset W1。跨段时按实际分成的片数逐项相加；Stream直接写的分支和调用按实际后端另计。

连续内存输出由调用方接收器选择容量策略。ArrayBufferWriter沿实际GetSpan提示与倍增容量入账；紧凑配置复用同一IBufferWriter入口，把本次编码写入max(64 KiB,sizeHint)的段，完成点内合并为精确Z长度数组，再结束临时段引用。五库沿同一接收配置、同一连续WrittenSpan/Load(span)能力比较，编码器每值调用一次。原ArrayBufferWriter配置继续保存独立账本。

紧凑接收器的冷分配为段数据容量S、最终Z、目录各次容量及数组/对象头，另有Copy_byte(Z)合并；合并期间同时持有S与Z。每次Clear清零上次结果并把它作为首段复用；结果长度相同时复用该数组，并跳过已经原位的首段，实际合并只复制其余已写字节。报告记录实际段数、S、数组分配、目录增长、合并复制和最终容量，完成后的持有为Z与目录容量。Stream与文件输出沿原缓冲和提交链处理。

g 个实际输出段：GetSpan g 次、Advance g 次，I=2g；默认writer自身控制〈2g−1,0,2(g−1),3g+7,0〉（含构造W7、g−1次切换的3(g−1)写和末次Flush的W3，切换各有上限与限额Span状态两判断）；实际输出后端每次GetSpan/Advance的集合操作另外加入。IBufferWriter使用相同实现与容量时这部分直接配对。ArrayBufferWriter的容量判断、Array.Resize与清零分别依据S-B，ArrayBufferWriter的最终数组c→c'加L1+Zero_byte(c')+Copy_byte(c)+引用W1。每个启用限额的拓扑段进入/结束共W6，取新Span时按实际次数更新原长度并裁剪到剩余额度，结束恢复同一缓冲余量；这些状态与计算均L0。

S-B [.NET 10 ArrayBufferWriter.GetSpan/Advance](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/System/Buffers/ArrayBufferWriter.cs) 容量充分、byte类型、正sizeHint时：GetSpan的_buffer两读P2、AsSpan起点A1、sizeHint三判断和AsSpan两判断B5；Advance为P1/B2/W1。因此g段后端合计〈3g,g,7g,g,0〉，加默认writer自身控制后为 **〈5g−1,g,9g−2,4g+7,0〉，I=2g**，再加实际Y/Yraw/codec。增长采用Array.Resize，复制旧容量c字节；首次至少256字节。复用输出时ResetWrittenCount W1，字节内容沿有效长度读取。

DataReader 对固定宽度已绑定输入写法为〈1,1,1,1,0〉：基址、长度、读取 w B、Offset W1；raw 读 Copy_byte(b)+Offset W1。输入跨段按实际段数加入取段/提交。codec 绑定、字段读写、字符串/用户对象分配属于实际 Encode_T/Decode_T；外层分派每列一次。

## 内存检查点：完整路径

| 阶段 | S-E | D |
|---|---|---|
| 入口 | 结构门 → 迭代检查 → FillDirtyTables | 结构/读范围 → 已声明的拓扑维护 → 复制 |
| 实体记录 | copy(16I)，另 copy(4F) 空闲 ID 数组 | 保留16 B记录槽；Chunk为空引用，Row/Generation有效字段读写各8I B；死亡Row保存空闲链，另存free head/tail |
| 每表实体行 | copy(8N_t) | copy(8N_t) |
| 普通非托管列 | 每列一个实际值复制调用 | 每列一个实际值复制调用 |
| 普通托管列 | typed CopyRows，n 项引用/值复制与屏障 | 相同语义 typed copy，n 项引用/值复制与屏障 |
| Buffer 列 | 每个非空 buffer 经 CopyRows→Clone 新建 Store/Items，L2，并复制元素 | 16 B行内头；独立元素数组首复制L1，容量复用L0，实际元素复制沿[[Containers]] |
| 已建立启用位 | 每列 copy(8U_t)，另 Absent[] 复制 | 每列 copy(8U_t)，disabled count 同步复制 |
| 追踪 | tick companion 列随普通列复制；Restore 默认重新 stamp | 按恢复模式保存所需 row ticks，或在“全部视为当前写入”模式重建；两侧同模式比较 |
| Sparse | E* 侧存储逐类型复制稠密实体与值 | copy(8S+Σsizeof(T)·S_T)，恢复时生成映射/附着链 |
| 资源 | 扫 singleton 目录，复制兼容 box 或 Clone | 扫资源目录，复制兼容 box 或 Clone |
| 关系/层级 | ChildOf 列与关系缓冲已在上述复制；索引恢复后 Reset | edge 主列/Parent/局部顺序复制，反向索引在恢复时重建 |

S-E 首次每表 Rows 对象与 Columns/Present/Absent 三个目录 L4，再加实体数组 L1、每值数组各 L1、实际启用位数组各 L1。D匹配实现使用一个块复制容器、每列结构目录和存在位缓存目录，L3，再加相同实体/值/位数组；启用位缓存目录按实际首次出现另计。缓冲/资源逐个实际拥有的容器计分配。D的容量复用目标为：检查点继续持有独立数组，容量充足时复用，增长时承担真实分配、复制和旧值释放；该目标的实现与实测单独核对。

D的保存记录数组由检查点独立持有，元素步长为16 B，有效ID为1..I，编号0保持全零。新建数组将Chunk字段初始化为空，后续Capture只逐项读写Row/Generation两个int，Chunk字段持续为空。源Chunk引用及其Archetype/World链由源World持有；检查点的独立值与身份由保存记录及复制块持有。数组容量c的payload为16c B，首次至少覆盖I+1项，头与对齐另计。每次Capture的字段流量为读取8I B、写入8I B；初建/增长的清零按新容量16c B计入。增长选用新建清零数组后直接填本次有效字段，旧记录复制量为0；被替换数组按原引用寿命回收。free head/tail共8 B元数据，tail沿已保存Row链扫描F项取得，另计F次字段读取及循环控制。

### 隔离原型的持有者与预算适用性

隔离原型的紧凑基线c391670的WorldCheckpoint采用Generations与FreeIds保存身份，ChunkData保存各块的有效Entity行。令I=World.Used、F=实际空闲ID数，身份及行数据payload为4(I+1)+4F+8ΣN_t B，数组头和对齐另计；其中4(I+1)包含编号0位置。原型RestoreEntityIds按这两份身份数组重建目标World的16 B记录及原空闲复用顺序。上表D采用16 B槽、仅复制Row/Generation的记录数组及嵌入式空闲链，源码计数和机器记录按各自实际表示归属。

原型每次Capture先ReleaseValues，再建立Generations、free/chunks/services临时List及其最终数组、ChunkData目录、实体行、列/存在位/启用位/版本数组和所需Shared/Sparse/服务/系统进度副本。CaptureAccesses的bool目录、List与最终访问数组同样计入本次捕获；数量按实际Chunk及实际能力计算，包含空块。检查点对象复用与这些独立数组的逐次分配分别计费。Buffer元素在池命中且容量足时复用；新的Chunks目录还带来序列化检查点绑定的重建成本。

原型的_values是拥有值复制及Buffer池的World上下文，首次默认分配17项EntityRecord，即272 B payload及数组头，World对象、初始目录和实际池另计。ReleaseValues保留该上下文，Checkpoint.Dispose结束其World及拥有值寿命。原型Generations、FreeIds、SystemVersions和Events字段的数组引用继续随检查点对象持有至替换或对象回收；复用、Dispose和对象回收三个观察点分别记录。保存过该检查点的序列化实例还通过_snapshotChunks和SnapshotTable持有ChunkData及其列/实体数组，_snapshotServices持有已绑定服务数据，寿命至对应检查点绑定替换或实例回收；持有字节按共享对象只计一次，Checkpoint.Dispose与序列化实例缓存分别列示。上述原型实测对应该表示与分配策略，D容量复用及16I目标布局的达标证据由匹配实现取得。

D容量复用采用目录引用与CaptureNumber共同识别捕获内容。Chunks目录由检查点独立持有；检查点保存有效ChunkCount，各复制块保存有效Count，数组Length表达容量。Capture、Save和Dispose遵守同一检查点的串行寿命，后台编码结束后再复用该对象。每次成功Capture完成全部身份、主值及进度复制后，将CaptureNumber加一；该编号独立于源World.Version。

序列化器保留上次目录引用及捕获编号。Save先沿本页既定注册表版本规则核对绑定，再比较目录引用与CaptureNumber；任一变化均按有效ChunkCount扫描本次表/类型并更新SnapshotTable与列绑定。重绑先将旧缓存目录引用置空，完整建立绑定后再保存新编号及目录引用。同次Capture的重复Save继续复用绑定元数据。编码始终按各块有效Count及当前列基址处理；增长替换数组时，同一描述指向本次实际数组。

编号字段payload为检查点8 B、序列化器8 B，共16 B；有效数量另计检查点ChunkCount的4 B与每复制块Count的4 B，对象对齐沿实际布局计。Capture编号递增承担一次读取、加法和一次8 B写；Save增加两次编号读取与一次比较，重绑发布编号另一次8 B写。新Capture后的首次Save承担真实重绑扫描、失效尾引用清理及实际触发的对象/数组建立或增长，容量足的目录继续复用；重复Save同次Capture沿命中路径计费。该字段、维护和绑定成本共同进入D容量复用预算，其实现及达标证据按匹配布局取得。

### D匹配实现的容量与释放

隔离实现405969c采用上面的16 B保存记录和CaptureNumber绑定。Chunks、Shared、Sparse、Services、事件与系统版本数组沿有效数量读写；容量足时复用，增长时记录真实分配。ChunkData保留实体/typed列、存在位、启用位与变化摘要数组。Capture访问声明的bool目录及List随检查点复用。Restore继续完成目标World的索引、分组、层级/关系与消费者进度。

Hierarchy/关系SavedData分别保存5/6个int有效数量，payload为20/24 B；具名销毁服务Count为4 B，WorldEvents两个count增加8 B。Future目录槽宽仍为Hierarchy16 B、关系24 B；每个保留成员组增加一个32 B List对象，backing为24+8c B，c为实际成员容量。retired Children成员复制8m B，邻接成员复制16m B；容量充足复用，增长时归还旧自有成员数组并从源填入新数组。Rebuild在同一SavedData复用时将Deferred/Version/ReaderPosition写为false/0/0。

ChunkData额外保留存在位缓存目录8K B及数组头；首次遇到启用位再保留一个8K B缓存目录。Count和缓存字段新增payload21 B，x64块描述对象由80 B变为104 B；检查点的访问声明目录、系统count目录与序列化器的表容量一起进入实际持有量。前述20+4C B只覆盖捕获编号与块数量字段；完整元数据按各持有者另计。ResourceEntries复用既有typed盒与8c B尾目录，结构值拷贝为264 B，资源真实Clone及释放继续计费。

Capture的Clear(true)保留服务自有数组高水位，成员有效数量独立于容量；已清空的Future List和暂时处于有效域外的自有成员数组仍计持有。服务实例被替换或目录截尾时，先结束旧实例；Dispose的Clear(false)遍历全部保留容量归还自有成员数组，再结束引用。二进制读取采用新建数据，transfer把已移交数组及其有效数量一并清空，目标成为后续释放者。ReadMembers直接按已读count填入List，读回完成时沿原恢复链建立目标独立成员集合。

块布局替换或目录截尾时，先对退出块执行Release(clearArrays:true)分离backing；兼容块继续复用。Checkpoint.Dispose释放拥有值并分离当前ChunkData的实体/列/掩码/变化数组、服务数组与保存记录；仍持有旧绑定的序列化器保留已清理的描述对象和自己的绑定容量。检查点释放后序列化器继续存活，以及全部对象释放后，分别读取整进程持有量。普通托管引用、Buffer元素、资源Clone、池保留与绑定缓存沿各自实际寿命入账。

### 关系头目录增长的单次填入

030ac09在增长时先分配最终AdjacencyHead[n]，前c项直接使用本次源字段及旧成员数组引用，新增域沿原CopyHead创建成员数组；容量足时继续原复用循环。每个增长目录省去Array.Resize对旧16c B头的读取和写入，新目录分配仍为24+16n B，成员独立复制/池归还及最终容量保持原预算。previous持有旧目录到首段最后使用，旧/新目录的重叠寿命进入真实增长峰值。

N=100000的固定混合输入在增长前Used=200003，双向目录各c=200004，合计省6400128 B旧头读取及6400128 B写入。405969c→030ac09的同主机/运行时/掩码定点记录为：Preserve增长Capture34.789→28.012ms、分配36650928 B保持；Rebuild27.313→25.232ms、分配24072184 B保持。初始和增长后输出长度逐项相同，Restore/Load公开结果通过。两个增长动作各观察一次，字节收益以已审源码及实际长度为依据，机器耗时保留原观察口径；含后续编码和验证的进程峰值分别为436101120/380637184 B。记录见task-d-growth-once-review.json，四组405969c复用测量沿下一节单列。

### 匹配实现的最小实测

固定紧凑内核c391670、公共同实例测量代码095c718及D实现405969c。全部配对在BOQI-THE-DAEMON、.NET10.0.14、mask255、计时CPU0完成，公共CPU队列采用自动放置，回执记录RemoteUnavailable回退，配额4物理核/1GiB。负载为N个普通实体、N个关系实体、两个根和一次销毁；增长增加1000个普通实体及1000条关系。构造、首次Capture、重复Capture、Grow、增长后的Capture、Save、Restore与释放分别观察。重复Capture每组10个约100ms样本，使用同一检查点及同一系统数组；资源读数来自独立于计时窗的整进程GC与进程计数。

| 输入与进度 | 重复Capture ms：紧凑→D | 每次分配B：紧凑→D | 检查点热持有增量B：紧凑→D | 首次Capture ms：紧凑→D | 增长Capture ms：紧凑→D |
|---|---:|---:|---:|---:|---:|
| 1万 Preserve | 2.679→1.404 | 4389970→1104 | 5921064→6171880 | 69.442→67.603 | 2.554→3.420 |
| 1万 Rebuild | 2.165→1.219 | 3349572→1104 | 4624344→4875432 | 91.141→113.518 | 2.971→3.597 |
| 10万 Preserve | 30.776→20.822 | 42947861→1104 | 56986208→59421600 | 111.802→132.351 | 23.164→34.789 |
| 10万 Rebuild | 28.748→18.988 | 32546375→1104 | 47261224→49696888 | 145.608→179.009 | 23.668→27.313 |

本行热分配上界可直接由用户资源Clone代入：FullArchiveValues的SaveResource包含一个long[64]初始化数组，Clone新建对象后再以Items.Clone替换它。x64对象为32 B，两份数组各24+64×8=536 B，因此每次Capture的资源Clone预算为32+2×536=1104 B；第一份初始化数组为本次临时分配，完成后资源持有32+536=568 B。四组热分配实际均为1104 B，预算余量为0，其余目录/主列/池的容量命中沿复用预算执行。后台首次请求仍按前述独立冷绑定账处理。

热持有增量按HotCaptures减EmptyCheckpoint计算，包含复制值World、池与访问声明/服务缓存；额外持有约0.251MB/2.436MB，主要来自16 B身份槽及复用元数据。首次分配每组增加约0.248MB/2.458MB；增长Capture分配分别为紧凑5.032/4.252/44.049/33.054MB，D为4.221/3.297/36.651/24.072MB。首次与增长各观察一次，包含对应首次JIT及实际重建工作；这些实测值与热样本分别呈现。

10万Preserve重复捕获阶段进程峰值为420270080→230703104 B，含后续编码与功能检查的全过程峰值为474042368→432087040 B；10万Rebuild全过程峰值为431247360→377823232 B。全部对象释放后的进程托管残留增量约1.84/2.11/13.95/13.95MB，两版同组差值在384 B以内，归属进程共享池和类型缓存。保存过检查点的序列化器仍存活时另有独立观察点，完整记录位于task-d-checkpoint-comparison.json。

增长后I=202002，D身份数组实际容量202003、payload3232048 B，紧凑身份payload808012 B；实体行数组分别为1616024/1616016 B，typed列payload均为12197500 B。D每次身份字段读写各1616016 B，紧凑各808008 B，按源码有效数量计算；增长清零、数组头/对齐和其余实际复制分别计入所属阶段。该字节账与整进程内存读数各自保留口径。

四组的初始及增长后输出长度逐项相同，精确输出容量等于有效长度。完整主值、拥有的Buffer与旧值、Sparse/Shared/索引、资源、延迟层级/关系顺序、销毁事实和系统版本在内存Restore与编码Load后均通过公开结果核对；最终405969c的check-checkpoint及具名消费者检查通过。原生文件完整性、双代数和原子提交沿既有服务执行。正式矩阵的Capture动作继续每次新建检查点，本节单独记录显式同实例复用；21页/19能力组/880行的统一来源验收继续沿Acceptance执行。

Restore实体身份：S-E对max(当前I,保存I)逐项更新16 B元数据，再写新空闲ID、复制保存的F个空闲ID。D先沿既有ClearValues完成拥有值释放、DataGeneration推进及当前记录代数更新，清理阶段按当前Used域U单列16U B记录写。预留max(U,I)的目标记录容量后，对保存ID 1..I读取Row/Generation，并与目标当前Generation取max，写入Chunk为空的目标16 B记录。捕获后新增编号按I+1..max(U,I)升序接成目标空闲链，再接到保存free tail之后；保存链为空时采用新增链，free head等批次字段更新一次。新增ID数记A_extra=max(U-I,0)，其目标链计4A_extra B的Row写，尾连接另计实际一次定位与4 B写。

D按每个独立复制块的有效Entity行恢复位置：为Entity.Id写入目标Chunk、实际row及保存的Entity.Generation，共16N B记录写，非空Chunk引用按实际屏障计。活跃行的最终位置由本次目标块确定；保存记录始终只读，其Chunk字段持续为空。保存记录初始化的16I B写、当前World清理、新增链连接、活行重绑以及值/派生恢复各自计费。EntityCount、Used及观察版本沿所选恢复模式收尾。

每表Restore：Reserve → 释放被替换的实际容器和超出部分托管值 → 实体行复制 → 每列值复制 → 启用位复制/填充 → Count更新 → 已开启tracking重建。查询Match持有Archetype，范围开始读取当前块目录。列绑定保存类型与codec，每块重新取得值基址；多块保存和恢复逐块处理实际行。值索引/反向关系工作计入派生维护账。

## 二进制与长期保存：完整路径

| 阶段 | S-E 源路径 | D 实现方法与成本 |
|---|---|---|
| 选择数据 | 扫表，选择非空表；记录每表 count | 同样扫描 A 个 Count；B(A+1) 与 A 个非空判断；复用表 ID 数组写 A_live 项 |
| 类型清单 | 两次扫表的组件，初始化到 maxBit 的 _saveTypes，逐列去重；每实际类型写类型记录 | 每序列化实例持有 TypeId→codec 与表绑定；新表逐 K_t 项绑定，已有绑定直接读取。每次按已选表汇总实际类型，文件目录与表列按格式 GUID 排列，目录编号在该次写入头部时绑定 |
| 身份 | 写 I 个版本、F 个 free ID、8N B 表实体 ID | 二进制使用同一形状：4I+4F+8N B；内存记录里的空闲链输出 F 个 ID，按同一复用顺序 |
| 列描述 | 每表组件 ID 与清单映射 | 每表写 K_t 个文件类型 ID，数组索引 K_t 次；列绑定/codec 每列一次 |
| 非托管 raw 列 | WriteColumn → bits → info → WriteRows | 绑定 codec → ColumnEntry → bits → raw copy；见下一表 |
| durable 值 | 同一 Nino/自定义 codec 的 n 次值操作 | 选定同一字段 wire 的 n 次值操作；生成批量入口可将类型分派提至列外 |
| Buffer | 写 Count 与实际元素，raw 或 codec | 相同 Count 与元素；行外绑定一次元素 codec；按 B 字节及实际元素数入账 |
| Sparse | E* 按类型的 Entity[] 与值序列 | 扫各类型紧密数组 S 项；写 8S B 身份及对应值；维护缓存的写入为 0 |
| 资源 | singleton 类型 ID/切片/value | 注册资源类型 ID/切片/value；R 次实际 codec |
| 拓扑 | 原生 Parent/关系值缓冲，完整 edge 能力使用 E* | Parent、edge endpoints、payload 所选列与局部顺序各写一次；places/反向邻接的输出为 0 |
| 外部引用 | 使用同一引用编码/收集适配能力 | r个外部字段均写16 B Guid并判断空键；每非空键读取集合引用P1并判断是否提供集合B1；提供集合时调用Add一次，重复键比较与容量费用沿实际集合计；GUID字节16r |
| 完成 | Flush；通用输出可能复制整个 Nino 中间缓冲 | 写末段并完成输出；IBufferWriter 路径直接写；Stream 适配器按实际缓冲/Flush 入账 |

S-E 输出为原生 NinoArrayBufferWriter 时直接写；其它 IBufferWriter 先写到内部 Nino 缓冲，再 copy(Z) 到目标。D 的原生 IBufferWriter 路径预算为直接输出，适配任意 Stream 的临时输出与调用成本在 Stream 入口承担。两侧按相同接口及相同 codec 配对。

## 每列与每项的常数

| 操作范围 | S-E / S-W | D |
|---|---|---|
| raw 列，表已取得、无启用位 | S-E WriteColumn 〈6,3,2,0,0〉 + 实际 WriteRows | 绑定 codec 〈2,1,0,0,0〉 + Chunk 列项/Rows/Enabled 〈3,1,2,0,0〉，合计〈5,2,2,0,0〉 + 相同 raw 写 |
| 有启用位的列 | 上述 + 一次 raw 位数组输出，8U_t B | 上述 + 一次 raw 位数组输出，8U_t B |
| 同一 item wire 的计字节与 footer | S-W 计数 Stream + BinaryWriter，**每 item L2**；外层 Flush、payload Flush、Dispose Flush，共 3 次调用 | 兼容格式适配：Position 差值与 Y(4)，每 item L0；新容器按列 footer，C 次 |
| 逐 item 读与 footer | S-W 计数 ReadStream + BinaryReader，每 item L2；读后校验 footer | DataReader 位置差值；同 item 格式逐项读4 B；新格式每列读4 B，实际 footer 检查 B1/列 |
| 拓扑类型段 | S-W 每段计数流/Writer，另预算对象与捕获 lambda | D 同一 DataWriter/DataReader 处理段与累计预算；每段一个 footer |

S-W WriteItemAdmitted 在 WorldSerializer.cs:534–545。三次 Flush 中第三次依据 [.NET 10 BinaryWriter.Dispose(bool)](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/BinaryWriter.cs)：leaveOpen=true 的 Dispose 调用底层 Flush。这里是调用次数，底层流实际系统调用按所用流类型计。

V_items个组件项的逐项包装分配为S-W L(2V_items)，拓扑包装另计；D 分配移到输出适配器/必要容量。原生 IBufferWriter 的 DataWriter 为值，热运行 L0；Stream 若需要计数流与 writer，对一次存档分配 L2。每个 payload 的实际编码字节与字节写入工作仍计入 codec。同item格式的4V_items B footer 与新 durable 容器的4C B footer 分别列入 Z；raw 列按固定宽度写入。

## 当前整理路径与新绑定缓存

S-W WorldWritePlan.Build 每次建立：componentId 字典、SparseSerializationPresence 及其字典/集合、manifest 集合、archetype 字典；每个有数据表另建 List 和 ToArray，按类型排序；每个有 Sparse 的实体另建 List，逐成员填字典，最后再排序。WriteWorldEntities 每实体查表字典和 Sparse 字典，每个 item 查 Guid→manifestIndex 字典，再创建上述逐项包装。

D 序列化实例的 TypeId→codec 是数组；每表 ID→绑定列数组也是数组。首次绑定 K_t 列：读取类型 ID A K_t、类型 codec 目录 A K_t、输出绑定 A K_t/W K_t，循环 B(K_t+1)，各类型的存在/保存策略判断按 K_t 次。分配实际绑定列数组 L1；表绑定目录容量充足时发布 A1/W1。绑定包含 type ID、存储种类、codec引用，16 B/项。

绑定缓存生命周期由注册表的版本和表身份决定。注册变化显式更新版本；后续首次使用重建受影响绑定。新表建一次；Chunk 容量增长仅更新 Chunk 的数组，已有绑定读取同一 Chunk。Sparse 分段直接读取类型 Store；每次 Save 的 Sparse membership 字典/List 分配为 0。类型清单按实际使用数组/位图汇总，位图词数与输出 T 项读写全部计入；类型目录扩容使用共同增长内核。

## 类型、表和输出选择的完整 D 内核

a 为实际非空保存表数，C 为其列数，T 为实际出现的保存类型。序列化实例持有 SelectedTables、SeenTypes 位图、UsedTypes、FileTypeByTypeId 与表绑定目录。SeenTypes 只用于当前类型清单；UsedTypes 同时用于清单输出和清除实际置过的位。

| D 步骤 | 〈P,A,B,W,L〉 | 全部处理项 |
|---|---|---|
| 选择非空表，已预留至 A | 〈2+A,A+a,2A+2,a+1,0〉 | AllTables/Selected 两基址；A 个表 ref；a 个输出 ref；循环 A+1、非空 A、容量1 |
| 每次 Save 汇总类型，基址已绑定 | 〈0,C+2T,C,3T,0〉 | 每列 Seen word A1/B1；新类型置位、写 UsedTypes、写文件序号各 W1；循环控制按 C+a 次另加 |
| 上项三个目录绑定/有效数发布 | 〈3,0,0,1,0〉 | SeenTypes、UsedTypes、FileTypeByTypeId；UsedCount W1 |
| 清实际 T 个类型位，沿用基址 | 〈0,2T,T+1,T,0〉 | 读 UsedTypes、取得 word、清对应位；复用目录 |
| 第一次表绑定 k 列 | 〈4+k,3k+1,3k+3,k+1,1〉 | 本表k列全部保存：热表目录检查〈2,1,2,0,0〉，类型ID/codec目录P2，k个codec引用Pk；逐ID/codec/输出A3k；循环k+1、范围/存在2k；发布ref写W1 |
| 热表绑定 | 〈2,1,2,0,0〉 | 表 ID 目录、已建绑定；范围/空值 |
| 写 I 个实体代数 | 〈1,I,I+1,0,0〉 + I·Y(4) | 逐记录取 Generation；主记录数组只绑定一次 |
| 写 F 个空闲 ID | 〈0,F,F+1,0,0〉 + F·Y(4) | 从死亡记录 Row 所表达的链读取 Next；沿用记录基址 |
| 写表行身份，共 N 个 | 每表身份数组 P1 + Copy/write(8N_t) | 原始 Entity 身份8 B，Encode Entity 内部逐字段方式按所选 codec |

首次表绑定的 P 含k个codec对象；实际处理入口在编码列绑定时读取；冷注册字典每类型一次，字典 Hash/Eq 与 schema 检查按 Cost-Model 实际参数加。表绑定缓存由注册表版本与表 ID 管理：显式修改注册时标记受影响项，下一次使用建立；Chunk 扩容更新 Chunk 数组，绑定仍读取同一 Chunk。类型汇总数组容量按实际 int ID 上限扩展，处理次数按 C/T 而非整个 ID 空间。

完整输出式为：读范围/Job 完成 + 表选择 + 新表绑定 + 类型汇总 + 类型清单字段写 + 身份字段写 + Σ表头与列头 + Σ列〈5,2,2,0,0〉+EncodeColumn_T(N_t) + 实际启用位/Buffer/Shared/Sparse/资源/顺序/外部引用 + footer + 类型清位 + writer 控制 + 输出后端增长/完成。普通列5P/2A/2B、Sparse/Shared的codec+值数组绑定3P/1A、资源codec+box定位分别相加；各段实际Encode_T与J个footer逐项计。每个符号由本文各表给出实际输入、字节或向量。

Source 中逐 item 的额外部分可单独核对：S-W WriteWorldEntities 每实体 TableItems/SparseItems 两次字典取值，共2N次；每item manifest字典一次，共V_items次；逐item包装L(2V_items)、Flush(3V_items)。D 将类型/表绑定移到每新类型/新表，逐列热定位为5P/2A/2B。每项 payload 的实际编码继续保留。

## 完整读取与实体重映射

1. 读取头和 T 个类型键；每个真实外部类型键查注册字典一次，检查 schema/存储种类一次；结果缓存到 `FileTypes[T]`。后续每列使用文件类型 ID 的数组索引。S-E SavedTypes.FindComponent 每类型持一次 Gate、一次字典查询、一次候选数判断和一次 Bit 入口分派；本地注册和解析属于首次/冷路径。
2. 读 I 个版本/F 个空闲身份；保留身份模式填实体记录。重映射模式预建完整 `RemapEntry[I+1]`，每项为 SourceGeneration int 与 Target Entity，共12 B。先按源ID顺序为N个活槽分配目标编号，再按文件free链顺序为F个空槽分配目标编号，目标保留对应源槽的当前Generation；全部I个槽的目标编号在解码前到位。SourceGeneration以正值表达活槽、负值表达free槽。既有Generation输入数组与free数组原位转为目标次序，交给RestoreEntityIds填最终身份元数据，随后逐payload直接填值。新增Map为L1、清零12(I+1) B，源代数I项、free标记F项、Target身份I项的写入分别计；输入数组沿原4(I+1)+4F B预算。普通Entity字段执行当前代数匹配，进度旧代沿下文完整规则处理。
3. 按表读取类型 ID；首次目标表执行位图匹配/建表/查询维护，后续文件表使用缓存。Shared列编号沿[[Shared]]的读回映射转换；GroupBy表键从修正后的实际值GetGroup()重算，复用既有分组迁移内核形成最终行分布。按实际追加的Chunk/行范围Reserve，读取实体行与列值，沿用已读Generation，逐行补齐Chunk/Row，W(2N_t)、写12N_t B。分组重排保留版本、存在位和启用位。S-E LoadTable使用Array.LastIndexOf找表ID、临时Rows，再在Restore复制到World；D绑定目标持有者的列直接解码，实际追加与分组迁移造成的增长/复制沿原规则计入。
4. 读取 enable 位与计数。S-E LoadColumn 即使是完整表，也逐行测试输入位，N_t 次位读/判断与实际置位。D 对完整对齐列 copy(8U_t)，按词 PopCount U_t 次计算缺失数；部分行拼接按实际头尾词做移位合并，中间整词复制。首次位数组 L1 与新容量清零计入。
5. Buffer 先读长度，Reserve 一次，再 raw/codec 填元素；S 项 Sparse 主值填紧密数组；资源 box 复用或首建。读取完整关系/层级主数据，保留 edge 身份、端点角色与局部顺序。
6. 引用字段逐个完成本地 Entity 重映射；外部键 r 次调用 resolver，集合字典/用户解析器成本按实际实现入账。按当前 schema 解码的类型使用已绑定 codec。
7. 执行下一表的派生重建，完成后返回可使用 World。新 World 分配属于 ReadWorld；恢复到既有 World 的内存检查点另按前表计算。

## 读取候选与最终数组写入

| D 候选 | 逐普通值 Decode | 身份/类型定位 | 完成到可用 World |
|---|---|---|---|
| 逐 item 即时 Set | Decode_T 一次 + SetCore；首次组件还 M(k) | 每 item 文件类型 A1、目标 R、列 V | 实际迁移、索引、拓扑全部支付 |
| 先读中间 Rows 后 Restore | Decode_T 一次，中间值数组各 L1；再 Copy_T(n) 到最终列 | 文件表一次定位，Restore 再定位 | 两套值数组容量与完整 Restore |
| 绑定并直接填最终列 | Decode_T 一次，最终列各 L1 或复用 | 每文件类型一次 Hhit；每列文件类型 A1、codec/最终列绑定一次 | 元数据 WN、启用位计数与派生重建一次 |

选定第三项。新World在文件头给定容量后建立目标目录，读文件表先绑定目标Archetype/Chunk并Reserve，按本次行段解码到其持有的值数组。文件表按既有顺序流入，同一目标表或全局POD缓冲的后续追加继续按实际需要增长，复制旧有效值、容量清零及地址重绑逐项计费；每份payload保持一次解码。完整入口返回一个已完成读取和派生建立的World；当前schema的字段codec与外部引用策略显式绑定。

| D 读取步骤 | 完整访问/写入 | 首次与增长 |
|---|---|---|
| T 个外部类型键 | 每类型 Dictionary P1+Hhit、schema/kind 两判断、FileTypes A1/W1；循环 B(T+1) | FileTypes[T] L1+Zero_ref(T)；真实新注册另付注册成本 |
| I 个身份代数 | 记录数组 P1；每项 A1、Generation写W1，共4I B；循环 B(I+1) | 最终 EntityRecord[I] L1/清零16I B；F个空闲ID再写Row链F次 |
| 映射模式 I 个目标槽 | Map绑定P1；源Generation写I项、free标记F项、Target Entity写I项；原位排列Generation及free并填最终元数据 | RemapEntry[I+1] L1、清零12(I+1) B；输入4(I+1)+4F B沿原读取预算 |
| 每列最终目标绑定 | FileTypes/codec 〈2,1,0,0,0〉 + Chunk 列项/Rows/Enabled〈3,1,2,0,0〉 | 列目录/最终值数组各实际 L1，已预留时 L0 |
| raw 列 n 项 | Copy_T(n)，更新 reader Offset W1 | 同一最终数组直接填 |
| codec 列 n 项 | Decode_T(n)，闭合类型入口 I1/列；用户逐值委托实际 I n | Decode_T 内部实际托管对象 L |
| 启用位 u 词 | Copy_ulong(u) + PopCount u 次 + disabledCount W1；循环 B(u+1) | 位数组 L1+Zero_ulong(capacityWords) |
| 每个缓冲 | 长度读取、store 空值/容量两判断、Count W1、实际元素 raw/codec；已有引用先清实际引用元素 | 新 store/Items 最多 L2，容量足 L0；增长 Copy_T(oldUsed) |
| Entity 字段重映射 r 个 | 已绑定 Map；每字段范围/代数 B2、Map A1、字段 W1；空值按实际短路 | 全 World Map 已在身份阶段建立 |
| 外部引用 x 个 | resolver 调用 x 次；实际键解析/分配归 resolver | 按同一个 resolver 与 key 集合比较 |

保留身份模式把文件中的 Entity 直接用于最终行，Map 分配0；重映射模式把I个槽的映射、输入数组排列与最终元数据写入完整追加。列读取末尾按 n 个表行回填 EntityRecord 的 Chunk/Row，A n/W(2n)，12 B/记录，循环 B(n+1)；Generation沿用身份段已经写入的值。

## 恢复后的维护账

| 数据 | S-E / E* | D |
|---|---|---|
| 值索引 | Reset 后首次 Find 扫 n 行并逐项 Put | 同样按请求的恢复时机逐 n 项建索引；冷查找计入首次使用 |
| 关系反向索引 | Reset，首次Find扫全部缓冲链接L条并Add；完整edge使用E* | 完整读取后按L条主edge建邻接/Applied及声明的当前占用索引 |
| Hierarchy children | 首次 ChildOf 索引 Build | 按 v 个 Parent 两遍：首遍计各父大小，次遍写子项和 Place；大小项读写各 v 次、子项写 v 次 |
| Sparse 映射/附着链 | E* 逐 S 成员建同一侧存储索引 | S 次 cell/附着写，按 [Sparse 新增内核](Sparse.md) 扣除已完成的值写与外层验证 |
| Enableable | E*读取保留值与禁用标签；原生存在位内核另列 | 保存全部已存T及启用位，复制位数组并PopCount，写禁用计数一次 |
| Tracking | StampTracked 或 ClearTracked，按所选恢复模式；Added/保留旧值使用E* | Rows按所声明追踪能力填v_T·N B，v_T为8或16；word/block按实际词/块重建；Removed事实随主值读取；两侧同模式 |
| 查询 | 新表追加匹配；旧表内容恢复 | 查询按CheckedArchetypes增量刷新，Match持有Archetype；范围重取实际块，首次绑定扫描实际表 |
| 销毁事实 | 同步清理后的状态 | 显式选择：保存前 Maintain 完成其实际链更新；或检查点同时保存日志和消费者进度，再恢复同一未消费状态 |

将维护移到首次 Find 的方案，总账为“读取 + 首次 Find”；选择读取完成时建索引的方案，总账为“读取含建索引”。两个总账按同一观察点比较。

### 派生重建的逐项 D 预算

以下主列和身份已读取，目录基址按批次绑定。关系类型有L条edge、u个实际非空方向Head；Hierarchy有H个Parent、u个实际非空父Head。重建在新World内执行：首遍计数，按真实数量分配，再填邻接和Applied；各数组容量形成内部不变量。

下表适用于从主值直接建立派生索引的路径。默认v5已携带有效Children/Head序列时，读取入口沿段内count分配，按有效序列建立Applied/Place；计数、序列复制与主值扫描分别按该入口实际执行次数计入。

| D 关系重建阶段 | 〈P,A,B,W,L〉 | 逐步依据 |
|---|---|---|
| 首遍统计 | 〈4,3L+u,3L+1,2L+2u,0〉 | endpoints/两Head/UsedHeads四基址；每edge读端点和两Head A3；两头Count各W1；首次非空各B1，写Generation与UsedHeads各W1 |
| 按u个头预留 | 〈0,2u,2u+1,2u,u〉 | 读UsedHeads/Head；方向选择B1/头和循环；每头Items引用与Count重置W2，项数组各L1，清零共32L B |
| 填两端与Applied | 〈2+2L,7L,L+1,5L,0〉 | 绑定行Entity/Applied P2；每edge读取两Items引用P2；端点/Entity/两Head/两项/Applied A7；两项与两Count、28 B Applied共W5 |
| 三阶段完整和 | **〈6+2L,10L+3u,4L+2u+3,7L+4u,u〉** | UsedHeads首次L1/Zero或增长另加；两Head/Applied目录按16I+16I+28I分配 |

Hierarchy的下列重建对应已完成维护的主值模式；保留进度模式按Applied与保存的Children/旧代集合恢复。

| D Hierarchy重建阶段 | 具体算法 | 临时数据与实际费用 |
|---|---|---|
| 统计父成员 | 分配int[I+1] Counts，扫H个Parent；Counts[parent.Id]递增，首次父追加UsedParents | 清零4(I+1) B；H次计数读写，u次父身份追加 |
| 绑定最终Children | 对u个父按最终布局取得Core Children列，按Counts分配Items；保存Values数组/Row/ExpectedCount到父绑定项，随后把Counts[parent.Id]改为绑定序号+1 | 每父一次实际R/列定位或最终迁移；每绑定项24 B；Items实际8H B；Counts改写u次 |
| 填成员与Applied | 再扫H个Parent，按Counts找父绑定，取得Core Children并追加child；写AppliedParent | 每child读Counts、父绑定、Core Children、Items与Applied；写成员、Count和16 B Applied |
| 完成 | 按保存的观察模式恢复Children行版本，归还Counts/父绑定临时数组 | 持久数据为Core Children的16u B、8H B成员与16I B Applied；版本/目录容量另计 |

父绑定项为{Parent Entity,Children[] Values,int Row,int ExpectedCount}，24 B。Counts在统计与填充阶段分别承担计数和父绑定编号，两个阶段顺序复用同一临时数组。所有目标布局准备完成后再绑定Values/Row，恢复过程沿用结构写范围。

局部顺序存在时，第二遍按保存的顺序写成员与place；读顺序项的A及其文件解码另加，实际主端点保持一次编码。上表普通循环只写最终值；容量已由首遍结果预留。

| 其余派生数据 | D逐项预算 | 批次/首次 |
|---|---|---|
| 值索引N项 | 每项GetKey1+Khit/miss+实际组建立+Append；见[[Indexes]] | built W1，Membership/Buckets/KeyGroup/成员初建与Grow，按需排序 |
| Sparse主值已读，新World附着重建 | 原首存在的每行〈1,6,2,4,0〉：Entity/页/cell/新节点/First/旧首A6；页引用P1；页存在/原首B2；8B cell/12B节点/First/旧首Previous各W1 | 全局Attachments/First P2、每类型pages P1；Count与附着有效数统一发布；新页各L1/清32768B；原首为空省A1/W1 |
| 启用位 | Copy_ulong(u)+PopCount u次，disabledCount W1；循环B(u+1) | 位数组L1/Zero或实际Grow |
| 追踪恢复当前版本 | Fill_Rows(N,v_T)+实际word/block填充；LastWrite W1/列；Removed事实按当前保留内容处理 | 保留版本模式复制实际数组；恢复规则与参照一致 |
| 查询匹配 | 已建立查询按CheckedArchetypes增量匹配；新World首次绑定扫描实际表 | 新World首次Query绑定和目录增长见[[Storage-and-Queries]] |
| 关系当前占用 | 每声明角色逐edge写Claim，UniquePair逐key建字典 | 每角色12I B Claim；Pair字典按Cost-Model初始化/增长 |

Sparse采用预留映射目录/附着容量，在已读取的Entity数组上逐项填映射；首附着为空时写新cell/节点/First三项。各类型稠密主值沿用读回数组，附着总数在批次末发布一次。字节清零与首个实际页始终保留。

恢复完成式：文件头/类型解析 + 身份/映射 + 各表最终分配/列读取 + 启用位/Buffer/Shared/Sparse/资源 + 引用修正 + 所有已开启派生重建 + 输入完成。延迟建立值索引时按Read+首次Find的总账比较。
## 文件提交、首次与容量变化

S-W DurableSaveStore.WriteWorld 的完整链为 BeginWrite → 检查已有两代文件 → 创建目标文件/预写 header → Encode World → 完成完整性计算/回写 header → SetLength → Flush(true) → 重新读取并验证临时文件 → Publish。令旧两代 payload 为 Z0/Z1、新数据为 Z：完整路径读字节包含 Z0+Z1+Z，写字节包含 Z 与 header 的预写/回写；实际缺失代按 0 计。文件锁、打开/关闭、flush、rename/replace 的系统调用分别保留。

D 将文件代数服务作为输出容器能力；同一容器服务同时包装 D 与 E* 时，保留其相同文件/完整性算法成本。纯编码入口接受流或 IBufferWriter，文件服务的费用按请求能力追加。完整性计算记实际处理 Z 字节的算法调用，CPU 与 I/O 成本分别入账。

五库文件路径复用DurableSaveStore的Envelope、SaveWrite、选代、生命周期及共享Digest256/Hashing流实现。编码直接进入BeginWrite().Stream，再由Commit完成原有校验和发布。文件容器版本4的头为64 B，记录认证种类、ulong代数、long payload长度和32 B摘要；最低接受代数与HMAC配置沿原服务保持。固定比较使用默认SHA-256；相同HMAC配置经由同一文件服务执行。

文件存储容量沿真实代文件单列。单代逻辑文件长度为Z+64 B；提交后两代的总长度为Z_latest+Z_previous+128 B。写入期间，原两代与临时文件同时存在，发布前峰值逻辑长度为Z0+Z1+Z+192 B；相同payload长度时分别为2Z+128 B和3Z+192 B。锁文件由OpenOrCreate建立，写入数据量为0 B；文件系统块分配和目录元数据沿实际磁盘指标另计。64 KiB文件流缓冲与完整性计算状态计入进程分配/保留，文件payload计入磁盘长度。Commit发布后结束临时路径持有，SaveWrite.Dispose结束流、完整性状态及写锁；Comparison的ArchiveAction.Dispose删除两代及锁文件。

现有ReadWorld绑定原World与注册表；多库接入在同一文件持有者增加通用BeginRead/SaveRead读取范围。它沿原有最高有效代选择与验证内核，验证所选打开文件后，将同一文件定位到payload起点交给具体恢复器；读取范围持有文件和操作寿命。该通用读回先验证Z字节，再由恢复器读取Z字节，共2Z加实际头读取；值解码直接读取文件。文件写入预算继续为上段已有两代与临时文件的验证链。OutputBytes从已提交代的payload元数据取得，文件头及元数据观察各自列示。

| 首次/增长事件 | S-E | D |
|---|---|---|
| 首个类型 codec | 注册/反射或生成元数据、SavedTypes 条目和字典增长 | 生成/用户 codec 注册、类型键字典与 int 目录发布；布局检查在注册时完成 |
| 首次 Save 缓存 | SaveBuffer、选择表/类型等数组 | 序列化实例、表绑定目录、列绑定数组、类型汇总数组；每数组分别 L1 |
| 普通已绑定 Save | 容量足时自有缓存 L0；输出后端另计 | 原生 IBufferWriter 适配层 L0；输出后端另计 |
| 首次 ReadWorld | World、读取缓存/Rows/值数组、Restore 目标列 | World、FileTypes[T]、实体映射、最终列及所需索引；直接填最终数据 |
| 表/列/启用位/缓冲增长 | Rows 与最终 World 各自容量维护 | 最终数组各自维护；copy(已用字节)、zero(新容量) 与发布 W1 |
| 类型/绑定目录增长 | 各自数组/字典增长 | 每目录 L1、copy(旧容量·项宽)、zero(新容量·项宽)、发布 W1 |
| 启动后台保存 | COW 根与各存储共享标记，首次改动复制相关 backing | 当前隔离实现同步Capture后Task.Run，新建独立序列化器并首次绑定；完整任务结束包含检查点释放。D的编码Job候选单列以下预算 |

类型键和 durable schema 的附加字节是保留能力成本，raw/codec、保留/重映射、缓冲深复制/普通引用浅复制分别选同语义路径。上述源码支撑调用与分配结论；新组织、字段预算和缓存策略为 D。完整保存与恢复的总成本由所有实际段、codec、索引维护和所选文件服务相加。

## 完整成本选择

D的内存Capture有效数据工作：入口 + 保存记录Row/Generation读写各8I B + 行身份读写各8N B + Σ普通主值复制 + Σ启用位/所选追踪数组复制 + 总计B字节的Buffer元素复制 + Sparse的8S B身份及实际值 + Shared实际K值 + R个资源 + 主拓扑/顺序。保存记录仍按16 B步长容量持有，其余独立数组按各自容量及元素宽度持有；有效数据流量、保留余量和首次/增长清零分别计账。每个首次建立的独立数组L1，Buffer元素首复制的数组按L1计。当前原型的身份表示、逐次分配与拥有值上下文按本页“隔离原型的持有者与预算适用性”核算。类型池与目录首次费用由[[Containers]]持有，Restore在这些实际复制外加入旧值释放、代数修正和本页派生重建。

快照释放按Chunk的实际拥有值列调用既有ReleaseRows。Buffer列沿列操作批量归还或清除header，其他拥有值按其类型操作释放；Presence由同一列释放入口选择实际成员。列类型分派按实际拥有值列计一次，每项资源及元素的释放工作计入原有最终清理阶段。

当前隔离后台入口SaveAsync的链为：调用线程完成Capture → Task.Run排队 → 新建WorldSerializer及冷绑定 → SaveCaptured编码/输出结束 → finally Checkpoint.Dispose → 返回Task完成。SaveFileAsync在Task内沿AtomicSaveFile.Write完成BeginWrite、编码、Commit和SaveWrite.Dispose，再结束检查点；返回Task完成包含文件发布与两类拥有者的释放。闭包、委托、Task、独立序列化器及其首次表/类型绑定和输出缓冲按每次后台请求计入分配；同步入口已热绑定的缓存预算按其自身持有者使用。

D的编码Job候选沿同一Capture后，将排队/权限登记/依赖/完成接入[[Jobs]]，其全部后端费用与编码器实例/绑定寿命同时核对；该候选的源码和机器证据对应实际接入点。同步完整存档：读范围/依赖 + 列编码总式 + 输出/文件提交。完整读取：输入/文件验证 + 类型解析 + 最终主数组 + 字段解码/重映射 + 派生重建 + 输入结束。

文件服务候选保持相同的双代与原子提交能力：当前服务实际读Z0+Z1+Z、写Z+两次header、Flush(true)一次、临时文件验证读取一次、替换/改名一次；缺失旧代按实际字节0计。复用该服务作为既有依赖，D与E*各调用一次；文件内容完整性处理按实际Z字节的同一算法计。内存输出选IBufferWriter直接填段；Stream适配按实际段写入/Flush，适配器L与缓冲增长各自计入。

本页选型把类型解析、表绑定、共享值归并和派生恢复移到明确的首次或恢复阶段；保留每个codec的真实工作、实际输出字节、后台复制和文件服务。S列支撑实际源码中的调用/数组/分配；D列支撑拟议实现的预算与容量式。

## 格式、引用与恢复完成设计

版本5容器按类型清单 → 实体身份 → Shared主值 → 表列 → Sparse → 资源 → 拓扑主值/顺序组织。每个实际主值有一个输出段；绑定表标记普通列、Shared元数据、受控Parent/endpoints各自负责的段。类型列表包含所有实际codec和存储种类，表描述只输出对应的文件类型编号。

类型清单和表列按格式 GUID 递增，检查点登记身份使用同一顺序。读取按文件顺序核对重复与乱序，以格式描述绑定 codec，再将本地组件编号归入 TypeSet。跨进程登记顺序由各进程独立决定，文件目录编号只在本次操作内使用。

长期存档使用当前schema及相同字段codec；检查点使用当前构建/布局标识。格式版本和类型schema在读取入口解析，随后按文件类型数组绑定一次。类型变化和旧容器转换沿ADR0005的离线工具规则进行。

实体身份模式分别执行：
- 保留身份：身份段先写4I B的Generation，空闲ID段写4F B的Row链，表行段补12N B的Chunk/Row。F+N=I时，记录字段的数据写合计16I B，分配清零另计。
- 重映射：RemapEntry为{SourceGeneration,Target Entity}，12 B/源ID，索引0沿空身份保留。全部Used槽位先分配目标编号，当前代与进度旧代沿下节两种映射视图处理；引用输出为8 B Entity。
- 外部引用：使用注册resolver，其输入是实际外部键。普通列、Buffer元素、Sparse、资源、Parent/endpoints均使用同一规则。

所有读取先建立最终主数组，拓扑在本次主值/引用全部到位后恢复。读取的最终返回点包含所声明的即时索引、Shared归并、Sparse映射和拓扑完成；按需值索引的比较观察点再加入首次Find。

## 检查点观察模式

### 身份选项和外部键的入口

目标入口复用既有`WorldSerializer.ReadWorld(Stream input, SerializationRegistry registry, WorldLoadOptions options = default)`。IdentityMode、MissingReferenceMode、ReadLimits、RequiredContract、Execution由WorldLoadOptions传递；Preserve/Remap分别由WorldRestorer/WorldImporter组织，隔离原型Load承担最终列填充。Capture/Restore采用保存身份，需要重新分配身份的检查点沿现有Save(snapshot)与ReadWorld(Remap)组合，其编码、输入和映射费用共同计入。

带系统进度的读取补充同名重载`ReadWorld(Stream input, SerializationRegistry registry, WorldLoadOptions options, Func<World, ISystemVersions[]> bindSystems)`。候选World的主值、服务、即时索引、Shared成员和Sparse映射恢复后、读取系统尾段前调用bindSystems建立参与者，返回数组按文件声明的固定顺序交给现有RestoreVersions流程。RebuildDataIndexes在绑定回调前完成一次。三参数入口使用空参与者集合。回调持有本次绑定的对象，调用者在ReadWorld成功后一起接收World和系统；候选失败沿现有释放链结束。

系统对象、查询/资源绑定、参与者数组及RestoreVersions的分配和写入计入本次ReadWorld首次费用，绑定与恢复沿同一候选完成链执行。

读取先绑定类型并建立全部Used槽的目标编号及映射规则，再逐列解码。普通列、Buffer元素、Shared、Sparse和资源的Entity字段使用当前活身份映射，MissingReferenceMode复用PolicyReferenceRemapper的KeepOriginal/Clear/Throw。受控Parent/Children/endpoints及其保留事实、Applied/Head与retired/Destroyed身份使用进度视图：当前活代直接读Target；旧代键(Id,g)满足0<g<abs(SourceGeneration)时映射到(Target.Id,g)。目标槽保持源当前Generation，后续正常复用从当前代继续递增，旧代身份沿原失效状态保存。完整旧代映射规则由前置身份头确定，两种视图直接使用同一密集表；完整Entity键中的g沿调用局部值进入结果。

Applied/Head按Target.Id重排目录并保留各项Generation，内部Entity字段沿进度视图修正；retired键、Children成员和Destroyed事实沿同一视图处理，序号与消费者位置按原值恢复。每个实际旧代键读取一次密集条目，当前代/历史代判断、Generation保留与Entity赋值按实际次数计入；映射数据由原12 B/源ID密集表持有，逐键临时对象L0。索引、拓扑与reader/系统进度在同一候选完成链结束。

值绑定同时持有codec实例与patcher实例。目标`IReferencePatcher<T>.Remap(ref T value,IReferenceRemapper remapper)`沿现有接口放宽T约束，组件/Shared/Sparse/Buffer入口继续限定struct，资源沿原T值类别修正`ref ResourceValue<T>.Value`。RegisterCodec与RegisterBufferCodec补同名三泛型重载`(typeKey,codec,patcher)`；RegisterShared、RegisterSparse、RegisterResource补同样的实例重载。旧的默认codec/patcher入口组合到同一绑定，显式patcher替代该类型的默认生成修正，带resolver的codec与实体patcher共同保留各自数据。DataReader.ReadEntity继续返回源身份，由绑定选中的patcher完成一次映射。Buffer按元素、Removed按保存值包装同一patcher，保留原Count及Version。每份解码值在Shared归并、索引或拓扑建立前完成一次修正。

每次重映射读取持有一份密集映射，两种映射视图复用这份数据。原型主体持有Entry数组、当前视图、进度视图三项引用，构造W3；每个视图持有主体引用、missing模式、进度标记及绑定委托，构造W4。主体与两视图共L3，另有密集数组L1及两委托L2，合计L6；对象实际字节与数组头分别计入，数组数据仍为12(I+1) B。Core生成的EntityMap入口通过现有external委托绑定相应视图，委托调用按每字段间接调用计费；视图在读取期建立，实体值视图在批次外绑定。注册表持有codec/patcher声明，读取范围持有映射，目标World持有解码后值；这条链的持有者和释放按各自范围计入。

外部对象和资产复用ExternalReferenceKey(Guid)、IExternalResolver.TryResolve(key,out object)、WriteExternalReference/ReadExternalReference。调用者通过RegisterCodec(typeKey,codec实例)或RegisterBufferCodec绑定resolver；codec编码16 B Guid，将非空key收集到SerializeOptions.ExternalReferences，读取后解析到声明的对象类型。空Guid表示空引用，解析失败沿codec的失败规则结束读取。Entity字段继续编码8 B Id/Generation。注册表持有绑定，候选World持有解析后值，resolver和收集集合由调用者持有，默认v5 payload与原文件服务保持原路径。

隔离入口在原Save名字上增加必选SerializeOptions参数的重载：World的Stream/IBufferWriter重载继续接受progress及systems，WorldCheckpoint的两种输出重载使用已捕获的进度。旧contract重载将各自默认用途组合为options。四个编码入口都把同一ExternalReferences交给DataWriter；SaveAsync与SaveFileAsync在Capture后把options交给检查点编码，SaveFile把options交给原AtomicSaveFile回调，文件服务沿原提交链执行。Contract与写侧容量上限沿同一options传递至既有用途/容量处理。

SerializeOptions持有Contract、MaximumSparseMemberships、MaximumTopologyRecords、MaximumTopologyPayloadBytes与ExternalReferences五字段，x64值布局32 B。调用方集合按实际容量持有Guid键：HashSet entry为hash/next 8 B加Guid 16 B，共24c_entry B，桶另4c_bucket B；对象头与增长另外计入。后台闭包保存这份32 B值及其中的集合引用至Task结束，编码入口沿同一集合执行Add。

写侧先借用现有类型化视图计Sparse总membership与拓扑实际记录，计数门L0。拓扑记录包含Parent/Children/Endpoints主行、Children成员，以及保存的Applied、Head、retired与future项和成员。后台Capture在已有读取范围内、复制身份/主值/资源/服务之前完成计数门，随后编码复用已检查事实。字节上限沿实际拓扑主段与Hierarchy/关系服务段累计；关系T载荷沿普通列预算计入。默认上限0沿原容量规则执行，开启上限的控制和实际编码字节分别计费。

后台编码持有调用方收集集合及codec引用至Task完成，集合Add发生在实际字段编码时；这份引用与Capture的主值复制分属各自持有者。检查点封装补同名options重载并复用原编码内核。收集集合、resolver和映射上下文的寿命分别在编码Task、读取范围及调用方对象中结束，默认空集合沿原输出字段执行。

### 销毁消费者的稳定对应

跨World或参与者集合变化的保留恢复补充`DestroyedReader World.WatchDestroyed(Guid consumerId)`与`SerializationRegistry RegisterDestroyedReader(Guid consumerId)`。World持有Guid到reader目录，具名入口取得或建立同一逻辑消费者，Dispose解除实例登记。原无参入口用于本地读取，其位置数组恢复采用同一固定参与者集合和注册顺序。

运行时目录为World惰性持有的Dictionary<Guid,DestroyedReader>，consumerId使用非空Guid，reader内部保存该16 B键；无参reader使用空键。首次具名注册建立字典及其桶/entry数组，新消费者建立reader并从Through开始，已有键返回同一有效实例。注销按键移除并稳定搬移紧密reader目录的后缀。x64字典entry为hash/next 8 B、Guid 16 B、reader引用8 B，共32c_entry B，桶另4c_bucket B；reader对象、目录首次与增长逐项计费。

紧密reader目录注销采用稳定后缀搬移，匿名子序列保持注册顺序；复制实际q_tail个reader引用的费用计入注销。内存Capture/Atomic保存全部具名进度；序列化的保存与目标键集合由各自RegisterDestroyedReader声明。读取在事件恢复后统一激活目标声明键并置Through，再由文件服务段叠加已保存位置；源服务数为0时目标声明同样完成登记。

World最终释放先完成实际reader，再清具名目录引用；费用包含实际消费者遍历、已用32 B entry引用清理及桶清理，目录对象随World持有期结束。

具名进度作为可选WorldServiceData/SavedService保存，每项为Guid consumerId和ulong Position，共24 B，另计count与目录。Capture在原结构读取范围复制；Restore先恢复事件与Through，再按Guid建立或取得目标reader并Reset保存位置，目标新增消费者从Through开始。层级和关系reader由各自SavedData恢复。Clear由Core.ClearLifecycle统一Reset，Dispose沿原完成链结束。

CopyEvents/RestoreEvents的位置数组仅按固定无参reader子序列对应，具名reader完全由Guid服务保存和恢复。混合注册时，运行时目录仍可紧密换位，位置数组的投影按声明的无参子序列形成；默认全部无参的v5路径保持原字段。服务reader最终由自身SavedData覆盖其进度，跨World的外部消费者沿具名入口取得对应。

内存检查点和Atomic暂存World沿同一具名数据服务传递消费者记录，暂存World持有这些逻辑消费者至提交完成，候选捕获继续携带Guid及位置。显式注册后通过现有服务段的类型化条目编码，默认服务集合保持原payload。应用在ReadWorld返回后以相同Guid取得恢复的reader；目录换位由运行时目录承担，恢复对应由Guid持有。

| 模式 | 捕获内容 | 恢复动作 | 额外成本 |
|---|---|---|---|
| 已完成维护的主值 | 主值、身份、版本、局部顺序、实际保留事实 | 按主值重建Applied/邻接/索引；消费者从本次基线开始 | 本页已有主数组复制与派生重建 |
| 保留消费进度 | 上项以及Applied、旧代集合、尚未消费的销毁序列、reader/系统基线 | 恢复同一已应用状态与未消费范围，后续Maintain继续处理 | 复制每个实际Applied项、邻接顺序与旧代项、8f B事实、实际进度项 |

默认长期存档在调用者完成规定维护之后进入读取范围。内存检查点选择保留进度时，Applied表达捕获时已经应用的端点，重建以这份Applied为输入；当前主Parent/endpoints继续保留捕获时的值。两份数据分别服务当前编辑结果与已应用视图。

普通managed引用的复制遵循类型注册规则。后台编码所读取的引用对象在编码期间保持对应内容；拥有可变托管对象的类型提供实际独立克隆，字符串等不可变对象按引用复用。完整Capture的分配和字节包含这些真实克隆。

恢复到现有World时完成所有Job和引用范围，释放被替换的实际Buffer/资源，恢复主数组及选定模式的版本/事实，再推进数据代数。新World读取在完成这些工作后返回结果，由调用方持有该World。跨World模板与依赖旧身份的绑定按数据代数重新建立；原Chunk对象的引用只有在原对象实际沿用时继续有效。

完整预算为本文保存/读取总式，加所选观察模式的实际状态复制、[[Changes]]的消费者恢复及[[Adaptation]]的E*完整扩展。每个实际数组分配、旧容器释放和派生恢复在自己的阶段计一次。

## 版本5的字段顺序

此段为目标格式定义。整数采用小端；Guid、字符串、SerializationTypeKey复用现有PayloadFormat的Write/Read配对编码，类型键由StableId、StableName、SchemaFingerprint组成。字段codec沿注册表提供的具体类型实现。

| 顺序 | 字段 | 编码与循环 |
|---|---|---|
| 文件头 | Magic、Version、Contract、Kind、BuildIdentity | uint 0x53434553、ushort 5、Contract byte、Kind byte固定0、Guid；Kind=0表示完整World状态及其检查点编码 |
| 类型清单 | TypeCount及各类型键/存储种类/固定宽度 | int；每项TypeKey、byte存储种类、int固定宽度；变长为0 |
| 观察模式 | ProgressMode、WorldVersion | byte：0重建、1保留；ulong |
| 身份 | Used、Alive、FreeCount、Generations、FreeIds | 三个int；Id 1..Used各int代数；FreeCount个int按复用次序 |
| Shared | TypeCount及各类型的ValueCount/主值 | 文件类型Id int、ValueCount int；按SharedId顺序编码实际值 |
| 表集合 | ArchetypeCount及各表类型/GroupBy键组合 | count均为int；文件类型Id数组，随后GroupCount及各组的文件类型Id/int键 |
| 物理块 | 每表ChunkCount、各块RowCount/Entity行 | count均为int；每行Id/Generation各int |
| 普通列 | 按表类型次序的存在位、列值、启用位、版本 | 存在位决定实际值行；列值含Shared行编号及由此段承载的类型，其余主值由下述拓扑段承载 |
| Sparse | TypeCount、各类型RowCount/Entity/值 | 文件类型Id及count均为int；紧密实体和值数组 |
| 资源 | ResourceCount与类型Id/值 | count和Id为int；每类型一个值 |
| 拓扑 | 各domain/关系声明、Parent/endpoints/Children与局部顺序 | 两模式均按类型写有效Entity/值与footer，方向/基数沿注册声明 |
| 销毁事件 | Through、事实数组、reader位置数组 | ulong、int count、各项Entity/ulong Sequence、int readerCount及ulong位置；Rebuild写空事实和位置数组 |
| 服务状态 | ServiceCount及各服务类型Id/状态 | 两模式均输出；Preserve保存Applied/旧代/差额及进度，Rebuild输出对应重建状态和顺序 |
| 系统状态 | 参与者count、VersionCount及Last/Current | 两模式均输出相同形状；Preserve保存基线，Rebuild按本次版本建立基线 |
| 结束 | 最后一个已声明段结束 | 外层文件服务负责文件代数与提交 |

存储种类在默认版本5固定为：0普通、1Tag、2Buffer、3Shared、4Sparse、5Resource、6Parent、7Endpoints、8Children、9Removed。类型绑定决定解码入口，Removed的T与追踪声明来自当前schema。表清单保留完整组合，Parent/endpoints/Children值统一在拓扑段输出一次，普通列循环保留其存在位、启用位和变化元数据。具名reader通过显式注册的服务类型键进入服务状态段，字段由该绑定持有。

默认Rebuild先在保存或Capture前完成Maintain，拓扑段保存该已应用时点的Children和邻接顺序；读取沿这些有效序列建立Applied/Place并推进本次基线。Preserve恢复保存的Applied、退休项及未消费范围。两模式共享完整字段次序及服务/系统尾段，复制量和输出字节按实际段计入。

每个字段codec值段先按已知有效行数编码，随后写int实际payload字节数footer；raw固定宽度列按有效行数×size直接读写。每列先写存在掩码：byte 0表示全部行存在，1后接ceil(RowCount/64)个ulong。值段按该掩码选取实际存在行；随后写启用掩码，byte 0表示全启用，1后接相同词数的ulong。末词高位清零。追踪元数据写byte声明位（Written=1、Added=2）及对应行版本；重建模式以保存的观察基线建立摘要，保留模式恢复行版本并重建摘要上界。

追踪行版本在小端平台沿已有WriteRawBytes写入连续ulong或Added/Written结构数组；其它平台沿固定小端字段写入内核，字段顺序与字节宽度相同。普通codec列先按存在掩码选择完整列或实际存在行循环，完整列直接遍历绑定值span。

固定协议字段复用连续主数组写出：Entity为Id/Generation两个int共8 B，AppliedParent为Parent/Place/Generation共16 B，AppliedEdge为Source/Target/SourcePlace/TargetPlace/Generation共28 B，邻接项为Other/Edge共16 B。小端平台沿既有WriteRawBytes输出这些顺序布局的有效span，其它平台沿对应字段编码。表行、Sparse身份、Children成员及未来成员序列复用DataWriter内部WriteEntities；单Entity的共享编码入口使用一次8 B写入。各项count、邻接头的Generation/Count/Ordered、列footer和用户codec调用沿原顺序输出。

Stream适配器使用现有4 KiB缓冲合并小段：WriteRawBytes的小段沿Take写入缓冲，达到缓冲边界时Advance；至少4 KiB的段先提交缓冲，再直接写Stream。直接IBufferWriter仍沿目标span复制。字段写入与小型邻接段共享同一缓冲，复制字节与实际Stream调用在所选输出后端入账。

拓扑主值以“Entity身份→该行codec值”的既定顺序输出。SavedColumn的类型化WriteEntityRows一次接收列与有效Entity span，在闭合T/TCodec循环内完成身份和值写入；完整列的数量直接取Chunk.Count，带存在位的列按实际有效行计数与写入。World主列和独立检查点列复用同一内核，类型分派发生在每个列批次入口。

Buffer值按每个存在行的int Count和实际元素编码；Shared列按存在掩码逐行写int共享编号，共享主值由前面的Shared段保存。Sparse和资源采用同一值段规则。局部顺序记录父/端点Entity、成员count及按顺序排列的child/edge Entity。ProgressMode=1的Applied决定已应用集合，当前Parent/endpoints决定当前主值；差额目标索引从二者重建。

多Chunk格式逐块处理最终列；C与J在费用式中统计实际编码的物理列和实际值段。类型/Archetype绑定一次，各Chunk重新读取基址。读取按类型绑定→身份与最终块分配→值解码→引用修正→所选进度恢复→索引/Children/邻接完成执行。身份段包含完整存活集合，引用修正可在所有目标身份建立后统一进行。

## 捕获访问与系统进度接入

同步保存/Capture在调用方的系统观察边界进入：绑定系统的公开完成handle已结束，调用方在Capture返回后开始下一次更新。公开完成包含命令、规定维护和系统基线。随后先取得World结构Read，汇总实际组件/Buffer/Sparse/资源的值Read及所需摘要Read，在同一值登记阶段等待这些访问全部就绪，再读取主值、版本及内容。范围持续到编码结束或独立Capture完成；后台编码读取已经独立持有的数据。访问费用按Jobs的(k+1)项登记/等待/释放计入。

SystemGroup继续持有自己的Last/Current版本。Core提供进度接口ISystemVersions：VersionCount、CopyVersions(Span<ulong>)、RestoreVersions(ReadOnlySpan<ulong>)；每项依次写Last/Current两个ulong。Systems实现该接口，Serialization绑定调用方按固定顺序提供的参与者，保持依赖方向。保留进度容器为每个参与者写int VersionCount及16n B版本对；同构建、同系统配置的恢复按相同顺序写回。

重建模式以恢复后的新观察点初始化消费者；保留模式同步恢复保存的World版本、关系reader与已绑定系统进度。系统进度在上述调用方观察边界复制，行/容器内容在实际值访问内复制。长存档默认沿重建模式，保留进度检查点包含明确绑定的参与者。

# 资产重构中的序列化能力审查

日期：2026-09-12。用途：记录现有能力、独立审查和成熟实现对比得到的问题，为[冻结原则](Asset-Rebuild-Principles.md)及[统一执行任务](Asset-Rebuild-Tasks.md)提供依据。用户已明确要求资产重构一并解决下述全部不足。

## 范围结论

**全部现有资产接入与 D01–D10 序列化不足一起交付。** 沿用有效的静态编码、根 / 块、范围 I/O 和包能力，同时修正已确认的缺陷、职责及 API；未讨论的业务架构继续保持。

沿用有效实现不是旧资产兼容层，也不意味着冻结现有 API 或协议缺陷。字段排列和根类型描述确需改变时，明确最小协议调整、版本及受影响产物；不建立并行容器、旧格式回退或第二套生成框架。

实际阅读了静态编码、类型目录、生成器、文档 / 视图打开、保存、范围来源、共享字节、调度与预算、包签名、原生布局及相关测试。独立审查时执行了 `dotnet test tests/SomeEngine.Serialization.Tests/SomeEngine.Serialization.Tests.csproj --no-restore --verbosity minimal`：149 通过，0 失败，0 跳过。随后四个公共 API 探针复现了 D01–D04；探针与生成内容已清理。**这些是修改前的审查证据，不表示问题已修复，也不表示其他未测路径都正确。**

## 已确认的不足与交付要求

| 编号 | 证据与问题 | 统一任务及要求 |
| --- | --- | --- |
| D01 | 修改签名包中的嵌套资产，使其内部摘要仍自洽；普通文档入口抛出 CryptographicException，生成的编码视图却读出修改值。BinaryDocumentView 的打开流程没有执行普通路径中的包认证核对 | B01：共用必要解析与认证；两种入口都必须拒绝。A19 / A31 验证包和消费者 |
| D02 | 总额度 4、外部解码内容占用 2、无活跃使用的缓存占用 2；请求另一份 2 超时，手动 Trim 后完成。局部额度未耗尽使共享账本不足时没有淘汰可释放内容 | B02 / A09：预算压力下自动推进，覆盖多使用者、释放 / 取消 / 失败的唤醒 |
| D03 | SharedBytes 先 Write 取得 Memory，再 Retain，最后通过先前 Memory 写入，两份引用均看到修改 | A06 / A08：落实可写期间与共享的互斥或消除逃逸写别名；页级复制本身不解决该问题 |
| D04 | 同一压缩块从文件读出 512 字节，改用 MemoryRangeSource 后抛出 NotSupportedException；代码对映射来源有同类限制 | B03：分离来源能力与内存策略，允许有明确目标、使用期限和预算的合法解码 |
| D05 | 通用流式写入仅对库内部公开，读取只接收连续 Span；调用方缺少完整标准缓冲区接口 | B04 / A10：IBufferWriter、连续 / 分段读取及单次编码，明确实际分配与缓冲行为 |
| D06 | 文档及包的写入入口固定要求 FileStream，实际格式要求的是写入与定位能力 | B05：接受实际能力足够的 Stream，保持有界写入、目录回填与文件级原子输出 |
| D07 | 通用编码生成反向调用资产生成注入字段；同一库内混入资产组织、GPU 预算及来源驻留策略 | B00 / A02 / A04 / A14：明确依赖方向与主要数据持有者，复用实现，不新增重复框架 |
| D08 | 生成视图每次定位后置字段时重新解析前面的字段、集合或字符串 | B07：固定偏移或复用必要定位信息；以公开访问行为及正式基准验收 |
| D09 | 编码字段按源码文件路径与声明位置排列，partial 文件整理可能改变编码顺序和 schema | B06：明确字段身份 / 顺序与冲突诊断；文件整理不再改变数据定义 |
| D10 | Inspect 需要预期业务类型，当前头与类型目录未独立明确无歧义根选择，不能直接完成未知类型公共描述读取 | B08：公共描述无需业务解码；必要时局部补充根类型标识并明确协议版本 |

D01–D04 有独立公共 API 运行证据；D05–D10 是源码确认的能力或结构问题，其中性能改善程度要由任务内实际测量确认。任务依赖和完成状态只在统一执行文件维护，不在本页再维护一套勾选状态。

## 已有能力与决定

| 区域 | 实际代码依据 | 决定 |
| --- | --- | --- |
| 静态编码 | [Contracts.cs](../../../src/SomeEngine.Serialization/Contracts.cs)、[BinaryContractSerializer.cs](../../../src/SomeEngine.Serialization/BinaryContractSerializer.cs)：静态 Read / Write、现有严格 schema 检查、一次写入调用方目标 | 保持架构和编码规则；资产 Duplicate 不调用编码后解码的 Copy |
| 类型身份与注册 | [BinaryContractCatalog.cs](../../../src/SomeEngine.Serialization/BinaryContractCatalog.cs)：显式 Register、TryGet、Freeze；Contracts 中已有独立文件类型名到类型 ID 的计算 | 复用已有能力，不另造同义二进制类型目录；资产实例目录及创建能力只保存自己新增的事实 |
| 源码生成 | [BinaryContractGenerator.cs](../../../src/SomeEngine.Generators/BinaryContractGenerator.cs)：增量生成、字段分类、集合 / 字典、闭合分派、编码视图和程序集本地注册 | 保留有效静态生成；修正资产注入、字段排列、视图扫描并补足分页声明接入 |
| 二进制容器 | [BinaryDocumentFormat.cs](../../../src/SomeEngine.Serialization/Containers/BinaryDocumentFormat.cs)：SEBDOC03、固定头、类型目录、根、块目录、压缩与摘要 | 作为修正基础；B08 核对明确根类型所需的最小协议变化，其余有效结构沿用 |
| 读取与描述 | [BinaryDocument.cs](../../../src/SomeEngine.Serialization/Containers/BinaryDocument.cs)：头 / 类型目录读取、明确根解码、按块查找、选定来源版本、读取到调用方 Memory | 直接作为新 Asset 的下层能力；公共资产描述或分段目标存在缺口时局部补充 |
| 保存与未修改块复用 | [BinaryDocumentWriter.cs](../../../src/SomeEngine.Serialization/Containers/BinaryDocumentWriter.cs)：根流式写到目标文件、回填目录、AddChunkAsync 复用来源块 | 开放标准缓冲区和可定位输出，接入非连续页；复用现有流式写入和原块复制 |
| 范围 I/O | [RangeSources.cs](../../../src/SomeEngine.Serialization/IO/RangeSources.cs)、[HttpRangeSource.cs](../../../src/SomeEngine.Serialization/IO/HttpRangeSource.cs)、[MemoryMappedRangeSource.cs](../../../src/SomeEngine.Serialization/IO/MemoryMappedRangeSource.cs) | 保留文件显式偏移、内存 / 映射、HTTP 版本约束和范围边界，不为资产重构全部重写 |
| 读取调度与预算 | [ChunkRequestScheduler.cs](../../../src/SomeEngine.Serialization/Streaming/ChunkRequestScheduler.cs)、[ResidencyBudget.cs](../../../src/SomeEngine.Serialization/Streaming/ResidencyBudget.cs)：同键请求去重、等待者取消隔离、pin、LRU、四类预算 | 修复共享预算淘汰和唤醒；再接入页、内容版本及有效 T 的保留 |
| 单块读取 | [DocumentChunkBatchLoader.cs](../../../src/SomeEngine.Serialization/Streaming/DocumentChunkBatchLoader.cs) 当前定义 DocumentChunkLoader，逐块读入最终持有内容 | 当前代码没有相邻范围合并；旧文档中的邻接批量读取描述不能当作已实现能力，也不因此擅自恢复 |
| 包、差分与签名 | [AssetPack.cs](../../../src/SomeEngine.Serialization/Packs/AssetPack.cs)：嵌套文档范围、差分构建、原子输出、签名与校验 | 沿用并接入共同认证和实际输出能力；适配必要根描述变化，不另建包系统 |
| 编码视图 | [BinaryContractViews.cs](../../../src/SomeEngine.Serialization/BinaryContractViews.cs)、[BinaryDocumentView.cs](../../../src/SomeEngine.Serialization/Containers/BinaryDocumentView.cs) | 修复认证与重复扫描；保持可选读取方式和明确使用期限，不产生 Asset 业务字段镜像 |
| 原生布局 | [NativeBlocks.cs](../../../src/SomeEngine.Serialization/NativeBlocks.cs) 及生成器的显式布局证明 | 保持；托管编辑页的选择不意味着把原生数据访问和图形上传机制全部改成托管 |
| 资产编辑共享 | [SharedBytes.cs](../../../src/SomeEngine.Serialization/Containers/SharedBytes.cs)、[SharedData.cs](../../../src/SomeEngine.Serialization/Containers/SharedData.cs) | SharedBytes.Write 当前会复制整个 Memory；这是页级 COW 的直接改造点。通用引用计数能力是否复用按实际用途判断，不按文件名一并删除 |

## 必须调整的连接处

### S01　去掉资产字段镜像接入

BinaryContractGenerator 的 Generate 目前调用 AssetGenerator.AddFieldDeclarations，再重新收集类型；AssetGenerator 会生成嵌套资产、Fields 和 SharedData 持有关系。这部分与独立资产声明、无 XXXData 的目标冲突。

改动应集中在资产声明和访问生成的接入方式。静态编码接口、已有字段类型编码、闭合分派、类型 ID 和注册能力不需要因此重写。

当前通用生成器还限制非 object 基类，并按已有集合种类生成读取。不能假定新的 Asset 基类或分页集合已经能直接代入。应先用现有手写静态编码扩展能力 / 最小生成增强证明接入，不能用另一个业务字段 DTO 绕过问题。

### S02　把整块字节复制改为页级共享

SharedBytes.Write 在共享或只读时执行整个 Memory 的 ToArray。新的页索引、竞技场和 COW 应替换这条资产编辑路径。

这不等于删除 BinaryDocument 的只读内容访问，也不等于把库中所有显式复制方法改成 COW。编码器的 Copy 若有独立消费者，继续保持其原含义；资产 Duplicate 不再借用它。

### S03　接入现有读取与调度

BinaryDocument.TryReadChunkAsync 已通过 destinationFactory 接受调用方最终 Memory，调度器已有同键去重和驻留 pin。先评估将页池内容接入这些边界，不能再建一个职责重复的读取调度器和预算账本。

当前调度键在一个调度器范围内使用 ulong；跨文档 / 版本共享不能直接混用该键。明确内容范围或局部键适配，同时完成 D02 的预算修复，并验证调度器销毁、文档关闭与有效 T 的保留关系。

### S04　处理分段目标和压缩块的真实限制

当前块读取接收一个连续 Memory，AddChunk 也主要接收连续内容。新页存储若形成非连续内容，可能需要在现有解码 / 写入路径增加分段提供方式，或者让一个连续解码区域承载多个独立 COW 页。

不能为了适配省事而先复制整块解码内容再复制到每张页；也不能在没有测量前要求重写整个读写器。

压缩块可能需要整块解压和校验后才能提供某个小页；内存修改仍可保持页级 COW。冷读粒度、磁盘块粒度和内存修改粒度必须分别记账，不以缩小内存页为理由擅自改动文件格式。

### S05　新增可读源表示，继续使用当前二进制产物

“人维护的资产定义可读”是本次明确新增的方向，应增加可读前端及到现有二进制编码的连接。它不要求替换二进制后端。

公共资产 ID / 类型 / 引用描述放入合适的资产定义或根信息。已有 Inspect 是带预期类型的入口，不代表已经具备全部未知资产类型发现功能；若需要无业务类型的公共描述入口，补充相应读取能力，而不是改写整个信封。

所有现有资产都接入新体系。资产定义、字段排列或根描述改变时明确 schema / 容器版本并重建对应产物；其他有效编码与结构不为换名而变化。

### S06　保留未涉及的验证与公共能力

现有测试已经覆盖静态读写、编码顺序、文档头 / 块、来源版本、等待者取消、pin、差分包、签名、视图和原生布局等公共行为。它们继续作为回归要求。

只针对明确改变的 Asset / 序列化 API 或协议调整相应公开预期。公共二进制协议、schema 拒绝、生成诊断和 AOT 验证不属于“无意义旧测试”；同时补齐 D01–D10 的验收。

## 成熟实现对比的采用范围

- MemoryPack 的直接类型声明、静态生成及标准缓冲区 API 与本项目方向相近；其文档明确部分 Stream 接口会先缓冲完整内容，因此只借鉴 API 能力，不照搬隐含缓冲策略。[官方文档](https://github.com/Cysharp/MemoryPack)
- MessagePack-CSharp 区分统一入口、每类型编码和基础读写器，适合作为公开 API 层次的参照；本项目继续使用静态类型接入。[官方文档](https://github.com/MessagePack-CSharp/MessagePack-CSharp#high-level-api-messagepackserializer)
- Protobuf 的明确字段编号和 MemoryPack 的显式顺序说明支持独立于文件组织的数据声明；不因此引入另一种 schema 语言或自动旧版本兼容。[Protobuf 字段规则](https://protobuf.dev/programming-guides/proto3/#assigning-field-numbers)
- FlatBuffers 的缓冲区访问和可选对象解包是两种不同使用方式；本项目保持编码视图可选，不强制 Asset / T 使用额外字段模型。[官方 C# 文档](https://flatbuffers.dev/languages/c_sharp/)
- Cap’n Proto 的官方 C++ 实现提供分段竞技场与明确消息访问边界；其分配方式不直接等于本项目的长期编辑页或 COW。[官方文档](https://capnproto.org/cxx.html)

这里只采用与已确认问题对应的设计依据，没有进行跨库性能比较，也不把第三方宣传数字作为本项目验收标准。

## 已发现的文档与当前代码差异

- docs/BinarySerialization.md 仍描述邻接范围批量读取；当前 DocumentChunkLoader 明确逐块读取，相关测试也使用独立目标内容。
- 该文档的资产接入部分仍描述较早的 AssetFormat / AssetWriter 流程；不能仅凭这段说明判断当前资产生成和生命周期。
- 因此，对未讨论的序列化架构以实际公共能力和当前实现核实为准；文档过时需要修正文档，不自动扩大重写范围。

## 任务准入条件

每项改动必须写清楚：

1. 对应本次讨论中的哪一项明确变化。
2. 现有哪项能力可以直接沿用。
3. 实际冲突或缺口位于哪个连接点。
4. 最小修改范围与需要保持的公开回归行为。

没有具体冲突证据的区域，继续使用现有架构。不能把“接入新 Asset 需要修改一些代码”改写成“建立全新的资产与序列化基础库”。

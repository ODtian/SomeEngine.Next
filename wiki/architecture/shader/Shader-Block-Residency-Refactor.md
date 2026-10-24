# Shader Block、驻留与 Slang 重构规范

日期：2026-09-29

状态：规范已确定；实现和验收仍有未关闭项，见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md)。本轮文档修订不关闭其中的实现偏差和组合测试崩溃。

适用范围：`SomeEngine.Assets`、`SomeEngine.Resources`、`SomeEngine.Assets.Importers`、`SomeEngine.Graphics`、D3D12/Vulkan 后端、Render、内容构建、测试与 Shader 架构文档

## 1. 结论

本次重构只保留下面一套模型：

1. `ShaderAsset` 是语义资产身份。场景、材质和其他资产只引用 `AssetId<ShaderAsset>`，不得因目标后端、网格管线、光追管线或编译状态引入新的 Shader 资产类型。
2. Block 是 `ShaderAsset` 内的具体数据及其异步取得边界。Block 不等于磁盘文件、不等于二进制块，也不承诺数据必然落盘。
3. `ShaderSourceBlock` 是可由 Slang 编译取得数据的特殊 Block；`ShaderBlock` 是已经能够取得可用代码的 Block。两者都产生同一个语义对象 `Shader`，不得产生不同的 Shader 资产类型。
4. `ShaderAsset` 不保证大数据驻留，因此访问 Block 可以异步；`Shader` 保证它引用的数据已经可同步使用。
5. `ShaderAsset` 与 `Shader` 的两级语义保持正交：前者表达身份、持久化和数据取得，后者表达客户端正在使用的直接 Slang 对象和代码；任何一方都不得为了保活字节而持有另一方。
6. 已发布的 `Shader` 及其托管代码不可变；版本变化创建另一个完整 `Shader` 并在既有消费边界交换外层引用。`Shader`、材质、管线描述和其他托管消费者不得因代码驻留而实现或传播 `IDisposable`。
7. 通用页级 COW、全局页池、页引用计数和 `PageRange.Dispose` 不是 Shader 驻留需求。普通 Shader 代码使用一个最终托管数组；只有确有尺寸、范围访问或流式读取需要时，才使用多个托管数组作为内部布局。
8. 已发布的托管数组永不归还池、永不被其他内容覆写；最后一个普通引用消失后由 GC 回收。arena 只服务读取、解压、编码、上传等明确不会逃逸且有结束边界的内部临时数据。
9. Slang 产生的 `ISlangBlob` 及其必要所有者由 `Shader` 直接引用，不得为了形式统一先复制进页或自定义内存包装。保存代码、编译代码和 blob 不要求统一成同一个新类型。
10. 用户机器上的 Slang 编译是正式路径，不是编辑器专属路径，也不是异常回退。预编译代码、Slang 中间数据、源代码编译和缓存命中只是 Block 取得数据的不同方式。
11. Slang 是 Shader 语义、入口、类型、布局、属性和反射的唯一权威。引擎内不得定义、序列化或维护第二套 Shader 反射模型。
12. `Shader` 不能被定义为“单个 target + 单个特化组合的编译结果”。它可以引用一个或多个目标、入口及特化结果；它的定义只取决于同步可用保证。
13. 最终字节码可以保存，也可以只存在于内存。是否落盘不改变 Block 和 `Shader` 的语义，也不得被写成“字节码只能是缓存”或“ShaderAsset 只能是编译输入”。
14. 对外代码视图继续使用 `ReadOnlyMemory<byte>`、`ReadOnlySequence<byte>` 等 BCL 类型；不得为生命周期再引入 `...Content`、带 release 委托的共享容器、`UnmanagedReadOnlyMemory`、`SlangBlobMemory` 或等价包装。
15. 正确性不得依赖某一种 GC。Satori 可以作为 self-contained 客户端运行时的性能选项，并影响内部连续数组阈值等调优；标准 CoreCLR 上必须保持相同语义和内存安全。
16. 当前所有不符合本规范的类型、字段、兼容读取、转换层、测试和文档必须删除；不得换名后保留同一套镜像数据。

以下两个等式明确无效，不得再作为设计依据：

```text
ShaderAsset = Slang 编译输入
Shader      = 一个具体 target + 特化组合
```

正确关系是：

```text
ShaderAsset = 一个 Shader 的语义身份 + 若干具体 Block
Shader      = 通过普通托管引用保持所需数据可用的使用对象
```

## 2. 名词边界

| 概念 | 唯一含义 | 不代表什么 |
|---|---|---|
| `ShaderAsset` | Shader 的语义身份和 Block 容器 | 某个后端代码、某个特化结果、所有数据均已加载 |
| Block | 一类具体数据以及取得这类数据的异步边界 | 文件、磁盘、二进制、缓存 |
| `ShaderSourceBlock` | 可供 Slang 取得源代码或 Slang 自身中间数据并执行编译的 Block | 只在导入器里有效的路径清单 |
| `ShaderBlock` | 可取得目标代码并建立直接 Slang 反射访问的 Block | 自定义反射快照、固定后端枚举 |
| `Shader` | 以普通托管引用持有直接 Slang 对象和实际代码的不可变同步使用对象 | 全资产副本、单目标结果类型、后台加载入口、可原地更新的容器、需要调用方释放的内存租约 |
| 最终托管代码 | 一个 `byte[]`，或确有必要时由多个不可变托管数组构成的 `ReadOnlySequence<byte>` | arena 槽、可归还数组、通用页级 COW 数据 |
| 内部临时内存 | 一次读取、解压、编码或上传操作内不会逃逸的缓冲区 | `Shader`、Asset 或异步后端任务的长期 backing |
| Slang 对象 | `ISession`、`IModule`、`IEntryPoint`、`IComponentType`、`ShaderReflection`、`EntryPointReflection`、`VariableReflection`、`VariableLayoutReflection`、`TypeReflection`、`TypeLayoutReflection`、`ISlangBlob` 等 | 可被引擎 DTO 替代的数据源 |
| 后端派生数据 | D3D12 root signature、Vulkan pipeline layout、描述符写入表等后端私有结果 | 通用 Shader 反射模型、可回写的 Shader 真相来源 |

`IModule` 等名称来自 Slang 公共 API，不受引擎自定义标识符禁用词约束。引擎自己的新类型和成员仍须遵守仓库 `AGENTS.md` 的命名要求。

## 3. 必须长期成立的不变量

### 3.1 语义资产只有一种

- 场景、材质、组件和其他资产只能保存 `AssetId<ShaderAsset>`。
- 禁止 `DxilShaderAsset`、`SpirvShaderAsset`、`ClusterShaderAsset`、`RayTracingShaderAsset` 等按具体管线拆出的语义资产。
- 入口名可以是语义引用的一部分；入口阶段必须从 Slang 的 `EntryPointReflection` / `SlangStage` 得到，不再维护引擎 `ShaderStage` 副本。
- 后端、特化、入口组合属于 Block 中的具体数据，不改变资产引用类型。

### 3.2 Block 是异步数据边界

- Block 可以从资产文件、网络、内容缓存、Slang 编译、内存生成或其他数据源取得内容。
- Block 可以完全不落盘；也可以保存源代码、Slang 不透明数据、最终代码或这些数据的任意必要组合。
- 编译必须由显式异步 Block 操作触发。`Shader` 的普通查询、RHI 的热路径和属性 getter 不得隐式读取 Asset 或启动编译。
- 当前 `ShaderSourceBlock` 就承担“等待 Slang 编译的数据 Block”职责；禁止再引入名为 `Pending...`、`...RequestData` 或相同含义的新 Shader 数据模型。
- `ShaderSourceBlock` 与 `ShaderBlock` 都能取得同一种 `Shader`。这与现有一个语义 Asset 内多个 Block 可产生同一种使用对象的设计一致。

### 3.3 驻留保证只属于 `Shader`

- `ShaderAsset`、`ShaderSourceBlock`、`ShaderBlock` 可以只持有描述和数据源，访问大数据可以异步。
- `Shader` 构造完成后，它引用的代码、Slang 所有者对象和直接反射句柄必须保持有效，普通访问必须同步。
- `Shader` 不引用的数据无需为了“完整”而加载或复制。
- `ShaderAsset`、Block、`AssetFile` 和加载缓存全部不可达后，已有 `Shader` 仍必须继续有效；不得通过反向引用整个 Asset 实现这一点。
- `Shader` 是普通 GC 对象，不实现 `IDisposable`。调用方丢弃最后一个引用就是结束其托管生命周期的唯一动作。
- 已发布的 `Shader` 实例不可变，不提供原地改写其内容的 `Shader.Update`。更新必须先完整创建一个新的 `Shader`，再由已经负责保存当前 Shader 的消费方或缓存边界一次交换那个 `Shader` 引用；不得仅为交换而增加新的全局包装类型。
- 已开始的 pipeline 创建和其他异步消费者直接捕获本次使用的 `Shader`。更新后它们继续使用旧实例，最后一个普通引用消失后旧实例及其托管代码可由 GC 回收。
- 引擎不得用 release 委托、引用计数、finalizer 或要求上层 `Dispose` 来模拟托管代码的可达性。Slang 绑定自身的 native interop 清理由绑定层封装，不能传播成 `Shader` 的公共释放协议。

### 3.4 最终代码存储、复制与 GC

按来源执行以下规则：

| 数据来源 | `Shader` 的持有方式 | 禁止行为 |
|---|---|---|
| 压缩或尚未物化的 Asset chunk | 根据已知解码长度直接写入最终托管数组；确需分段时直接写入最终托管数组段 | 解码到池或 arena 后再复制到另一份长期存储 |
| 已经是独立不可变 GC backing 的代码 | 保存普通数组、`ReadOnlyMemory<byte>` 或 `ReadOnlySequence<byte>` 引用 | 为“统一所有权”再套共享容器或 release 回调 |
| 借用池、arena、文件映射或更大 backing 的切片 | 在异步创建边界内复制到独立最终存储；若来源自身已经具有安全的普通 GC 生命周期则直接共享 | 让可归还、可覆写或依赖外部关闭的数据逃逸到 `Shader` |
| Slang 新产生的代码 | 直接持有 `ISlangBlob` 及其必要 Slang 所有者 | 仅为统一形式先复制进页，再复制给 RHI |
| 必须连续而现有最终数据分段 | 只在实际原生 API 边界建立一次最终连续表示 | 同时长期保留分段源和连续副本 |
| 后端创建任务 | 任务捕获本次代码存储和 Slang 所有者直到创建结束 | 只借裸指针，随后允许有关托管引用消失 |
| 后端原生结果 | 由对应 pipeline/layout 对象持有 | 回写 `Shader`，或形成第二套通用反射树 |

零复制不是高于生命周期和内存占用的绝对目标。复制符合下列任一条件时是正确行为：

- 把可归还、可覆写或依赖外部关闭的借用数据变成独立最终存储；
- 避免一个很小的有效范围长期保活远大的 backing；
- 满足已经证明必须连续的原生 API；
- 直接从压缩输入产生唯一的最终表示。

复制以后不得同时长期保留旧表示。性能验收记录复制字节数、保活放大和峰值内存，而不是把“复制次数必须为零”写成脱离上下文的完成条件。

普通 Shader 代码整体生成、发布后不可变、更新时整体替换，因此不需要页级 COW。编译器不会在旧目标代码上做受支持的局部写入，新版本本来就会产生一份完整结果；旧消费者只需继续引用旧 `Shader`。这里没有 COW 能省掉的语义复制，反而会增加分段/共享元数据、写屏障、间接访问和保活放大；若再采用手工页管理还会增加引用计数。因此对 Shader 而言它是伪需求。存储布局规则如下：

1. 默认使用一个实际长度的 `byte[]`，并通过单段 `ReadOnlySequence<byte>` 或 `ReadOnlyMemory<byte>` 暴露。
2. 只有连续数组导致明确的尺寸、工作集、范围访问或流式读取问题时，才使用多个不可变托管数组；这些数组不是全局 arena 的可复用槽。
3. 分段只是一种内部布局，不改变 chunk 的语义身份、驻留单位和 GC 生命周期。
4. `ReadOnlySequence<byte>` 是现有公共代码视图，不因内部存储重构而全量替换。
5. Shader 路径不得依赖 `PageArena`、`PageBuffer`、`PageRange`、页引用计数或通用页级 COW；本次资产重构移除这套通用 CPU 页设施；业务几何页和纹理 tile 的语义保持。

Satori 的 region GC 可以让较大的单数组和不可变版本图更高效，但只影响内部阈值。实现必须满足：

- 标准 CoreCLR 与 Satori 上的公共行为完全一致；
- 不通过公共 API 暴露当前 GC、页大小或数组分段；
- Satori 只能由固定版本、固定校验值的 self-contained runtime pack 接入，不得覆盖系统运行时；
- 不会逃逸且高周转的临时数据仍可使用内部 arena，不能因为 Satori 较快就让任意临时分配进入长期对象图。

### 3.5 通用 Asset 数据的存储分界

这套结论属于 Asset 系统，不是 Shader 特例。存储选择的第一分界是数据来源和语义，而不是先拿固定页大小套所有内容：

| 情况 | 默认处理 |
|---|---|
| 有 tile、mip、LOD、几何范围、独立 Block 等业务边界 | 按业务单位独立取得和回收；内存页不能取代业务单位 |
| 已经是独立、不可变、大小匹配的 GC 对象 | 直接共享普通引用，不复制 |
| 来自池、arena、可关闭映射、复用 I/O 缓冲区或其他可覆写来源 | 在逃逸前一次复制到最终 GC 对象；安全性不由大小阈值决定 |
| 只使用一个远大 GC backing 的小范围 | 根据复制字节数与被额外保活字节数决定；超过内部保活放大阈值时复制成实际长度数组 |
| 大型但整体生成、整体替换的不可分值 | 仍以一个最终数组为基线；只有实测连续分配造成峰值或停顿问题时才改为多个不可变数组 |
| 大型、无法按语义拆分、频繁局部修改且多个旧版本同时存活 | 才允许为该具体数据结构评估持久化分块或 COW；不得把它提升成所有 Asset 的基础生命周期 |

因此不存在“低于某个大小才安全复制、高于该大小就必须进页”的公共规则。大小阈值只优化两个已经安全的选择：是否切断过大 backing，以及最终值是否分段。阈值必须满足：

- 是内部实现参数，不进入 Asset schema、公共 API 或语义身份；
- 由真实 Asset 分布、普通 CoreCLR、可选 Satori 和纯手工 native arena 的同口径测试决定；
- 同时考察吞吐、p99/最大停顿、分配量、复制字节数、峰值工作集和保活放大，不能只比较单次分配速度；
- 修改阈值不改变调用方生命周期，也不要求调用方 `Dispose`；
- 没有数据证明时选择实际长度的普通 GC 数组，而不是预先建设全局页系统。

2026-09-29 的综合基准已经补齐仓库真实 Shader、Mesh、Texture、文档，以及代表性音频、网络缓存、小对象、1 MiB、混合 4 MiB、版本并存和 churn 分布。结果不支持设置一个“超过 N 字节就自动切成 64 KiB 页”的通用阈值：普通整体发布中，分段没有稳定的延迟、停顿或工作集综合优势；在 Satori 上，实际长度数组的普通场景 p50 几何平均比每轮申请/释放的 native arena 还低约 31%，64 KiB 分段只低约 14%，且会引入数组、节点和遍历分支。当前通用分段阈值因此等同于关闭；出现明确业务范围、映射限制或经实测的连续分配问题时，具体数据结构再单独决定。

基准同时确认了两个不能混淆的边界：

1. 预热后复用同一批 native block、每轮只重置 bump 指针，是纯手工 arena 的理想吞吐上界；Satori 实际长度数组的普通场景 p50 仍约慢 31%，p99 约慢 81%。但该上界要求整批数据共享同一回收时刻，不能独立随普通对象引用回收，也尚未计入把 native 指针安全暴露给托管 API 所需的所有权包装。它适合已有结束边界的解码、编码、上传和构建临时数据，不适合成为已发布 Asset 的生命周期。
2. 4 MiB 不可分值、12 次 4 KiB 局部修改、同时保留 4 个版本时，持久化 64 KiB 分段确有收益：Satori 下 p50 为 7.39 ms，实际长度数组为 15.91 ms，复用 arena 为 9.42 ms；分段只分配 4.75 MiB payload，而整体数组和 arena 都写入 52 MiB。这个结果证明 COW 只对该特定工作负载是真需求，不证明 Shader 或普通 Asset 需要通用 COW。

详细方法和结果见第 14.4 节。这里据此固定默认规则：普通不可变发布值使用实际长度数组；业务分页按业务单位；内部临时批次可用 arena；只有同时满足“大型、不可按语义拆分、频繁局部修改、多个旧版本并存”并取得本数据结构的实测收益，才允许持久化分段/COW。

### 3.5.1 统一字节缓存

数据复用按 [Asset 数据共享与修改](../Assets/Asset-Sharing-and-Writes.md) 执行：不可变文件来源、chunk 和范围确定字节缓存键，所有数据语义共用整数 LRU 数组及 CPU 字节预算，任务表在实际读取期间保留共同任务；使用对象通过普通强引用保留结果。不同内容版本不能因路径、页号或大小相同而误共享。

普通属性、集合和已发布字节不使用 PageArena、PageBuffer、PageList、PageRange 或通用页 COW。文件读取直接物化最终数组，移除每文件 ChunkRequestScheduler 及其长期解码缓存；全局 AssetByteCache 按不可变字节来源缓存最终数组，预算同时计入数组和动态索引。文件句柄和 GPU 完成边界仍按真实操作期限管理。

### 3.6 Slang 是唯一反射权威

- 所有 Shader 入口、阶段、参数、资源类别、绑定、类型、字段偏移、数组步长、属性和特化信息均从直接 Slang 对象读取。
- `ShaderReflection` 等反射对象是非独立所有者；`Shader` 必须同时持有使它们有效的 `ISession`、`IModule`、`IEntryPoint`、`IComponentType` 或 Slang 文档要求的其他接口。
- 不得把 Slang 反射复制为引擎 record/class，再让 Render、Material 或 RHI 查询副本。
- 不得序列化一棵引擎反射树。
- 不得以“避免热路径调用 Slang”为理由保存通用镜像。热路径需要的数据应在创建 pipeline/material 时直接由 Slang 派生为该消费者的具体原生数据。
- 消费者派生数据只能服务一个明确消费者。例如 D3D12 root parameter 表或材质常量写入布局可以存在，但它们不能提供通用反射查询，不能成为 `Shader` 的数据字段，也不能写回 Shader Asset。

### 3.7 通用 Asset/Block 规则与 Mesh 现状

本规范使用的是全引擎 Asset/Block 规则，不是只为 Shader 发明的例外。

当前 Mesh 方向已经采用正确结构：

```text
MeshAsset
  +-- MeshBlock
  +-- MeshMaterialsBlock
  +-- ClusterMeshBlock
```

- 当前源码中不存在 `ClusterMeshAsset`；`MeshAsset.cs`、`MeshBlock.cs`、`MeshMaterialsBlock.cs`、`ClusterMeshBlock.cs` 的语义拆分应保留。
- Scene 始终引用 `AssetId<MeshAsset>`；Cluster 只是同一 Mesh 语义下的一种具体 Block。
- `ClusterMeshBlock.CreateAsync`、`LoadPagesAsync` 和 `OpenPayloadSourceAsync` 展示的是 Block 显式异步取得、使用对象同步消费和语义几何范围按需加载；它们不证明 Shader 代码需要通用内存页、页级 COW 或手工生命周期。
- Texture tile、Mesh 几何范围、LOD 等是业务定义的语义单位。通用内存页不能替代这些单位，也不能从其他资源存在分页反推出 Shader 必须分页。
- Shader 重构必须复用这套思想，不能创建按后端或管线命名的 Shader Asset。
- 以后若出现 `ClusterMeshAsset`、`DxilShaderAsset` 或相同语义的新类型，直接删除并把具体数据移入对应语义 Asset 的 Block。

### 3.8 Shader 配置资产的边界

当前 `ClusterShadersAsset` 和 `VirtualShadowMapShadersAsset` 不是单个 Shader 的替代身份；它们目前保存“管线角色到 `ShaderRef`”的配置集合。因此它们可以保留，但必须满足：

- 每个实际 Shader 仍通过 `AssetId<ShaderAsset>` 引用；
- 配置 Block 只保存角色与语义引用，不保存代码、Slang 反射副本或后端布局；
- `ClusterShadersBlock.CreateAsync`、`VirtualShadowMapShadersBlock.CreateAsync` 取得的是由 `Shader` 锚定的直接 Slang 入口；
- 如果这些 Asset 以后开始承载实际 Shader 数据，就必须删除该数据并移回所引用 `ShaderAsset` 的 Block。

编译目标和特化由真正创建渲染数据的调用边界显式提供，使用 Slang 类型；加载行为根据可用内容选择 `ShaderBlock` 或 `ShaderSourceBlock`。当前内部 `ShaderRef.LoadAsync` 已接收这些参数并检查 Block 是否存在；应用材质加载路径采用相同的 Block 选择规则；应用 Shader 缓存按资产保留当前成功版本，新的失败保留先前结果。不得让 `AssetLoader` 猜设备目标，也不得重新引入自定义 target 请求 record。

### 3.9 命名约束

- 本次重写或新增的引擎标识符必须遵守仓库 `AGENTS.md` 的禁用词要求，按“是什么数据”或“是什么行为”命名。
- Slang 官方类型名原样使用，不为规避禁用词再包一层引擎类型。
- `ClusterGeometryContracts.cs` 在迁移直接 Slang 属性时必须按其中实际数据/行为改名；不得延续空洞的 `Contract` 命名。
- `CookedClusterShaderContractTests`、`D3D12TestShaderProgram`、`VulkanTestShaderProgram`、`CookedShaderTestProgram` 等在重写测试 helper 时按实际行为改名；如果只验证旧模型则直接删除。

### 3.10 校验边界

- Slang 已建立的入口、类型、布局和属性不由引擎再复制一份并重复校验。
- Slang 调用结果和 diagnostics 决定编译/反射是否成功；引擎只校验自身真正拥有的边界，例如 Asset 身份、chunk 完整性和原生 API 必需条件。
- 可关闭的诊断优先进入引擎统一 validation 层，不在每次 Shader 普通访问中加入重复遍历。
- 当前针对 `ShaderMetadata`、标量布局副本和实例属性副本的校验随这些 DTO 一起删除，不能搬到新包装中。

## 4. 目标数据流

### 4.1 语义和 Block 拓扑

```text
场景 / 材质 / 其他资产
          |
          | AssetId<ShaderAsset>
          v
     ShaderAsset                       同一个语义身份
       |       |
       |       +-- ShaderBlock         已有代码或可直接取得代码
       |
       +---------- ShaderSourceBlock   源代码或 Slang 自身中间数据
                         |
                         | 显式异步 Slang 编译
                         v
                    直接 Slang 对象 + 代码存储
                               |
             ShaderBlock 取得 --+-- ShaderSourceBlock 编译取得
                               v
                            Shader
                 同步访问；只保留实际引用的数据
```

这里不存在第三种 Shader 数据通路。导入器、用户机器编译和缓存命中都必须落在上述 Block 取得行为中。

### 4.2 已有代码路径

```text
ShaderRef.LoadAsync
  -> 打开 ShaderAsset
  -> 根据调用方提供的直接 Slang 目标/特化和 Block 可用性选择取得路径
  -> 取得 ShaderBlock
  -> ShaderBlock.CreateAsync
     -> 异步取得 Slang 不透明数据和必要代码 chunk
     -> 将压缩/借用输入直接物化为唯一最终托管数组，或保留已有独立 GC backing
     -> 用 Slang 恢复/建立 IComponentType 与直接反射句柄
     -> Shader 保存普通托管引用或直接 Slang blob
     -> 返回同步可用且无需 Dispose 的 Shader
  -> ShaderAsset、Block 和文件引用可以全部消失
```

最终目标代码可以直接保存在 `ShaderBlock` 中。问题不是“能否保存字节码”，而是该 Block 必须同时能够建立有效的直接 Slang 反射访问；仅有无法恢复 Slang 语义的裸后端字节码不足以创建完整 `Shader`。

可采用的恢复方式只有两类：

1. 使用 Slang 官方 API 能重新打开的 Slang 不透明数据，并从它建立直接 Slang 对象；
2. 使用同一 `ShaderAsset` 内 `ShaderSourceBlock` 的数据重新建立 Slang 对象，同时复用已经保存的目标代码。

选择哪一种必须由实际 Slang API 往返测试证明。当前 C# 绑定中一次 `GetContainerCode` 失败不能推出 Slang 不支持容器；同样，在没有成功往返测试前也不得把该容器写成架构前提。

### 4.3 用户机器编译路径

```text
ShaderRef.LoadAsync 或显式 Shader 加载行为
  -> 打开 ShaderAsset
  -> 目标代码不存在、不适用，或调用方明确要求编译
  -> 取得 ShaderSourceBlock
  -> 显式异步取得源代码 / include / Slang 中间数据
  -> 创建 ISession，加载 IModule，取得 IEntryPoint
  -> compose / specialize / link / codegen
  -> Shader 直接保留 IComponentType、反射句柄和 ISlangBlob
  -> 可选：由正常 Block 保存行为把结果写入 ShaderBlock
  -> 返回驻留完成的 Shader
```

编译结果是否随后保存，不影响本次返回的 `Shader`。保存行为从当前 blob 或托管代码读取；不得为了保存改变本次 `Shader` 的 backing，也不得要求 `Shader` 反向持有 Asset。

### 4.4 版本替换路径

```text
旧 Shader（不可变，继续可用）
  + 异步取得或编译替代内容
  + 完整创建包含新 Slang 所有者与代码的新 Shader
  + 在既有消费/缓存边界一次交换一个 Shader 引用
  + 通知使用方重建其具体 pipeline/material 数据
  + 已开始的任务继续持有旧 Shader
  + 最后一个普通引用消失后由 GC 回收旧 Shader 的托管数据
```

版本替换不得逐字段改写旧 `Shader`，也不得在 `Shader` 内增加 `Content`、`SharedData` 或同义的可交换内层对象。目标结构中不再有反射 DTO。更新不是对已发布代码执行 COW 写入，而是创建另一个完整 `Shader` 后交换外层引用。

## 5. `ShaderSourceBlock` 的职责

`ShaderSourceBlock` 保留，但必须从“导入器路径和自定义选择 DTO”重构为真正可取得编译输入的 Block。

必须满足：

- 二进制 Asset 中若要求用户机器编译，就必须包含或引用实际可取得的源代码、include/import 内容或 Slang 自身可重新加载的数据；只保存编辑器机器上的相对路径不构成可用 Block。
- include/import 解析交给 Slang。引擎可提供读取 Block 内容的 Slang 文件系统适配，但不得自行解析 Shader 语言依赖关系来建立另一套语义。
- 编译调用使用 Slang 的 `TargetDesc`、`CompileTarget`、profile、`SpecializationArg`、`IEntryPoint` 等直接类型。
- 当前 `.shader` 来源 schema 保存独立资产身份及 `ShaderSourceBlock.SourceNames`。入口由 Slang 从这些根源码发现并按声明顺序组成；target、profile、编译选项及特化由明确编译调用提供。若后续扩展持久构建设置，须先修订来源 schema 与本规范；不得恢复已删除的自定义入口、阶段或目标镜像，也不得把构建设置当作 Shader 反射数据。
- 当前 `ShaderEntrySelection`、`ShaderTarget` 和 `ShaderStage` 必须删除。入口发现与阶段来自 Slang；目标配置直接使用 Slang 类型。
- 当前 `SourceFiles` 若保留为文本构建输入，写入二进制 Block 时必须转换为可取得的 Block 内容或 Slang 不透明数据，不能继续依赖外部工作区文件。
- 当前 `CookProfile` 只能作为工具侧构建策略存在；它不得成为 Resources/Graphics/RHI 的 Shader 语义。

Block 上的行为应明确表达“创建 Shader”或“编译 Shader”，并返回驻留完成的 `Shader`。不得引入 `SlangUnitData`、`ShaderEntryData`、`ShaderTargetData`、`ShaderEntryCodeData` 或等价的新 record。

## 6. `ShaderBlock` 的职责

`ShaderBlock` 保留，但其序列化内容只允许是：

- 通用 Asset 块元数据和 chunk 映射；
- Slang 官方产生的不透明数据；
- 目标 API 最终代码；
- 为取得这些数据所需的普通来源信息，但不是反射副本。

`ShaderBlock` 不再包含：

- `Programs` / `CookedShaderProgram`；
- `EntryPointAttributes`；
- `Reflections`；
- `EntryPointReflections`；
- `Metadata`；
- `EntryPointMetadata`；
- 任何入口、目标、类型、变量、布局、属性或资源绑定的引擎镜像。

`ShaderBlock.CreateAsync` 必须重新实现：

1. 在进入异步读取前取得这次操作所需的 Block 元数据和数据来源；并发编辑必须产生新的 Block 版本，不能原地改写本次读取正在使用的内容；
2. 异步取得所需 chunk。压缩或借用输入直接物化为实际长度的最终托管数组；来源已经是独立、不可变的 GC backing 时直接保留普通引用；
3. 使用直接 Slang API 建立必要的 Slang 所有者对象、入口与反射；Slang 生成的代码保留原始 `ISlangBlob`；
4. 用普通托管引用把直接 Slang 对象以及实际代码交给 `Shader`，不创建页引用、release 委托或统一内存包装；
5. 返回同步可用的 `Shader`。返回后不得要求 `ShaderAsset`、Block、`AssetFile`、读取流或加载缓存继续存活。

文件读取直接解码到最终 GC 数组，同一内容定义通过业务数据节点复用已有数组。已删除的通用 CPU 页容器和旧 Shader 对象图读取路径不得作为中间层恢复。外部已经存在的可覆写借用输入仍按第 3.4 节在逃逸前复制。

## 7. `Shader` 的目标形态

`Shader` 是使用对象，不是序列化格式。它只持有下列三类数据：

1. 直接 Slang 所有者接口：例如 `ISession`、`IModule`、`IEntryPoint`、`IComponentType`；
2. 直接 Slang 反射句柄：例如 `ShaderReflection`、`EntryPointReflection`、`VariableReflection`、`VariableLayoutReflection`、`TypeReflection`、`TypeLayoutReflection`；
3. 实际被引用的代码：独立不可变的托管数组及其 BCL 只读视图，或者 Slang 直接产生的 `ISlangBlob`。两种来源不要求包装成同一个新类型。

`Shader` 不再持有：

- `ImmutableList<ShaderProgram?> Programs`；
- `ShaderEntryPointAttribute`；
- `BackendReflection`；
- `ShaderEntryPointReflection`；
- `ShaderMetadata`；
- `ShaderEntryPointMetadata`；
- 由上述类型间接持有的材质布局、资源列表和实例属性副本。

查询 API 的规则：

- 查询返回直接 Slang 类型，或者返回 BCL 内存类型的只读视图。
- 入口按名称查找时最终返回 `IEntryPoint` / `EntryPointReflection`，阶段使用 `SlangStage`。
- 代码读取不得要求调用方取得自定义 `ShaderProgram` 或 `CookedShaderEntry`。
- 托管代码继续以 `ReadOnlyMemory<byte>` / `ReadOnlySequence<byte>` 暴露；Slang 代码直接以 `ISlangBlob` 暴露。不得为强行统一两者而增加 `ShaderContent`、`SharedData`、自定义 `MemoryManager<byte>`、`UnmanagedReadOnlyMemory`、`SlangBlobMemory` 或同义类型。
- 需要消费两种来源的内部调用点可以按实际来源分支或提供明确重载；这种差异只存在于代码取得边界，不得演化成一套新的生命周期协议。
- 当前 `ShaderEntry` record 必须删除。消费者需要同时保活 `Shader` 和直接 Slang 入口时，由消费者自己的具体使用对象分别持有两者；不得再引入一个全引擎通用的 Shader 入口镜像。

`Shader` 不实现 `IDisposable`，不增加 finalizer，也不提供原地更新。新版本是另一个完整 `Shader`；已开始的任务直接持有旧 `Shader`。旧实例及其托管代码在不可达后由 GC 回收，不存在 `_disposed`、可交换内层包装、页释放、release 回调或要求上层配对调用的释放路径。

## 8. Slang 绑定与依赖决策

### 8.1 删除绑定层自建模型

以下文件必须删除：

- `external/SlangShaderSharp/src/ShaderProgram.cs`
- `external/SlangShaderSharp/tests/ShaderProgramTests.cs`

不得把它们拆成其他文件或换名保留。`ShaderProgram`、`ShaderProgramLayout`、`ShaderEntryPoint`、`ShaderVariable`、`ShaderType`、`ShaderVariableLayout`、`ShaderTypeLayout` 整棵脱离 Slang 生命周期的对象图都不再存在。

### 8.2 使用官方 NuGet，不保留 fork

本次重构已经选择官方 `SlangShaderSharp` NuGet `2026.17.1`，并删除
`external/SlangShaderSharp` 子模块。选择依据不是 API 表面检查，而是直接行为验证：源代码与序列化
Slang 数据加载、入口发现、特化、链接、DXIL/SPIR-V 代码生成、直接反射、诊断、强制 GC、失败后继续编译、
D3D12/Vulkan pipeline 创建和异步创建期间保活在该轮记录中通过。随后同日审查两次重现组合测试的 COM finalizer 原生崩溃，见 V01；2026-10-01 将 Importer 全局会话交由进程级编译服务持有后，原组合加 GC 回归完成 64 项通过，完整 Asset 进程完成 240 项通过，详见 [本次验证](../Assets/Asset-Verification-2026-10-01.md)。

此前一组 `ComObject.Finalize -> Marshal.Release` 访问冲突的验证记录指出：引擎和测试对默认、可能共享的 source-generated
COM wrapper 主动调用 `ComObject.FinalRelease()`，随后运行时仍按自己的引用账本完成清理；删除上层
`FinalRelease` 后，当时的强制 GC 与后端行为测试通过。这个历史定位不能直接解释本轮 V01，也没有提供给官方包增加
`UniqueComInterfaceMarshaller` 或恢复手工释放的依据。当前规则保持：

- 引擎、测试、benchmark 和工具不得调用 Slang wrapper 的 `FinalRelease`、`Marshal.AddRef` 或
  `Marshal.Release`；
- 不得为了允许手工释放而把普通返回值普遍改成 `UniqueComInterfaceMarshaller`；`Unique` 改变 wrapper
  identity，并不是通用的 COM 所有权修复；
- `ISession` 拥有其加载的 module，`Shader` 同时保存 session、module、entry、linked component 和 blob
  的普通托管引用；非拥有反射句柄不得脱离这些所有者；
- 后续只有在官方包上存在可隔离复现、且确认属于 binding 而非调用方手工释放后，才能重新评估绑定补丁；
  重新引入 fork 是单独的依赖决策，不能由实现过程擅自完成；
- 即使以后需要 binding 修复，fork 中也不得存在 SomeEngine 语义、反射快照、Asset schema 或 RHI
  数据模型。

### 8.3 生命周期

`SlangImportLifetime` 必须删除。它当前用任意对象列表和逆序 `FinalReleaseComObject` 补偿不清晰的所有权，不能作为正式模型。

替代规则不是另建一个 Shader 生命周期容器，而是：

- 创建 Slang 对象的代码把直接 Slang 接口作为普通托管引用交给本次构造过程，成功后由最终 `Shader` 持有；不得先塞进任意对象袋或 Shader 专用生命周期包装。
- 非拥有反射句柄始终与使它有效的直接 Slang 所有者一起保持可达。
- 发布后的 Slang 接口必须通过普通托管可达性安全使用，并在不可达后由 binding/运行时完成 native 引用回收；`Shader`、Graphics 和 Render 不承担 `Release`/`Dispose` 传播。既有行为测试覆盖不替代 V01 的后续定位和回归。
- 构造尚未成功时，本次调用取得的 Slang wrapper 只需离开普通托管作用域；引擎层失败路径同样不得调用 `FinalRelease` 或建立手工逆序释放表。
- 引擎不得为 `Shader`、代码数组或页增加 finalizer。若候选 Slang binding 只有依赖每个上层调用方手工释放才能正确工作，则该 binding 在修复前不满足本规范；修复只能位于通用 binding 层，并用回收行为与长期压力测试证明。

## 9. Graphics、RHI 与 Render 的直接 Slang 边界

### 9.1 Graphics 公共描述

`SomeEngine.Graphics/Pipelines/Descriptions.cs` 以及 Ray Tracing、Work Graph、Indirect Commands、Descriptors 能力接口中的下列参数必须迁移：

- `ShaderProgram` -> 对应的直接 `IComponentType` / `ShaderReflection`，以及实际来源对应的 `ISlangBlob` 或 BCL 代码只读视图；
- `ShaderEntryPoint` -> `IEntryPoint` / `EntryPointReflection`；
- `ShaderVariable` -> `VariableReflection`；
- `ShaderVariableLayout` -> `VariableLayoutReflection`；
- `ShaderType` / `ShaderTypeLayout` -> `TypeReflection` / `TypeLayoutReflection`。

不要再定义一个“更适合 Graphics”的中间 Shader 模型，也不要用自定义联合内存类型强行统一 blob 与托管数组。异步原生对象创建必须直接捕获本次使用的代码引用和 Slang 所有者，直到创建结束。

### 9.2 D3D12 与 Vulkan

以下后端逻辑改为直接遍历 Slang：

- D3D12 pipeline、root signature、descriptor、stream output、ray tracing、work graph 与 state object 创建；
- Vulkan pipeline、pipeline layout、descriptor、mesh、ray tracing 与 stream output 创建。

允许保留的后端私有数据：

- 已创建的原生 pipeline/layout/root signature；
- 命令编码所需的紧凑后端索引或原生句柄表；
- 与某个原生对象同生命周期、只服务该对象的创建结果。

禁止保留的后端数据：

- 可被任意系统查询的通用 Shader 类型树；
- 复制 Slang 字段名、类型、布局和属性后长期驻留的反射表；
- 可与 Slang 结果独立变化的第二事实来源。

### 9.3 Render 与 Material

- `ClusterMaterialStages`、`ClusterPipelines`、`LinkedRasterPipeline`、`LinkedComputePipeline`、UI 与 Presentation 管线直接从 `Shader` 取得 Slang 入口和代码。
- `ClusterGeometryContracts.cs` 当前复制的入口属性改为 `FunctionReflection.GetAttribute` 等直接 Slang 查询。
- 材质资源绑定、标量布局和实例属性在材质/pipeline 创建的冷路径上直接从 `VariableLayoutReflection` / `TypeLayoutReflection` 派生。
- `SomeEngine.Render/Materials/ScalarLayout.cs` 可以作为材质写入所需的消费者具体数据保留，但必须从 Slang 直接构造；删除 `FromData(ShaderMaterialScalarLayout)`，不得序列化回 `Shader`。
- Game world 到 Rendering world 的既有边界不变；本次重构不得增加第三条 Shader 数据通路。

## 10. 当前代码的删除与重写清单

### 10.1 无条件删除

| 文件或类型 | 原因 |
|---|---|
| `external/SlangShaderSharp/src/ShaderProgram.cs` | 自建脱离式 Shader 反射和代码对象图 |
| `external/SlangShaderSharp/tests/ShaderProgramTests.cs` | 只验证被删除的内部模型 |
| `src/SomeEngine.Assets.Importers/Importers/SlangImportLifetime.cs` | 任意对象袋，掩盖所有权 |
| `src/SomeEngine.Assets.Importers/Importers/SlangBindingMap.cs` | 把 Slang 资源反射复制成引擎 DTO |
| `src/SomeEngine.Assets.Importers/Importers/SlangEntryMeta.cs` | 把 Slang 入口属性复制成引擎 DTO |
| `src/SomeEngine.Assets.Importers/Importers/SlangMaterialMeta.cs` | 构建并排序持久化 Shader 材质反射副本 |
| `src/SomeEngine.Resources/Shader.Fields.cs` | 全部字段属于旧镜像；直接所有者字段在 `Shader.cs` 内重新建立 |
| `CookedShaderEntry` | 绑定 `ShaderProgram` / `ShaderEntryPoint` 的旧边界 |
| `CookedShaderProgram` | 自定义后端代码目录 |
| `BackendReflection` | 自定义后端资源反射 |
| `ShaderEntryPointAttribute` | Slang 属性副本 |
| `ShaderEntryPointMetadata` | Slang 入口元数据副本 |
| `ShaderEntryPointReflection` | Slang 入口反射副本 |
| `ShaderReflectionData` | 通用反射副本 |
| `ShaderResourceReflection` | 资源反射副本 |
| `ShaderQualifiers` | Slang 资源限定信息的自定义枚举副本 |
| `AccessEffect` | Slang 资源访问效果的自定义枚举副本 |
| `TextureSampleType` | Slang 纹理类型信息的自定义枚举副本 |
| `StorageFormat` | Slang image format 的自定义枚举副本 |
| `ShaderMetadata` | 聚合的 Shader 元数据副本 |
| `ShaderMaterialBinding` | 材质绑定反射副本 |
| `ShaderMaterialScalarField` | Slang 布局字段副本 |
| `ShaderMaterialScalarLayout` | Slang 布局副本 |
| `ShaderInstanceProperty` | 从 Shader 属性复制出的持久化表 |
| `ShaderEntrySelection` | 自定义入口选择模型 |
| `ShaderEntry` | 保存入口名和自定义阶段的通用包装 |
| `SlangShaderCookProfile` | 用字符串重复表达 Slang target/profile；构建行为改用直接 Slang 类型 |
| 引擎 `ShaderTarget` | 自定义目标枚举；改用 Slang 目标类型 |
| 引擎 `ShaderStage` | 自定义阶段枚举；改用 `SlangStage` |

若上述类型与其他非 Shader 用途同处一个文件，只删除类型和生成代码入口，不得因文件合并而保留类型。

### 10.2 保留身份，完全重写内容

| 位置 | 目标 |
|---|---|
| `ShaderAsset.cs` | 继续只表达语义身份和 Block 容器；不得加入编译结果字段 |
| `ShaderSourceBlock.cs` | 成为可异步取得真实 Slang 输入并编译的 Block |
| `ShaderBlock.cs` | 保存/取得不透明 Slang 数据和目标代码；直接创建驻留 `Shader` |
| `Shader.cs` | 只持有直接 Slang 所有者、反射句柄和实际代码；不实现 `IDisposable`，不增加 finalizer、页引用或 release 回调 |
| `ShaderRef.cs` | 保留 Asset 身份与必要入口名；删除阶段镜像，显式异步取得 `Shader` |
| `ShaderEntry.cs` | 删除；具体消费者分别持有 `Shader` 和直接 Slang 入口 |
| `ClusterShadersBlock.cs` / `VirtualShadowMapShadersBlock.cs` | 保留角色到 `ShaderRef` 的配置；把解析结果改为由 `Shader` 锚定的直接 Slang 入口 |
| `SlangShaderDefinition.cs` | 只负责构建边界和 Block 行为编排；不再建立反射 DTO |
| `SlangShaderImporter.cs` | 复用同一 Slang 编译行为并可选保存 Block；不得成为唯一可编译位置 |
| `Descriptions.cs` 及 Graphics Shader 参数 | 接受直接 Slang 类型，以及实际来源对应的 `ISlangBlob` 或 BCL 代码只读视图 |
| D3D12/Vulkan Shader 消费文件 | 从 Slang 直接派生原生对象所需数据 |
| Render/Cluster/UI/Presentation Shader 消费文件 | 使用 `Shader` 的直接 Slang 入口和代码，不再调用 `RequireEntry` 返回旧包装 |
| Shader 测试与 benchmark helper | 使用直接 Slang 行为并按具体用途改名；只服务旧模型的 helper 删除 |

### 10.3 当前实现中必须消失的具体行为

- `ShaderBlock.CreateCoreAsync` 中的 `ShaderProgram.Read`。
- `Shader.Create(IEnumerable<ShaderProgram>, ...)`。
- 原地修改已发布实例的 `Shader.Update`；版本变化改为创建新 `Shader` 并在既有消费边界交换外层引用。
- `Shader.TryProgram`。
- 返回 `ShaderProgram` / `ShaderEntryPoint` 的 `Shader.TryEntry`。
- 返回 `CookedShaderEntry` 的 `Shader.RequireEntry`。
- 查询 `ShaderEntryPointReflection` 副本的 `Shader.TryReflection`。
- `Shader.ValidateScalarLayouts`、`ValidateInstanceProperties` 及对镜像不变量的校验。
- `SlangShaderImporter` 中 `CreateMetadata`、`AddBackendReflections`、`CollectModuleMaterialMetadata` 和所有 DTO 填充行为。
- Asset 保存时写入自定义反射和材质元数据。
- RHI 从 `ShaderProgramLayout` 等脱离对象创建原生布局。

## 11. Schema 与内容迁移

这是破坏性 schema 重构，不提供旧格式兼容读取。

1. 实施破坏性字段变更时，从实施分支上 `ShaderBlock.Version` 的实际当前值再增加一次；不得照抄本规范曾记录的历史数值。`ShaderAsset.Version` 随之变化。
2. `SlangShaderImporter.ImporterVersion` 增加，确保所有生成结果失效。
3. 删除旧 `ShaderBlock` 字段，不保留 `[Obsolete]`、别名、兼容 reader 或旧数据转换器。
4. 删除旧 `ShaderProgram` blob 解码路径。
5. 所有 `.shader.asset` 重新构建；旧生成文件和旧缓存直接丢弃。
6. `.shader` 文本若仍含 `Targets`、`SelectedEntries` 等旧字段，按新 Slang 直接输入格式一次性迁移；不在引擎中长期保留双格式 reader。
7. 内容仓库、示例和测试 fixture 全部在同一重构中更新，主分支最终不得依赖旧产物。

迁移允许在开发分支的连续提交中临时出现未迁完的调用点，但合入主分支时只能有一条新路径，不能用适配器让新旧模型长期共存。

## 12. 文档处理

本规范是当前 Shader Asset、驻留、编译和反射边界的唯一总规范。Asset 重构原则与主题页遵循这些规则；API 使用文档说明当前可调用入口，并链接尚未满足规范的实现差距。下列文档不得继续保留冲突结论：

| 文档 | 处理 |
|---|---|
| `wiki/architecture/shader/Detached-Reflection.md` | 删除；其核心设计被明确禁止 |
| `wiki/architecture/shader/Shader-Build.md` | 重写；删除“只在导入/烘焙编译、用户机器永不编译”结论 |
| `wiki/architecture/shader/Shader-Composition.md` / `User-Stage-Interfaces.md` | 把“build 阶段”改为显式 Slang 编译边界；该边界可以位于用户机器 |
| `wiki/architecture/shader/Shader-Resource-Effects.md` | 明确资源使用事实来自当前直接 Slang 反射，不来自保存的引擎表 |
| `wiki/architecture/shader/Geometry-Invocation-Contract.md` | 保留行为规则但按实际含义改名，更新关联链接 |
| `wiki/architecture/Assets/Asset-Shaders.md` | 重写为本规范的 Asset/Block/Shader 语义 |
| `wiki/architecture/RHI/Core/Slang-S-is-the-sole-shader-authority.md` | 保留标题但重写为直接 Slang 对象，不再认可 detached reflection |
| `wiki/architecture/RHI/Core/Pipeline-descriptions-and-immutable-identity.md` | 删除 cooked/detached 输入，改为直接 Slang 和驻留代码输入 |
| `wiki/architecture/RHI/Descriptors/Slang-derived-binding-and-direct-native-placement.md` | 删除镜像反射输入，改为直接 Slang 派生 |
| `wiki/architecture/RHI/Descriptors/Parameter-binding-inputs.md` | 参数布局改为直接 `VariableLayoutReflection`，并说明其 Slang 所有者 |
| `wiki/architecture/RHI/Backends/D3D12/Native-root-signatures-and-Pipelines.md` | 删除“不保留 live Slang handle”结论，按 pipeline 创建所需生命周期重写 |
| `wiki/architecture/RHI/Backends/Vulkan/Binding-ABI.md` | 删除 detached reflection，改为直接 Slang 派生的后端私有布局 |
| `wiki/architecture/RHI/Index.md` | 删除 `Detached-Reflection` 链接，链接本规范 |
| `wiki/architecture/shader/Slang-Authority.md` | 与本规范合并或仅保留链接，不能重复维护规则 |
| `docs/ShaderSources.md` | 更新用户机器编译、Block 数据来源和新 `.shader` 格式 |
| `wiki/architecture/Assets/Asset-Rebuild-Execution.md` / `Asset-Rebuild-Inventory.md` | 只保留当前职责与结果导航；被替代的架构规则从现行页面删除，由 Git 历史保存 |
| `wiki/architecture/Assets/Asset-Loading-and-Lifetime.md` / `Asset-Sharing-and-Writes.md` | 删除“所有已发布数据都通过页引用计数或通用 COW 管理”的结论；普通不可变 GC 对象是默认，COW 只允许服务第 3.5 节的特定数据 |
| `wiki/architecture/Assets/Asset-File-IO.md` | 把池、页和 arena 限定为内部 I/O 实现；可归还或依赖文件的数据不得逃逸到使用对象 |
| `wiki/architecture/Assets/Asset-Objects.md` / `Asset-Responsibilities.md` / `Asset-Object-Identity.md` | 明确语义 Asset、Block 和使用对象与 backing 布局正交，不把 `Dispose`、页大小或 GC 选择写进语义模型 |
| `wiki/architecture/Assets/Asset-Rebuild-Principles.md` / `Asset-Rebuild-Tasks.md` / `Asset-Verification-2026-09-16.md` / `Asset-Verification-2026-09-20.md` | 删除把全局页池、页级 COW 或零复制当作统一完成条件的段落，并按第 3.5、14.4 节的综合成本验证重写 |
| 其他出现旧类型或“shipping 不编译”的文档 | 同步删除或重写 |

被替代的架构规则不得以“历史设计”名义继续作为现行规范，旧规则正文由 Git 历史保存。按日期记录的测试事实、执行范围、任务完成记录和审查证据可以保留，但必须明确适用版本与范围，不提供当前 API 示例，也不覆盖较晚的失败结果或未关闭项。现行导航链接规范、API 说明及带日期的证据，避免重复维护规则。

## 13. 实施顺序

### 阶段 A：证明 Slang 直接路径

1. 针对官方 NuGet 验证：源代码编译、入口发现、特化、链接、目标代码、直接反射、诊断、GC 可达期间的有效性、不可达后的 native 引用回收和失败后继续编译。
2. 验证采用的 Slang 不透明数据能否在新 session 中往返并重新建立所需直接反射。
3. 验证 `ISlangBlob`、`IComponentType` 和反射句柄的真实生命周期。
4. 已根据结果选择官方 NuGet 并删除 fork；不得设计自定义容器或手工 COM 释放补偿调用方错误。

完成条件：一条不经过 `ShaderProgram` 或引擎反射 DTO 的端到端 Slang 行为测试通过。

### 阶段 B：建立 Block 与 `Shader` 所有权

1. 重写 `ShaderSourceBlock` 的实际数据取得和异步编译。
2. 重写 `ShaderBlock` 的代码/Slang 数据取得。
3. 重写不可变 `Shader` 的普通托管引用和同步查询；删除原地 `Update`、`IDisposable`、finalizer、可交换内层包装、页引用和 release 委托路径。
4. 对每类来源建立直接的最终表示：借用/压缩输入一次物化为最终数组，独立 GC backing 直接引用，Slang 结果直接保留 blob；不增加统一包装。

完成条件：丢弃 Asset、Block 和文件引用后 `Shader` 仍可同步反射并创建 pipeline；`Shader` 无需 `Dispose`；临时页不会逃逸；编译产生的 blob 不经过页或托管包装再复制回 `Shader`。

### 阶段 C：迁移 Graphics 与后端

1. Graphics 描述改用直接 Slang 类型，以及实际来源对应的 `ISlangBlob` 或 BCL 代码只读视图。
2. 迁移 D3D12 所有 Shader 消费点。
3. 迁移 Vulkan 所有 Shader 消费点。
4. 为异步原生对象创建补齐真正的持有关系。

完成条件：`SomeEngine.Graphics` 与后端不再引用 `ShaderProgram*`、`ShaderVariable*`、`ShaderType*` 旧类型。

### 阶段 D：迁移 Render 与 Material

1. 迁移 Cluster、普通 Render、UI、Presentation。
2. 从 Slang 直接产生消费者具体的材质写入布局和后端放置数据。
3. 删除对 `Shader.Metadata`、`TryReflection`、`CookedShaderEntry` 的全部调用。

完成条件：Render 行为保持，且没有通用反射镜像。

### 阶段 E：删除旧模型并重建内容

1. 删除第 10.1 节的文件和类型。
2. 删除生成的旧序列化代码入口和旧 schema reader。
3. 增加版本并重新生成全部 Shader 资产。
4. 更新测试、示例和文档。
5. 全仓静态审计旧名和旧结论。

完成条件：最终树中不存在兼容层、第二套反射来源或旧资产读取。

## 14. 验证要求

### 14.1 公共行为测试

测试只覆盖行为和公共 API，不断言内部字段排列或私有持有类。

必须覆盖：

- 保存代码的 `ShaderBlock` 创建 `Shader` 后，丢弃 Asset、Block 和加载缓存引用并关闭文件，`Shader` 仍能创建有效 pipeline。
- `ShaderSourceBlock` 在用户机器路径编译源代码/Slang 中间数据并创建有效 pipeline。
- 保存代码路径与现场编译路径对调用方呈现相同的入口、反射和渲染行为。
- D3D12 与 Vulkan 的 raster、compute、mesh、ray tracing、stream output；具备 Work Graph 的环境还要覆盖 Work Graph。
- Slang 属性直接驱动几何和材质行为，不经过序列化反射 DTO。
- 普通托管数组来源、分段托管数组来源和直接 `ISlangBlob` 来源均不要求调用方 `Dispose`，也不依赖 release 委托。
- 最后一个 `Shader` 及其消费者引用消失后，缓存预算决定已保存代码数组的额外保留；Clear 或预算淘汰解除该强引用。实际读取结束后任务表移除工作，Slang native 引用由 binding/运行时最终回收。该项使用隔离的弱引用与内存压力测试验证，不要求精确到某一次 GC。
- 新 `Shader` 构建成功后，既有消费/缓存边界一次交换外层引用并通知使用方；构建失败或取消时仍引用旧 `Shader`。
- 异步 pipeline 创建期间，即使外层引用已换成新 `Shader` 且调用方丢弃旧引用，创建任务仍直接持有并使用旧 `Shader`。
- 版本变化是完整对象替代而非页级 COW：旧消费者只能观察旧实例，新消费者只能观察完整的新实例，不得观察逐段修改的中间状态。
- include/import、诊断和特化走同一 Block 编译路径。

以下测试删除而不是改写：

- 只验证 `ShaderProgram` 序列化/反序列化对象图的测试；
- 只验证 `ShaderMetadata`、`ShaderReflectionData` 等 DTO 字段的测试；
- 只验证旧 schema 字段顺序和旧反射快照内容的测试。

现有端到端渲染测试可以保留并改写其 Shader 创建 helper，使其使用直接 Slang 路径。

### 14.2 静态审计

实现完成后，源代码和测试中下列名字必须为零；架构迁移文档中的删除清单除外：

```text
ShaderProgram
ShaderProgramLayout
ShaderEntryPoint
ShaderVariable
ShaderVariableLayout
ShaderType
ShaderTypeLayout
CookedShaderEntry
CookedShaderProgram
ShaderReflectionData
ShaderEntryPointReflection
ShaderResourceReflection
ShaderMetadata
ShaderQualifiers
AccessEffect
TextureSampleType
StorageFormat
ShaderEntrySelection
ShaderEntry
SlangShaderCookProfile
SlangImportLifetime
```

Shader 与 Asset 发布数据路径中下列生命周期包装必须为零；其他领域碰巧同名但语义无关的类型必须人工判定：

```text
ShaderContent
SharedData
UnmanagedReadOnlyMemory
SlangBlobMemory
```

其中 `SharedData` 指带 release 委托、手工引用计数或可交换 Shader 内层数据的容器。还要审计语义等价物，不能仅通过换名满足字符串检查。

引擎源码、测试、benchmark 和工具中的 Slang 路径还必须满足以下调用为零；绑定包自身的实现不在该字符串审计范围内：

```text
ComObject.FinalRelease
FinalReleaseComObject
Marshal.AddRef
Marshal.Release
```

仓库中不得重新出现 `external/SlangShaderSharp` gitlink、项目引用或 `.gitmodules` 项。

### 14.3 构建与内容

- 全解决方案 clean build。
- Shader、Asset、Graphics、D3D12、Vulkan、Render 的相关测试全部通过。
- 全部 `.shader.asset` 从新 schema 重建成功。
- DefaultGame 使用实际内容启动，不读取旧缓存或旧 Asset。
- 无临时探针、临时脚本、临时结果和未引用的迁移文件留在工作区。

2026-09-29 审查前的执行记录如下，保留其当时结果与范围。随后审查记录了 V01 两次组合测试中止及 I06、I10–I16 等实现偏差；这些具名问题的 2026-10-01 修复与验证见 [本次记录](../Assets/Asset-Verification-2026-10-01.md)。

- `dotnet build SomeEngine.slnx --no-restore`：0 警告、0 错误；benchmark Release 构建同样为 0 警告、0 错误。
- 解决方案相关测试合计 2,494 项通过，3 项按既有条件跳过。一次跨项目并行运行使全局 Job 初始化测试和“零分配”计数测试各受到另一测试进程干扰；两个项目随后隔离复跑分别为 181/181、255/255，通过且未修改产品代码。
- 其中与本重构直接相关的 Asset 223/223、D3D12 312/312（另有 1 项 Intel 硬件条件跳过）、Vulkan 45/45、Render 83/83、Render Cluster 30/30、RenderGraph 114/114 全部通过。
- DefaultProject 从源重新构建 67 个项目资产；独立 `SomeEngine.AssetCook` 对 `hello_triangle.shader` 同时生成并重开 Dxil/Spirv，两个 target 均发现 2 个入口。
- `SomeEngine.Assets.AotSmoke` 以 win-x64 NativeAOT self-contained 发布并实际执行；Asset 关闭后的 Shader、直接 Slang 创建、Mesh/Texture/Material/World 等行为全部通过。
- 静态审计确认产品源码、测试、benchmark 和工具中旧 Shader DTO 名与手工 `ComObject.FinalRelease` / `Marshal.AddRef` / `Marshal.Release` 调用为零；`external/SlangShaderSharp` 目录、gitlink、`.gitmodules` 项和项目引用均不存在。
- Satori archive、自包含 benchmark、NDJSON、AOT publish 目录和场景原始报告均在记录结果后删除；仓库只保留可复现工具与规范中的汇总证据。

### 14.4 性能与内存

- 性能验证不能只统计 Shader。必须覆盖仓库真实 Shader、Mesh、Texture，以及能代表客户端框架的音频/文档类二进制和网络缓存样本；样本报告尺寸分布、压缩率、范围访问比例、更新粒度和旧版本并存时间。
- 对同一数据和访问模式至少比较：实际长度 GC 数组、多个不可变 GC 数组、现有纯手工 native arena；可选 Satori 作为第四组，标准 CoreCLR 仍是正确性和基础性能基线。
- 每组记录构建/读取吞吐、端到端延迟、p50/p95/p99/最大停顿、托管分配量、GC 次数与暂停、native 分配量、复制字节数、峰值工作集、保活放大和完成后回落内存。不得只用分配微基准推断 Asset 综合成本。
- 分段方案必须另外记录数组对象数、序列节点数、对象头/对齐/索引元数据开销和遍历分支；native arena 必须记录预留但未使用的字节与碎片。不能只比较 payload 字节。
- 压缩或借用来源允许一次进入最终托管存储的复制；已经独立且大小匹配的不可变 GC backing 应为零复制；直接 Slang blob 不为形式统一而复制。任何额外复制必须标注来源、目的和存活区间。
- 分段阈值和切断大 backing 的阈值只能依据上述结果决定。至少同时包含小对象密集、固定 1 MiB、包含 4 MiB 大值、混合尺寸、更新后旧版本短期并存和长时间 churn；报告 CoreCLR 与 Satori 各自结果，不能把 Satori 的优势写成公共语义前提。
- 纯手工 arena 基线必须计入申请、回收、同步、碎片、峰值预留和调用方生命周期维护成本，不能只计顺序写入循环；GC 方案也必须计入真实回收停顿和峰值。
- 若本次重构影响渲染性能，必须使用 DefaultGame 的统一场景平台，运行 `tools/Measure-Scenes.ps1`，覆盖 1024 小球、Sponza、Amazon Bistro；单次多场景反馈不超过 60 秒。三场景未全部收敛的报告不能判定通过。
- 性能对比保持分辨率、场景行为、渲染选项、JIT 配置和采样方式一致；trace 定位结果与无 trace 性能结果分开记录。

#### 14.4.1 已执行的存储基准

仓库内的 `tools/Measure-AssetStorage.ps1` 和 `tools/SomeEngine.Assets.Benchmarks` 是正式复现入口。2026-09-29 的本机基线条件如下：

- Windows x64，32 个逻辑处理器；CoreCLR `.NET 10.0.12`；
- Satori 固定为 [`20260927.1-net10.0`](https://github.com/hez2010/Satori/releases/tag/20260927.1-net10.0)，Windows x64 archive SHA-256 为 `7DDBA8AB054904E55D849CC3A8093EF0493B166A9C596DC16F9B682BAF44B3A7`，self-contained 运行时报告 `.NET 10.0.13-dev`；
- runner 同时检查 `GC.GetConfigurationVariables()` 中的 `SatoriGC` 和 100,000 字节数组的 generation，防止把普通 CoreCLR 误标为 Satori；
- 每个“GC / latency / 输入 / 存储”组合使用独立进程，先完整预热一次，再采样 31 次；源文件读取和测试输入生成在计时区外；所有实现执行相同复制、完整扫描和校验和；
- 普通组覆盖真实 Shader、Mesh、Texture、Markdown，以及代表性音频、网络、小对象、固定 1 MiB、含 4 MiB 的混合尺寸和 churn，共 10 类；每轮 payload 为 16.00–18.84 MiB；
- `array` 为每个值的实际长度 `byte[]`；`segments` 为 64 KiB 实际长度数组和真实 `ReadOnlySequenceSegment<byte>` 节点；`native` 为每轮申请并释放的 4 MiB block bump arena；`native-reuse` 为预热后保留 block、每轮仅 reset、31 轮结束后统一 free 的理想复用 arena；
- p50/p99 是单轮“构建 + 完整读取 + 释放引用或 arena 动作”；`combined` 是 31 轮热区加最终强制回收，复用 arena 还计入最终 free；工作集为进程绝对峰值。输入已经是安全独立 GC backing 时，正式路径可以直接引用而不复制，因此这里的 copy-in 结果是对 GC 发布路径更保守的上界。

下表是 10 个普通场景相对“每轮申请并释放的 native arena”的几何平均；小于 `1.0` 更好：

| GC / latency | 数组 p50 | 数组 p99 | 数组 combined | 数组峰值 WS | 64 KiB 分段 p50 | 分段 p99 | 分段 combined | 分段峰值 WS |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| CoreCLR / Interactive | 0.991x | 1.259x | 1.025x | 2.218x | 1.030x | 1.578x | 1.075x | 1.577x |
| Satori / Interactive | 0.687x | 1.028x | 0.760x | 1.795x | 0.861x | 1.255x | 0.910x | 1.888x |
| CoreCLR / LowLatency | 1.658x | 1.417x | 1.848x | 11.799x | 2.905x | 2.319x | 3.069x | 12.192x |
| Satori / LowLatency | 0.697x | 1.098x | 0.767x | 1.885x | 0.831x | 1.290x | 0.906x | 1.871x |

CoreCLR `LowLatency` 会压制普通 Gen2 回收，31 轮大对象分配尚未回收时峰值自然累积；它不是 Asset 批量构建的合理全局配置。该行解释配置代价，不代表 CoreCLR 数组在正常 Interactive 模式下固有地慢十倍。Satori 的 LowLatency 与 Interactive 在这组工作负载中接近，但这仍只是可选运行时调优，不进入 Asset 语义。

`native-reuse` 是更强但生命周期不同的下界。相对该下界，普通场景结果如下：

| GC / latency | 数组 p50 | 数组 p99 | 数组 combined | 数组峰值 WS | 每轮新 arena p50 | 每轮新 arena combined |
|---|---:|---:|---:|---:|---:|---:|
| CoreCLR / Interactive | 1.901x | 2.280x | 1.926x | 2.172x | 1.919x | 1.878x |
| Satori / Interactive | 1.308x | 1.812x | 1.405x | 1.764x | 1.905x | 1.850x |
| CoreCLR / LowLatency | 3.212x | 2.513x | 3.475x | 11.570x | 1.937x | 1.881x |
| Satori / LowLatency | 1.341x | 1.877x | 1.428x | 1.854x | 1.923x | 1.862x |

这张表不能单独决定发布数据使用 arena：复用 arena 的速度来自已经预留、已提交且可整体覆盖的内存，并要求所有切片同时失效。它量化了选择 GC 独立生命周期的性能价格，而不是提供把 `Dispose` 重新传染给 Asset/Shader 的理由。

真实/代表性输入在 Satori Interactive 下的绝对端到端延迟如下；数值为 `p50 / p99`，单位 ms：

| 输入 | 每轮 payload / 值数 | 实际长度数组 | 每轮新 arena | 复用 arena | 复用 arena 容量放大 |
|---|---:|---:|---:|---:|---:|
| 仓库 Shader | 18.84 MiB / 52 | 9.64 / 13.07 | 13.77 / 14.47 | 7.51 / 8.38 | 1.27x |
| 仓库 Mesh | 16.06 MiB / 23 | 6.32 / 11.22 | 9.82 / 11.73 | 4.82 / 5.48 | 1.25x |
| 仓库 Texture | 16.00 MiB / 4,081 | 7.87 / 42.87 | 11.91 / 34.41 | 6.38 / 28.80 | 1.25x |
| 代表性音频 | 16.06 MiB / 38 | 6.98 / 10.73 | 10.07 / 11.69 | 5.14 / 5.75 | 1.25x |
| 仓库 Markdown | 16.02 MiB / 2,854 | 8.08 / 26.94 | 11.96 / 23.04 | 6.71 / 16.80 | 1.25x |
| 代表性网络缓存 | 16.01 MiB / 96 | 7.30 / 10.88 | 10.50 / 13.87 | 5.19 / 5.86 | 1.25x |

数组在多数普通场景的 p50 优于每轮新 arena；大量小对象的 Texture/文档组显示其 p99 和峰值仍高于 native。发布动作因此不得放入帧内或音频实时临界区；需要严格临界延迟的临时构建可在明确边界内使用 arena，完成后只把最终独立值发布给普通 GC 对象。

#### 14.4.2 COW 边界实测

特定 COW 组使用一个 4 MiB 不可分值，连续做 12 次位于不同 64 KiB 区间内的 4 KiB 修改，并同时保留最后 4 个版本；语义存活数据为 16 MiB。Satori Interactive 结果为：

| 存储 | p50 / p99 | 31 轮 combined | payload 分配/轮 | 结束时实际保留 | 峰值 WS | 结构数量/轮 |
|---|---:|---:|---:|---:|---:|---:|
| 实际长度数组 | 15.91 / 17.23 ms | 497.6 ms | 52.00 MiB | 16.00 MiB | 89.4 MiB | 13 arrays |
| 64 KiB 持久化分段 | 7.39 / 8.41 ms | 232.3 ms | 4.75 MiB | 4.19 MiB | 63.8 MiB | 76 arrays + 832 nodes + 13 value wrappers |
| 每轮新 arena | 25.23 / 30.52 ms | 796.6 ms | 52.00 MiB | 52.00 MiB | 89.8 MiB | 13 blocks；其中 36.00 MiB 已废弃 |
| 复用 arena | 9.42 / 10.02 ms | 298.6 ms | 52.00 MiB | 52.00 MiB | 91.2 MiB | 13 blocks；其中 36.00 MiB 已废弃 |

这里的分段收益来自跨版本共享未修改区间，不来自“页对象天然比数组快”。如果数据整体生成/整体替换、可以按语义拆分，或旧版本不同时存活，这个前提就不存在。Shader 满足整体生成/整体替换，因此仍禁止为了这个特殊结果引入通用 COW。

#### 14.4.3 渲染场景验证

迁移涉及 Graphics/RHI/Render 的 Shader 描述和异步持有边界，因此在 Release、无 trace 下执行 `tools/Measure-Scenes.ps1`。2026-09-29 的三场景均收敛为 `Measured`，整轮含应用启动 45.05 秒：

| 场景 | 帧数 | Mean | Median | P95 | Prepare | Execute |
|---|---:|---:|---:|---:|---:|---:|
| 1024 spheres | 1,473 | 1.36 ms | 1.33 ms | 1.50 ms | 0.22 ms | 0.77 ms |
| Sponza | 875 | 2.29 ms | 2.27 ms | 2.60 ms | 0.08 ms | 0.93 ms |
| Amazon Bistro | 550 | 3.65 ms | 3.63 ms | 4.05 ms | 0.11 ms | 1.94 ms |

这组数据证明当前分支三场景可完整运行并提供迁移后的绝对基线；没有同机、同配置的迁移前一轮时，不声称具体百分比提升或回归。

## 15. 完成定义

只有同时满足以下条件，本重构才完成：

- 整个引擎只有 `ShaderAsset` 一种 Shader 语义资产身份；
- 编译、已有代码、缓存和内存生成都通过 Block 取得数据；
- 用户机器可以显式异步编译 Shader；
- `Shader` 只表达它所引用数据的驻留与同步访问保证；
- `Shader`、材质、Graphics 描述和托管 pipeline 创建方不因数据驻留实现或传播 `IDisposable`；
- 发布后的托管代码只由普通 GC 引用保活，不使用页引用计数、release 委托、finalizer 或通用页级 COW；
- 借用或压缩输入最多一次进入唯一最终托管表示，独立 GC backing 直接共享，Slang blob 不为形式统一而复制；
- `ReadOnlyMemory<byte>` / `ReadOnlySequence<byte>` 等既有 BCL 视图保留，没有新增 `...Content` 或自定义非托管内存包装；
- Asset 系统按语义单位取得和回收数据，只有业务本身需要分页时才分页；通用页大小、GC 类型和内部数组分段不进入 Asset 身份、schema 或调用方生命周期；
- Shader 反射只来自当前有效的直接 Slang 对象；
- 所有消费者已迁移到直接 Slang 边界或明确的消费者具体原生数据；
- 第 10.1 节旧文件和类型全部删除；
- 旧 Shader Asset 无兼容 reader，所有内容已重建；
- 冲突文档和内部实现测试全部删除或重写；
- 构建、行为测试、内容构建、内存检查和需要时的三场景性能验证全部通过。

任何一项仍依赖旧反射 DTO、`ShaderProgram`、仅导入时编译的假设、额外 Shader Asset 类型、把通用页级 COW 当作 Asset 基础生命周期、向上要求 `Dispose`、为统一表示增加生命周期包装，或在没有收益证据时长期保留两份代码，均视为重构未完成。

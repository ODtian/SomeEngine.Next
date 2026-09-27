# Shader Block、驻留与 Slang 重构规范

日期：2026-09-28

状态：重构基线

适用范围：`SomeEngine.Assets`、`SomeEngine.Resources`、`SomeEngine.Assets.Importers`、`SomeEngine.Graphics`、D3D12/Vulkan 后端、Render、内容构建、测试与 Shader 架构文档

## 1. 结论

本次重构只保留下面一套模型：

1. `ShaderAsset` 是语义资产身份。场景、材质和其他资产只引用 `AssetId<ShaderAsset>`，不得因目标后端、网格管线、光追管线或编译状态引入新的 Shader 资产类型。
2. Block 是 `ShaderAsset` 内的具体数据及其异步取得边界。Block 不等于磁盘文件、不等于二进制块，也不承诺数据必然落盘。
3. `ShaderSourceBlock` 是可由 Slang 编译取得数据的特殊 Block；`ShaderBlock` 是已经能够取得可用代码的 Block。两者都产生同一个语义对象 `Shader`，不得产生不同的 Shader 资产类型。
4. `ShaderAsset` 不保证大数据驻留，因此访问 Block 可以异步；`Shader` 保证它引用的数据在自身生命周期内驻留，因此普通访问必须同步。
5. `Shader` 只保证自己实际引用的数据驻留，不要求复制或持有整个 `ShaderAsset`。
6. `Shader` 与 Asset 共用现有页机制。页内数据必须通过 `PageBuffer` / `PageRange` 的引用计数直接保留；不得先由 Asset 读入页，再复制一份给 `Shader`。
7. `Shader` 不被强制使用页内存。Slang 产生的 `ISlangBlob` 可以由 `Shader` 直接持有；其他确有需要的最终内存也可以由 `Shader` 直接持有。关键约束是只有一个主要持有者、没有无意义的双份表示。
8. 用户机器上的 Slang 编译是正式路径，不是编辑器专属路径，也不是异常回退。预编译代码、Slang 中间数据、源代码编译和缓存命中只是 Block 取得数据的不同方式。
9. Slang 是 Shader 语义、入口、类型、布局、属性和反射的唯一权威。引擎内不得定义、序列化或维护第二套 Shader 反射模型。
10. `Shader` 不能被定义为“单个 target + 单个特化组合的编译结果”。它可以引用一个或多个目标、入口及特化结果；它的定义只取决于同步驻留保证。
11. 最终字节码可以保存，也可以只存在于内存。是否落盘不改变 Block 和 `Shader` 的语义，也不得被写成“字节码只能是缓存”或“ShaderAsset 只能是编译输入”。
12. 当前所有不符合本规范的类型、字段、兼容读取、转换层、测试和文档必须删除；不得换名后保留同一套镜像数据。

以下两个等式明确无效，不得再作为设计依据：

```text
ShaderAsset = Slang 编译输入
Shader      = 一个具体 target + 特化组合
```

正确关系是：

```text
ShaderAsset = 一个 Shader 的语义身份 + 若干具体 Block
Shader      = 对其所引用 Shader 数据作出生命周期内驻留保证的使用对象
```

## 2. 名词边界

| 概念 | 唯一含义 | 不代表什么 |
|---|---|---|
| `ShaderAsset` | Shader 的语义身份和 Block 容器 | 某个后端代码、某个特化结果、所有数据均已加载 |
| Block | 一类具体数据以及取得这类数据的异步边界 | 文件、磁盘、二进制、缓存 |
| `ShaderSourceBlock` | 可供 Slang 取得源代码或 Slang 自身中间数据并执行编译的 Block | 只在导入器里有效的路径清单 |
| `ShaderBlock` | 可取得目标代码并建立直接 Slang 反射访问的 Block | 自定义反射快照、固定后端枚举 |
| `Shader` | 持有直接 Slang 对象和代码存储的同步使用对象 | 全资产副本、单目标结果类型、后台加载入口 |
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
- 释放 `ShaderAsset` 或取得它的 `AssetFile` 后，已有 `Shader` 必须继续有效。
- `Shader.Update` 必须先完整取得替代内容，再原子交换所引用的持有集合；失败或取消不得破坏旧内容。

### 3.4 数据只有一个主要持有者

按来源执行以下规则：

| 数据来源 | `Shader` 的持有方式 | 禁止行为 |
|---|---|---|
| 已在 Asset 页中的代码 | 复制页引用，保留同一 `PageBuffer` / `PageRange` | `ToArray()`、经 `MemoryStream` 复制、另存一份托管数组 |
| Slang 新产生的代码 | 直接持有 `ISlangBlob` 及其必要 Slang 所有者 | 仅为统一形式先复制进页，再复制给 RHI |
| 必须连续而页数据分段 | 优先直接读入最终使用方持有的连续内存 | 同时长期保留分段源和连续副本 |
| 后端创建任务 | 任务持有代码存储和 Slang 所有者直到本次创建结束 | 只借裸指针，随后允许 `Shader` 更新或释放 |
| 后端原生结果 | 由对应 pipeline/layout 对象持有 | 回写 `Shader`，或形成第二套通用反射树 |

如果原生 API 确实要求连续内存而已有页范围分段，允许一次有证据的复制。该路径必须直接得到最终持有的连续存储，并在复制后释放不再需要的源引用；不得用“以后可能需要”维持双份数据。

### 3.5 Slang 是唯一反射权威

- 所有 Shader 入口、阶段、参数、资源类别、绑定、类型、字段偏移、数组步长、属性和特化信息均从直接 Slang 对象读取。
- `ShaderReflection` 等反射对象是非独立所有者；`Shader` 必须同时持有使它们有效的 `ISession`、`IModule`、`IEntryPoint`、`IComponentType` 或 Slang 文档要求的其他接口。
- 不得把 Slang 反射复制为引擎 record/class，再让 Render、Material 或 RHI 查询副本。
- 不得序列化一棵引擎反射树。
- 不得以“避免热路径调用 Slang”为理由保存通用镜像。热路径需要的数据应在创建 pipeline/material 时直接由 Slang 派生为该消费者的具体原生数据。
- 消费者派生数据只能服务一个明确消费者。例如 D3D12 root parameter 表或材质常量写入布局可以存在，但它们不能提供通用反射查询，不能成为 `Shader` 的数据字段，也不能写回 Shader Asset。

### 3.6 通用 Asset/Block 规则与 Mesh 现状

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
- `ClusterMeshBlock.CreateAsync`、`LoadPagesAsync` 和 `OpenPayloadSourceAsync` 已经展示了正确方向：Block 显式异步取得数据，`Mesh` 保留使用期所需数据，后续页读取仍由明确行为触发。
- Shader 重构必须复用这套思想，不能创建按后端或管线命名的 Shader Asset。
- 以后若出现 `ClusterMeshAsset`、`DxilShaderAsset` 或相同语义的新类型，直接删除并把具体数据移入对应语义 Asset 的 Block。

### 3.7 Shader 配置资产的边界

当前 `ClusterShadersAsset` 和 `VirtualShadowMapShadersAsset` 不是单个 Shader 的替代身份；它们目前保存“管线角色到 `ShaderRef`”的配置集合。因此它们可以保留，但必须满足：

- 每个实际 Shader 仍通过 `AssetId<ShaderAsset>` 引用；
- 配置 Block 只保存角色与语义引用，不保存代码、Slang 反射副本或后端布局；
- `ClusterShadersBlock.CreateAsync`、`VirtualShadowMapShadersBlock.CreateAsync` 取得的是由 `Shader` 锚定的直接 Slang 入口；
- 如果这些 Asset 以后开始承载实际 Shader 数据，就必须删除该数据并移回所引用 `ShaderAsset` 的 Block。

当前 `ShaderRef.LoadAsync` 固定读取 `ShaderBlock`，且调用方只提供 `AssetLoader`，无法表达用户机器编译所需的直接 Slang 目标和特化输入。它必须重写：编译目标和特化由真正创建渲染数据的调用边界显式提供，使用 Slang 类型；加载行为再选择可用 `ShaderBlock` 或 `ShaderSourceBlock`。不得让 `AssetLoader` 猜设备目标，也不得重新引入自定义 target 请求 record。

### 3.8 命名约束

- 本次重写或新增的引擎标识符必须遵守仓库 `AGENTS.md` 的禁用词要求，按“是什么数据”或“是什么行为”命名。
- Slang 官方类型名原样使用，不为规避禁用词再包一层引擎类型。
- `ClusterGeometryContracts.cs` 在迁移直接 Slang 属性时必须按其中实际数据/行为改名；不得延续空洞的 `Contract` 命名。
- `CookedClusterShaderContractTests`、`D3D12TestShaderProgram`、`VulkanTestShaderProgram`、`CookedShaderTestProgram` 等在重写测试 helper 时按实际行为改名；如果只验证旧模型则直接删除。

### 3.9 校验边界

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
     -> 异步取得页、Slang 不透明数据或其他必要数据
     -> 用 Slang 恢复/建立 IComponentType 与直接反射句柄
     -> Shader 保留相同页引用或 Slang blob
     -> 返回驻留完成的 Shader
  -> ShaderAsset 可以释放
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

编译结果是否随后保存，不影响本次返回的 `Shader`。保存时不得为了 `Shader` 再做一份副本；写入行为从当前持有的 blob/页读取即可。

### 4.4 更新路径

```text
旧 Shader（继续可用）
  + 异步取得或编译替代内容
  + 完整建立新的 Slang 所有者与代码存储
  + 一次交换 Shader 的持有集合并增加 Revision
  + 通知使用方重建其具体 pipeline/material 数据
  + 没有使用方后释放旧持有集合
```

更新过程中不得逐字段改写反射 DTO；目标结构中也不再有这些 DTO。

## 5. `ShaderSourceBlock` 的职责

`ShaderSourceBlock` 保留，但必须从“导入器路径和自定义选择 DTO”重构为真正可取得编译输入的 Block。

必须满足：

- 二进制 Asset 中若要求用户机器编译，就必须包含或引用实际可取得的源代码、include/import 内容或 Slang 自身可重新加载的数据；只保存编辑器机器上的相对路径不构成可用 Block。
- include/import 解析交给 Slang。引擎可提供读取 Block 内容的 Slang 文件系统适配，但不得自行解析 Shader 语言依赖关系来建立另一套语义。
- 编译调用使用 Slang 的 `TargetDesc`、`CompileTarget`、profile、`SpecializationArg`、`IEntryPoint` 等直接类型。
- `.shader` 文本可以保留人类可编辑的构建设置，但它只是构建边界的输入格式；这些设置不得进入 `Shader` 成为反射数据。
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

1. 复制/保留 Block 自身的访问状态，避免与编辑并发破坏数据源；
2. 异步取得创建所需的数据；
3. 使用直接 Slang API 建立所有者对象、入口与反射；
4. 将页引用、Slang blob 或最终存储的持有权交给 `Shader`；
5. 返回后不要求 `ShaderAsset`、Block 副本或文件继续存活。

严禁继续执行当前 `PageBuffer -> PageBufferStream -> ShaderProgram.Read -> 托管对象图` 路径。

## 7. `Shader` 的目标形态

`Shader` 是使用对象，不是序列化格式。它只持有下列三类数据：

1. 直接 Slang 所有者接口：例如 `ISession`、`IModule`、`IEntryPoint`、`IComponentType`；
2. 直接 Slang 反射句柄：例如 `ShaderReflection`、`EntryPointReflection`、`VariableReflection`、`VariableLayoutReflection`、`TypeReflection`、`TypeLayoutReflection`；
3. 实际被引用的代码存储：页引用、`ISlangBlob` 或有明确需要的其他最终存储。

`Shader` 不再持有：

- `ImmutableList<ShaderProgram?> Programs`；
- `ShaderEntryPointAttribute`；
- `BackendReflection`；
- `ShaderEntryPointReflection`；
- `ShaderMetadata`；
- `ShaderEntryPointMetadata`；
- 由上述类型间接持有的材质布局、资源列表和实例属性副本。

查询 API 的规则：

- 查询返回直接 Slang 类型，或者返回 BCL/现有通用内存类型的只读视图。
- 入口按名称查找时最终返回 `IEntryPoint` / `EntryPointReflection`，阶段使用 `SlangStage`。
- 代码读取不得要求调用方取得自定义 `ShaderProgram` 或 `CookedShaderEntry`。
- 若需要统一查看页内存和 `ISlangBlob`，只能增加通用、与 Shader 无关的只读内存适配；不得再创建携带入口/目标/反射字段的 Shader 数据类。
- 当前 `ShaderEntry` record 必须删除。消费者需要同时保活 `Shader` 和直接 Slang 入口时，由消费者自己的具体使用对象分别持有两者；不得再引入一个全引擎通用的 Shader 入口镜像。

`Dispose` 和 `Update` 必须释放/替换真正的所有者。仅设置 `_disposed` 而不释放页引用、Slang 接口和 blob 不合格。

## 8. Slang 绑定与依赖决策

### 8.1 删除绑定层自建模型

以下文件必须删除：

- `external/SlangShaderSharp/src/ShaderProgram.cs`
- `external/SlangShaderSharp/tests/ShaderProgramTests.cs`

不得把它们拆成其他文件或换名保留。`ShaderProgram`、`ShaderProgramLayout`、`ShaderEntryPoint`、`ShaderVariable`、`ShaderType`、`ShaderVariableLayout`、`ShaderTypeLayout` 整棵脱离 Slang 生命周期的对象图都不再存在。

### 8.2 官方 NuGet 优先，fork 只修绑定缺口

依赖处理顺序固定为：

1. 用行为测试验证官方 `SlangShaderSharp` NuGet 是否覆盖编译、直接反射、代码生成、诊断、所需序列化往返和生命周期；
2. 若覆盖，删除 `external/SlangShaderSharp` 子模块引用，改用官方包；
3. 若不覆盖，保留 fork 时只能保留可独立证明的 C# 绑定或 COM 所有权修复，并把差异缩到最小；
4. fork 中不得存在任何 SomeEngine 语义、反射快照、Asset schema 或 RHI 数据模型。

当前 fork 相对官方的本地差异需要逐项处理：

| 差异 | 处理 |
|---|---|
| COM 所有权修复 | 用生命周期行为测试证明；官方包已解决则删除，否则保留最小补丁 |
| `ShaderProgram.cs` 脱离式反射 | 无条件删除 |
| SourceLink 打包调整 | 与引擎 Shader 架构无关；仅按包构建需要决定，不得作为保留 fork 的理由 |

### 8.3 生命周期

`SlangImportLifetime` 必须删除。它当前用任意对象列表和逆序 `FinalReleaseComObject` 补偿不清晰的所有权，不能作为正式模型。

替代规则不是另建一个 Shader 生命周期容器，而是：

- 创建 Slang 对象的代码立即把明确的所有者接口交给 Block 的临时状态或最终 `Shader`；
- 非拥有反射句柄始终由对应所有者锚定；
- 失败路径按已成功取得的明确资源释放；
- 若绑定确实需要统一的 COM 引用工具，该工具必须是通用绑定设施，只管理引用，不保存 Shader 字段，也不解释 Shader 语义。

## 9. Graphics、RHI 与 Render 的直接 Slang 边界

### 9.1 Graphics 公共描述

`SomeEngine.Graphics/Pipelines/Descriptions.cs` 以及 Ray Tracing、Work Graph、Indirect Commands、Descriptors 能力接口中的下列参数必须迁移：

- `ShaderProgram` -> 对应的直接 `IComponentType` / `ShaderReflection` 与代码只读视图；
- `ShaderEntryPoint` -> `IEntryPoint` / `EntryPointReflection`；
- `ShaderVariable` -> `VariableReflection`；
- `ShaderVariableLayout` -> `VariableLayoutReflection`；
- `ShaderType` / `ShaderTypeLayout` -> `TypeReflection` / `TypeLayoutReflection`。

不要再定义一个“更适合 Graphics”的中间 Shader 模型。代码存储和 Slang 所有者必须共同保持到异步原生对象创建结束。

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
| `Shader.cs` | 只持有直接 Slang 所有者、反射句柄和实际代码存储 |
| `ShaderRef.cs` | 保留 Asset 身份与必要入口名；删除阶段镜像，显式异步取得 `Shader` |
| `ShaderEntry.cs` | 删除；具体消费者分别持有 `Shader` 和直接 Slang 入口 |
| `ClusterShadersBlock.cs` / `VirtualShadowMapShadersBlock.cs` | 保留角色到 `ShaderRef` 的配置；把解析结果改为由 `Shader` 锚定的直接 Slang 入口 |
| `SlangShaderDefinition.cs` | 只负责构建边界和 Block 行为编排；不再建立反射 DTO |
| `SlangShaderImporter.cs` | 复用同一 Slang 编译行为并可选保存 Block；不得成为唯一可编译位置 |
| `Descriptions.cs` 及 Graphics Shader 参数 | 接受直接 Slang 类型与通用代码内存视图 |
| D3D12/Vulkan Shader 消费文件 | 从 Slang 直接派生原生对象所需数据 |
| Render/Cluster/UI/Presentation Shader 消费文件 | 使用 `Shader` 的直接 Slang 入口和代码，不再调用 `RequireEntry` 返回旧包装 |
| Shader 测试与 benchmark helper | 使用直接 Slang 行为并按具体用途改名；只服务旧模型的 helper 删除 |

### 10.3 当前实现中必须消失的具体行为

- `ShaderBlock.CreateCoreAsync` 中的 `ShaderProgram.Read`。
- `Shader.Create(IEnumerable<ShaderProgram>, ...)`。
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

1. `ShaderBlock.Version` 从当前 `9` 增加到下一版本；`ShaderAsset.Version` 随之变化。
2. `SlangShaderImporter.ImporterVersion` 增加，确保所有生成结果失效。
3. 删除旧 `ShaderBlock` 字段，不保留 `[Obsolete]`、别名、兼容 reader 或旧数据转换器。
4. 删除旧 `ShaderProgram` blob 解码路径。
5. 所有 `.shader.asset` 重新构建；旧生成文件和旧缓存直接丢弃。
6. `.shader` 文本若仍含 `Targets`、`SelectedEntries` 等旧字段，按新 Slang 直接输入格式一次性迁移；不在引擎中长期保留双格式 reader。
7. 内容仓库、示例和测试 fixture 全部在同一重构中更新，主分支最终不得依赖旧产物。

迁移允许在开发分支的连续提交中临时出现未迁完的调用点，但合入主分支时只能有一条新路径，不能用适配器让新旧模型长期共存。

## 12. 文档处理

本规范完成实现后成为 Shader Asset、驻留、编译和反射边界的唯一总规范。下列文档不得继续保留冲突结论：

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
| `wiki/architecture/Assets/Asset-Rebuild-Execution.md` / `Asset-Rebuild-Inventory.md` | 删除仍把 `ShaderProgram` 和镜像元数据视为现状的段落；历史只留在 Git |
| 其他出现旧类型或“shipping 不编译”的文档 | 同步删除或重写 |

不得用“历史设计”名义把冲突文档留在现行架构目录。确需保存历史只能依赖 Git 历史。

## 13. 实施顺序

### 阶段 A：证明 Slang 直接路径

1. 针对官方 NuGet 和当前最小 binding 分别验证：源代码编译、入口发现、特化、链接、目标代码、直接反射、诊断和释放。
2. 验证采用的 Slang 不透明数据能否在新 session 中往返并重新建立所需直接反射。
3. 验证 `ISlangBlob`、`IComponentType` 和反射句柄的真实生命周期。
4. 根据结果决定官方 NuGet 或最小 fork；不得先设计自定义容器补 API 证据空缺。

完成条件：一条不经过 `ShaderProgram` 或引擎反射 DTO 的端到端 Slang 行为测试通过。

### 阶段 B：建立 Block 与 `Shader` 所有权

1. 重写 `ShaderSourceBlock` 的实际数据取得和异步编译。
2. 重写 `ShaderBlock` 的代码/Slang 数据取得。
3. 重写 `Shader` 持有集合、同步查询、更新和释放。
4. 建立页引用与 blob 的无复制交接。

完成条件：Asset 和文件释放后 `Shader` 仍可同步反射并创建 pipeline；编译产生的 blob 不经过页再复制回 `Shader`。

### 阶段 C：迁移 Graphics 与后端

1. Graphics 描述改用直接 Slang 类型和通用代码内存。
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

- 页支持的 `ShaderBlock` 创建 `Shader` 后，释放 Asset、Block 和文件，`Shader` 仍能创建有效 pipeline。
- `ShaderSourceBlock` 在用户机器路径编译源代码/Slang 中间数据并创建有效 pipeline。
- 保存代码路径与现场编译路径对调用方呈现相同的入口、反射和渲染行为。
- D3D12 与 Vulkan 的 raster、compute、mesh、ray tracing、stream output；具备 Work Graph 的环境还要覆盖 Work Graph。
- Slang 属性直接驱动几何和材质行为，不经过序列化反射 DTO。
- Asset 可提前释放而 `Shader` 有效；`Shader` 释放后所有页和 Slang 所有者最终释放。
- `Shader.Update` 成功后使用方收到变化；失败或取消时旧 `Shader` 保持可用。
- 异步 pipeline 创建期间，即使原 `Shader` 更新或释放，创建任务仍持有本次所需数据。
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

还要审计语义等价物，不能仅通过换名满足字符串检查。

### 14.3 构建与内容

- 全解决方案 clean build。
- Shader、Asset、Graphics、D3D12、Vulkan、Render 的相关测试全部通过。
- 全部 `.shader.asset` 从新 schema 重建成功。
- DefaultGame 使用实际内容启动，不读取旧缓存或旧 Asset。
- 无临时探针、临时脚本、临时结果和未引用的迁移文件留在工作区。

### 14.4 性能与内存

- 记录 Shader Asset 读取字节数、页保留量、Slang blob 持有量、pipeline 创建时复制次数和峰值内存。
- 已在页中的代码交给 `Shader` 时复制次数必须为零。
- Slang 产生代码直接交给 `Shader` 时复制次数必须为零；若原生 API 要求另一种最终连续表示，最多一次且有调用点证据。
- 若本次重构影响渲染性能，必须使用 DefaultGame 的统一场景平台，运行 `tools/Measure-Scenes.ps1`，覆盖 1024 小球、Sponza、Amazon Bistro；单次多场景反馈不超过 60 秒。三场景未全部收敛的报告不能判定通过。
- 性能对比保持分辨率、场景行为、渲染选项、JIT 配置和采样方式一致；trace 定位结果与无 trace 性能结果分开记录。

## 15. 完成定义

只有同时满足以下条件，本重构才完成：

- 整个引擎只有 `ShaderAsset` 一种 Shader 语义资产身份；
- 编译、已有代码、缓存和内存生成都通过 Block 取得数据；
- 用户机器可以显式异步编译 Shader；
- `Shader` 只表达它所引用数据的驻留与同步访问保证；
- 页数据不复制，Slang blob 不为形式统一而复制；
- Shader 反射只来自当前有效的直接 Slang 对象；
- 所有消费者已迁移到直接 Slang 边界或明确的消费者具体原生数据；
- 第 10.1 节旧文件和类型全部删除；
- 旧 Shader Asset 无兼容 reader，所有内容已重建；
- 冲突文档和内部实现测试全部删除或重写；
- 构建、行为测试、内容构建、内存检查和需要时的三场景性能验证全部通过。

任何一项仍依赖旧反射 DTO、`ShaderProgram`、仅导入时编译的假设、额外 Shader Asset 类型、Asset 到 `Shader` 的无条件复制，均视为重构未完成。

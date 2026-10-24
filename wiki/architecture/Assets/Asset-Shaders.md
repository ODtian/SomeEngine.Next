# ShaderAsset、Block 与驻留 Shader

`ShaderAsset` 是唯一 Shader 语义资产身份。材质、场景和管线配置保存 `AssetId<ShaderAsset>`；target、入口和特化不会产生新的资产类型。

完整规则见 [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)。本页说明当前取得 API；具名修复及验证状态见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

## Block 组成

```text
ShaderAsset
  +-- ShaderSourceBlock           共享 IR、所选输入的位置与顺序
  +-- ClusterMaterialEntriesBlock Cluster 入口 IR
  +-- 业务自己的 ShaderInputBlock 业务管线入口 IR
  +-- ShaderBlock                 可异步取得的已生成代码
               -> Shader         同步、驻留的共同链接结果
```

可读 `.shader` 的 `ShaderSourceBlock.SourceNames` 指定相对于定义文件的根源码。内容构建通过 Slang 读取源码及 include/import，将每个 `IModule.Serialize` 的结果保存到 Block；二进制 Asset 从这些 Slang 数据恢复模块，无需重新打开构建机器上的源码。

`ShaderInputBlock` 统一保存 `SourceNames` 与逐份序列化 Slang IR。`ShaderSourceBlock`、`ClusterMaterialEntriesBlock` 与消费者派生的入口 Block 使用同一 `ShaderInputData` 编码。消费者声明 `[AssetBlock("业务的持久名称")]` 的具体 partial 类；`AssetBlockGenerator` 生成该类型的复制、静态 `ReadAsync` 和保存方法，恢复为业务指定的具体类型。

`ShaderSourceBlock.Inputs` 保存共同输入清单。每个 `ShaderInputLocation` 用 `BlockTypeId` 与 `SourceIndex` 指向所在 Block 的实际 IR，清单顺序决定 Slang 输入与入口顺序。业务通过 `ShaderAsset.SelectInputs<TBlock>()` 选择已经取得的强类型 Block。各 Block 持有自己的字节，共同清单只持有位置。

## 共享材质与入口准备

`StandardMaterialInputs.Create()` 返回含引擎材质接口及 `StandardMaterial` 序列化 IR 的 `ShaderSourceBlock`。`ClusterMaterialEntriesBlock.Create()` 返回 Cluster 的入口 IR。业务入口沿 Slang 的 `import material_interfaces`、`import standard_material` 使用这些公开类型，并将自己 `IModule.Serialize` 的结果放入具体入口 Block。

Unlit 使用 `UnlitMaterialInputs.Create()` 与 `ClusterMaterialEntriesBlock.CreateUnlit()`。DefaultGame 和游戏模板的准备代码显式选择各自的输入，然后保存固定入口集合的共同发布代码。通用 CLI 编译已经准备好的 ShaderAsset 输入；管线集合由业务准备代码选择。

```csharp
[AssetBlock("Game.ForwardMaterialEntries")]
public sealed partial class ForwardMaterialEntriesBlock : ShaderInputBlock;
```

```csharp
using var asset = new ShaderAsset { Name = "Standard" };
using ShaderSourceBlock material = StandardMaterialInputs.Create();
using ClusterMaterialEntriesBlock cluster = ClusterMaterialEntriesBlock.Create();
using var forward = new ForwardMaterialEntriesBlock { SourceNames = ["forward_material.slang"] };
forward.SetSerializedSource(0, forwardIr.Span);
asset.SetBlock(material);
asset.SetBlock(cluster);
asset.SetBlock(forward);
asset.SelectInputs<ShaderSourceBlock>();
asset.SelectInputs<ClusterMaterialEntriesBlock>();
asset.SelectInputs<ForwardMaterialEntriesBlock>();
Shader shader = await asset.CreateAsync(slang, sessionDescription);
```

`ShaderAsset.CreateAsync` 按共同清单异步取得 IR，使用同一个 `ISession` 恢复 Slang 对象，compose/specialize/link 一次，返回一个包含全部所选入口的 `Shader`。持久 Block 的共同编码由 Asset 的目录与静态数据读取能力直接取得，业务的具体入口类型由泛型 Block 入口确定。`Inputs` 为空时，已有单个 `ShaderSourceBlock` 的取得行为继续作为默认输入。

引擎 IR 通过固定版本 Slang 导出并嵌入资产程序集。修改相关 Slang 实现后执行：

```powershell
dotnet run --project tools/ExportShaderInputs/SomeEngine.ExportShaderInputs.csproj -- .
```

输出位于 `assets/Shaders/Inputs/*.slang-module`，对应导出来源是同目录的 `*.ir.slang`。

## 固定管线发布

发布内容确定管线入口集合后，调用 `ShaderAsset.CompileAsync(slang, sessionDescription)` 编译全部所选输入并替换共同 `ShaderBlock`。返回的 `ShaderCompilation.Shader` 持有这次 Slang 链接生成的直接代码 blob；源码定义的编译同时保存序列化 IR 并返回实际读取的文件记录。已经准备好的 IR 继续由各输入 Block 持有。

共同 `ShaderBlock` 保存可取得的整 target 代码及全部逐入口代码。`InputVersions` 按输入顺序记录 `ShaderInputLocation` 与所在 Block 的 `DataVersion`。共同 IR 编码保存同一 `DataVersion`，文件重开恢复该内容版本；字节修改、字段替换与入口选择通过现有修改方法推进版本。输入位置、顺序与内容版本共同决定保存代码对应的输入。

`ShaderAsset.CreateAsync` 在链接前依据输入版本和现有代码设置寻址选择保存代码或 IR 代码生成。匹配保存代码时，同一组 IR 恢复直接 Slang 对象及反射，代码 chunk 取得为最终托管数组。IR 分发、额外特化以及编译设置变化使用共同输入生成 Slang blob。两条路径都按所选顺序链接一次，并返回同一种 `Shader`。

```csharp
await asset.CompileAsync(slang, sessionDescription, cancellationToken);
await asset.SaveAsync(releasePath, cancellationToken);
using ShaderAsset published = await ShaderAsset.OpenAsync(releasePath, cancellationToken);
Shader shader = await published.CreateAsync(slang, sessionDescription,
    cancellationToken: cancellationToken);
```

共同字节码服务固定管线发布内容的玩家启动。业务添加、替换或重排入口后重新调用 `CompileAsync`，同一资产身份取得新的共同代码。仅 IR 的分发内容保留完整输入，玩家机器通过相同 `CreateAsync` 完成编译。

`ShaderBlock` 保存由 Slang 生成的代码 chunk。chunk key 由实际 `SessionDesc`/`TargetDesc` 编译设置与入口序号得到；整 target 代码允许缺失，逐入口代码仍可独立取得。Block 不保存入口、阶段、资源、类型或布局的引擎副本。

## 两条取得行为

`ShaderSourceBlock.CreateAsync`：

1. 异步取得已经保存的序列化 Slang 数据，或从调用方搜索路径读取源码及依赖；
2. 用调用方提供的 `IGlobalSession` 和 `SessionDesc` 建立 `ISession`；
3. 恢复 `IModule`，取得 `IEntryPoint`，compose/specialize/link；
4. 直接持有 Slang 产生的代码 blob；
5. 返回驻留完成的 `Shader`。

`ShaderBlock.CreateAsync`：

1. 通过同一 `ShaderSourceBlock` 恢复直接 Slang 对象与反射；
2. 异步取得与调用方编译设置匹配的代码 chunk；
3. 将代码物化为实际长度的最终 `byte[]`，交给 `Shader` 通过普通 GC 引用持有；
4. 返回同一种 `Shader`。

释放 `ShaderAsset`、Block 副本或 `AssetFile` 后，已有 `Shader` 仍持有其 `ISession`、`IModule`、`IEntryPoint`、`IComponentType` 以及最终托管代码或直接 `ISlangBlob`。普通入口、反射和代码访问都是同步的。`Shader` 不实现 `IDisposable`，更新创建完整新实例，由消费方交换外层引用。

`Shader.SlangCode` 是 `SlangShaderData`，共同持有 global、session 与实际 linked component；
同一码的代码副本共享此对象，独立专门化保留自己的 component。RHI 的管线描述、异步创建请求、
管线和继续访问 reflection 的参数或间接命令对象传递同一份数据，直到实际使用结束。
原生 Slang component 通过借用指针访问 session，因此数据还持有一份原生 session 引用；
由 `SafeHandle` 在普通 COM wrapper finalizer 之后释放，保证整组回收时的父子顺序。
直接访问 `SlangCode.Code` 或借用的 reflection 时，调用方持续持有对应的 `SlangShaderData`。

`ApplicationShaderLifetimeTests.ApplicationsRenderTheSameSceneAcrossShaderCollection` 于 2026-10-16
通过 D3D12、Vulkan 两后端检查：每项在同一进程先后启动两个 Application，各渲染 8 帧，
关闭后执行完整 GC 与 finalizer 等待，两次线性图像在 0.001 容差内一致，测试进程正常完成。

`Shader.GetSlangEntryMetadataAsync` 在驻留 Shader 内共用每个 target/入口的直接 Slang `IMetadata` 与取得任务。材质加载可以提前启动入口准备，渲染侧从同一个 Shader 取得结果；入口准备的后台并发由这一取得路径统一限制。

Cluster 的提前准备与材质阶段解析共用入口选择。应用启动通过具体资产方法创建场景材质和管线 Shader，图形设备创建与内容加载并行；场景资源就绪后，渲染消费阶段对共用 Shader 调用 `ClusterPipelineSystem.PrepareMaterialShader`，按编译 target、可见性模式和设备能力选择着色、细分及光栅入口，启动入口资源分析。Release 的保存代码加载会话使用 Slang 的最少优化及基础代码优化级别完成反射与入口资源分析，必要的 lowering 与资源使用分析仍由 Slang 执行。保存代码 chunk 按调用方原始编译设置读取；源编译与导入使用构建设置。

`ShaderBlock` 的保存代码直接读入最终数组，通过统一 AssetByteCache 预算缓存，后续创建复用缓存结果。ShaderSourceBlock 的 CompileAsync 复用 CreateAsync 的编译主体，返回直接 Shader、序列化 Slang 数据和编译器实际读到的字节摘要。Importer 保存这些结果，并由项目核对输入版本。进程级 Importer 编译服务持有全局 Slang 会话，共同 Block 入口按会话串行访问。

## 引用与配置

`ShaderRef` 保存 `AssetId<ShaderAsset>` 与入口名。调用方为具体 Shader 创建提供 `IGlobalSession`、`SessionDesc` 与所选编译 target。`ShaderRef.LoadAsync` 接收文件读取器、强类型 WeakValues 及明确的 Slang 输入，通过 `ShaderAsset.GetAsync` 取得存活的共同 Shader，返回 `AssetId<ShaderAsset>` 到实际 Shader 的引用字典。Cluster、VirtualShadowMap Block 的 `Create(name, shaders)` 直接从这些实际引用解析入口。

`ClusterShadersAsset` 与 `VirtualShadowMapShadersAsset` 保存角色到 `ShaderRef` 的配置集合。对应 Block 的 `Create(name, shaders)` 同步消费已取得的实际引用，创建的资源组持有该 `Shader` 和由它锚定的 `EntryPointReflection`。

## 构建与失效

`.shader` 是人类可编辑来源，`SourceNames` 与 `Inputs` 表达实际编译输入。构建 target 由工具选择。`SlangShaderImporter` 使用官方 Slang API 编译、保存模块 IR 和可用代码，并记录实际读取的依赖。

这是破坏性 schema；旧反射字段和旧代码容器没有兼容 reader。编译器、来源、依赖、Block 编码或 target 设置变化时重新构建同一资产身份。

实现入口：[ShaderAsset](../../../src/SomeEngine.Assets/Schema/ShaderAsset.cs)、[ShaderInputBlock](../../../src/SomeEngine.Assets/Schema/ShaderInputBlock.cs)、[ShaderSourceBlock](../../../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs)、[ClusterMaterialEntriesBlock](../../../src/SomeEngine.Assets/Schema/ClusterMaterialEntriesBlock.cs)、[ShaderBlock](../../../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[Shader](../../../src/SomeEngine.Resources/Shader.cs)、[项目来源说明](../../../docs/ShaderSources.md)。

## 存活实例取得

`ShaderAsset.GetAsync(files, id, values, slang, settings)` 接收业务持有的 `WeakValues<AssetResourceKey<ShaderAsset>, Shader>`。一个索引的使用范围固定 Slang 设置；完整取得键保存文件读取器来源、资产 ID 和存储选定版本。所有可变特化输入通过 `ShaderAsset.CreateAsync` 的明确调用建立独立结果。

索引命中直接返回存活 Shader；唯一创建发起者在索引锁外打开 ShaderAsset、读取全部选定输入并创建完整 Shader。业务在自己的对象中保留实际引用；Shader 代码、反射与 Slang 数据的使用期由这些普通引用表达。完整取得与分批清理见 [WeakValues](../Weak-Values.md)。

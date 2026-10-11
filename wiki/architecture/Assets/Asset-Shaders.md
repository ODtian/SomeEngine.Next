# ShaderAsset、Block 与驻留 Shader

`ShaderAsset` 是唯一 Shader 语义资产身份。材质、场景和管线配置保存 `AssetId<ShaderAsset>`；target、入口和特化不会产生新的资产类型。

完整规则见 [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)。本页说明当前取得 API；具名修复及验证状态见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

## Block 组成

```text
ShaderAsset
  +-- ShaderSourceBlock  可异步取得的 Slang 编译输入
  +-- ShaderBlock        可异步取得的已生成代码和入口资源使用地址
             \\          /
              \\        /
               -> Shader   同步、驻留的使用对象
```

可读 `.shader` 的 `ShaderSourceBlock.SourceNames` 指定相对于定义文件的根源码。内容构建通过 Slang 读取源码及 include/import，将每个 `IModule.Serialize` 的结果保存到 Block；二进制 Asset 从这些 Slang 数据恢复模块，无需重新打开构建机器上的源码。

`ShaderBlock` 保存由 Slang 生成的代码与对应入口资源使用地址。chunk key 由实际 `SessionDesc`/`TargetDesc` 编译设置、专门化入口名、有序参数与入口序号得到；整 target 代码允许缺失，逐入口代码仍可独立取得。类型和布局由直接 Slang 反射提供；资源使用地址描述已生成代码的实际引用。

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
3. 将代码物化为实际长度的最终 `byte[]`，同时取得各 target/entry 已构建的资源使用地址，交给 `Shader` 持有；
4. 返回同一种 `Shader`。

释放 `ShaderAsset`、Block 副本或 `AssetFile` 后，已有 `Shader` 仍持有其 `ISession`、`IModule`、`IEntryPoint`、`IComponentType` 以及最终托管代码或直接 `ISlangBlob`。普通入口、反射和代码访问都是同步的。`Shader` 不实现 `IDisposable`，更新创建完整新实例，由消费方交换外层引用。

`Shader.GetSlangEntryMetadataAsync` 在驻留 Shader 内共用每个 target/入口的直接 Slang `IMetadata` 与取得任务。材质加载可以提前启动入口准备，渲染侧从同一个 Shader 取得结果；入口准备的后台并发由这一取得路径统一限制。

Cluster 的提前准备与材质阶段解析共用入口选择。着色、细分种子和细分拆分入口随材质 Shader 加载开始；光栅入口在设备创建后按编译 target、可见性模式和设备能力选择。Release 的保存代码加载会话使用 Slang 的最少优化及基础代码优化级别完成反射与入口资源分析，必要的 lowering 与资源使用分析仍由 Slang 执行。保存代码 chunk 按调用方原始编译设置读取；源编译与导入使用构建设置。

`ShaderBlock` 的保存代码直接读入最终数组，通过统一 AssetByteCache 预算缓存，后续创建复用缓存结果。ShaderSourceBlock 的 CompileAsync 复用 CreateAsync 的编译主体，返回直接 Shader、序列化 Slang 数据和编译器实际读到的字节摘要。Importer 保存这些结果，并由项目核对输入版本。进程级 Importer 编译服务持有全局 Slang 会话，共同 Block 入口按会话串行访问。

入口的资源使用地址与代码在同一次 Slang 编译中形成，随 `ShaderBlock` schema12 保存。
`Shader` 同时持有此数据与入口名称/阶段索引。Cluster 安装按用户 shader 的已构建使用地址
声明资源访问，场景切换读取同一 Shader 数据。ShaderSourceBlock 的直接编译在返回 Shader
前形成相同资源使用数据。Importer34 与 schema12 通过资产版本重新构建现有来源。

## 入口专门化

`ShaderSourceBlock.Specializations` 声明离线组合，每项包含原入口名 `EntryPoint` 和有序 Slang 表达式 `Arguments`。Importer 对每个入口单独 specialize，再与源码模块 compose/link；同一 Shader 内不同 generic 入口的参数互不拼接，nongeneric 入口按原方式构建。

加载 `ShaderBlock` 时，普通入口的代码和资源使用数据与已声明的专门化组合一起驻留于同一个资产版本。未特化反射保留原入口名称和阶段。调用方用 `HasSpecializations(entryPoint)` 判断指定入口，再用 `RequireSpecialization(entryPoint, arguments)` 取得对应 Shader，并从中查询原入口。普通入口仍直接使用原 Shader；不存在隐含的首组合执行选择。

公共 include 可在入口上重复使用 `[ShaderSpecialization("[\"false\"]")]` 这样的属性；一个属性的字符串是完整的有序 Slang 表达式 JSON 数组。属性类型由 `common.slang` 提供。源码编译和 Importer 共用读取逻辑，与显式 `Source.Specializations` 按入口名及完整有序参数取并集，完全重复的声明仅保留一次。原 .shader 声明仍用于导入失效判定，派生组合只写入 cooked 输出；保存代码加载仅读取该输出，不再次扫描属性或隐式编译。

generic 入口未声明所需组合，或已声明组合缺少代码/资源使用数据时，加载或选择明确失败。保存代码路径不隐式退回源码编译；直接 `ShaderSourceBlock.CreateAsync` 的显式源码开发能力保留。缓存身份包含入口与有序参数，一个资产 Guid 的 resident 对象持有各自的组合，不会把不同参数误合成同一码。

## 引用与配置

`ShaderRef` 只保存 `AssetId<ShaderAsset>` 与入口名。内部 `ShaderRef.LoadAsync` 的调用方显式提供 `IGlobalSession`、`SessionDesc` 和可选 `SpecializationArg`；公共材质及管线配置创建 API 传入这些参数或明确的 Shader 加载行为。`AssetLoader` 不猜设备 target。

`ClusterShadersAsset` 与 `VirtualShadowMapShadersAsset` 是角色到 `ShaderRef` 的配置集合，不是新的 Shader 身份。它们创建的资源组分别持有 `Shader` 和由该 `Shader` 锚定的 `EntryPointReflection`。

## 构建与失效

默认项目的 `DefaultContent.BuildAsync` 枚举引擎 `.shader` 声明，项目已有的同名定义直接编译；
缺项按声明中的 `SourceNames` 生成到 `assets/Shaders/generated/EngineSources/`，随后通过
同一 `AssetProject.BuildAsync` 导入。管线配置解析项目路径及其生成来源。引擎新增 VSM、
驻留传播或页需求 shader 时，这一构建入口从其唯一源声明产生项目输入。

`.shader` 是人类可编辑来源，`SourceNames` 与入口 `Specializations` 属于定义。构建 target 是工具选择，不进入 `Shader` 语义。`SlangShaderImporter` 使用官方 Slang API 编译、保存模块 IR 和可用代码，并记录实际读取的依赖。

默认 Vulkan target 明确使用 `spirv_1_5`，Importer 与应用加载使用同一设置，导入输入身份包含该 target。SPIR-V 1.5 是 `ShaderLayer` 核心 capability 的最低版本，允许顶点 shader 输出 `SV_RenderTargetArrayIndex`；现有 Vulkan 1.3 backend 支持该版本。无需为此提升到 1.6，也不向未使用分层输出的代码强行加入 capability。内建 NativeAOT 资产 smoke 与 graphics benchmark 使用同一默认 target，benchmark 的代码身份也包含该 profile。调用方显式提供的 `SessionDesc` / `TargetDesc` 保留原值，不由资产加载或 RHI 重写。旧 target 的资产通过正常增量构建刷新：导入输入 hash 包含默认 target，保存代码的 key 包含实际 profile；加载时不匹配的保存代码明确报缺项，由正式内容构建按新设置重新生成；显式源码编译仍可由开发调用方选择。

这是破坏性 schema；旧反射字段和旧代码容器没有兼容 reader。编译器、来源、依赖、Block 编码或 target 设置变化时重新构建同一资产身份。

实现入口：[ShaderAsset](../../../src/SomeEngine.Assets/Schema/ShaderAsset.cs)、[ShaderSourceBlock](../../../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs)、[ShaderBlock](../../../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[Shader](../../../src/SomeEngine.Resources/Shader.cs)、[项目来源说明](../../../docs/ShaderSources.md)。

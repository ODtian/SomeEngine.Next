# ShaderAsset、Block 与驻留 Shader

`ShaderAsset` 是唯一 Shader 语义资产身份。材质、场景和管线配置保存 `AssetId<ShaderAsset>`；target、入口和特化不会产生新的资产类型。

完整规则见 [Shader Block、驻留与 Slang 重构规范](../shader/Shader-Block-Residency-Refactor.md)。本页说明当前取得 API；具名修复及验证状态见 [2026-09-29 审查](../../../reviews/AssetSpecReview-2026-09-29.md)。

## Block 组成

```text
ShaderAsset
  +-- ShaderSourceBlock  可异步取得的 Slang 编译输入
  +-- ShaderBlock        可异步取得的已生成代码
             \\          /
              \\        /
               -> Shader   同步、驻留的使用对象
```

可读 `.shader` 的 `ShaderSourceBlock.SourceNames` 指定相对于定义文件的根源码。内容构建通过 Slang 读取源码及 include/import，将每个 `IModule.Serialize` 的结果保存到 Block；二进制 Asset 从这些 Slang 数据恢复模块，无需重新打开构建机器上的源码。

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

`ShaderBlock` 的保存代码直接读入最终数组，通过统一 AssetByteCache 预算缓存，后续创建复用缓存结果。ShaderSourceBlock 的 CompileAsync 复用 CreateAsync 的编译主体，返回直接 Shader、序列化 Slang 数据和编译器实际读到的字节摘要。Importer 保存这些结果，并由项目核对输入版本。进程级 Importer 编译服务持有全局 Slang 会话，共同 Block 入口按会话串行访问。

## 引用与配置

`ShaderRef` 只保存 `AssetId<ShaderAsset>` 与入口名。内部 `ShaderRef.LoadAsync` 的调用方显式提供 `IGlobalSession`、`SessionDesc` 和可选 `SpecializationArg`；公共材质及管线配置创建 API 传入这些参数或明确的 Shader 加载行为。`AssetLoader` 不猜设备 target。

`ClusterShadersAsset` 与 `VirtualShadowMapShadersAsset` 是角色到 `ShaderRef` 的配置集合，不是新的 Shader 身份。它们创建的资源组分别持有 `Shader` 和由该 `Shader` 锚定的 `EntryPointReflection`。

## 构建与失效

`.shader` 是人类可编辑来源，只有 `SourceNames` 属于定义。构建 target 是工具选择，不进入 `Shader` 语义。`SlangShaderImporter` 使用官方 Slang API 编译、保存模块 IR 和可用代码，并记录实际读取的依赖。

这是破坏性 schema；旧反射字段和旧代码容器没有兼容 reader。编译器、来源、依赖、Block 编码或 target 设置变化时重新构建同一资产身份。

实现入口：[ShaderAsset](../../../src/SomeEngine.Assets/Schema/ShaderAsset.cs)、[ShaderSourceBlock](../../../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs)、[ShaderBlock](../../../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[Shader](../../../src/SomeEngine.Resources/Shader.cs)、[项目来源说明](../../../docs/ShaderSources.md)。

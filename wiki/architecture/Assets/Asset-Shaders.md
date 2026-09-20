# Shader 的来源 Block、代码 Block 与组合资源

2026-09-16。ShaderAsset 保持独立身份；ShaderSourceBlock 保存人工维护的源码、入口及编译选择；ShaderBlock 保存编译字节码、反射与构建追踪。来源和产物是不同 Block，不再把它们平铺在资产根。

## 来源与编译

可读 `.shader` 拥有自己的 ID，可以选择多份原生 Slang 源码及入口。`.slang` 保持原语言、include 和 import 规则；仅发现源码不自动生成 ShaderAsset，也不创建与 `.shader` 重复的伴随设置。

项目编译参数、定义选择和实际读取的传递依赖一起决定构建结果。依赖来自实际编译读取，不用正则模拟 Slang 语义。编译器、扩展版本或源码变化可重新构建同一资产身份；失败保留已有成功产物和使用中的对象。

`ShaderSourceFormat` 通过 `AssetProject.BuildAsync` 完成输入核验、编译、成组保存和登记；局部编译入口为 `SlangShaderImporter.CompileDefinition`。旧的单个 `.slang` 隐式导入、自动创建 `.meta` 和独立缓存流程已移除。项目根目录与外部库 include 路径显式传入，不向父目录或进程当前目录查找引擎配置。

直接编译并保存一个定义时使用 `SlangShaderImporter.BuildAsync`，其 `projectRoot` 参数与 `CompileDefinition`、`GetDefinitionInputs` 一致：传入时以该目录记录依赖，省略时以定义所在目录记录。源码及 include 的相对选择仍以定义为基准。核验此前依赖时必须使用相同的工程目录参数，不能从祖先构建文件推断。

`.shader.import.json` 只保存可重建的输入记录、输出路径、内容摘要和文档版本。输入包括定义内容、实际传递依赖、入口、特化、后端目标、编译器及 Block 编码版本。复用前还须核验真实输出，不变或无关源码改动不重写输出；改名、所读 include 或目标改变触发构建。缺失、截断或损坏产物在登记身份一致时可以重建，不能覆盖另一有效资产身份。相同内容恢复可以保持同一内容版本；版本不是每次执行递增的计数。

保存失败或来源在编译期间变化时，旧成功产物和目录保留。原生定义删除时清理其衍生记录；移动定义保留 ID、丢弃路径相关构建记录，由显式重建恢复。

## 代码与使用对象

各后端代码作为 ShaderBlock 的负载保存。读取资产元数据不等于取得所有后端。ShaderBlock 的明确创建函数读取选定后端或所需组合，返回已经能够使用的 Shader；Shader 本身不读取源码或隐式编译。

创建后另需目标或特化时，通过资产侧明确产生可用结果或更新对象，不把文件来源藏在普通 Shader 使用路径。未修改代码保存时沿原文档复制，不能因一个元数据变化物化所有后端。

ShaderRef 表达资产引用、入口与阶段，不承担图形管线状态、描述符布置或调度职责。材质和管线继续由用户 Shader 解释数据，不恢复逐字段的动态元数据多态。

## 资源组与模板

ClusterShadersAsset 的 ClusterShadersBlock 保存各操作需要的入口集合；VirtualShadowMapShadersAsset 的对应 Block 保存阴影阶段引用。两者显式取得需要的 Shader 后建立原有资源组，保留既有必需角色、阶段和配对检查，不在资产层另造图形管线。

Standard、Unlit 的具体材质与顶层 Slang 属于模板项目。引擎提供只读 Shader 库和原有业务算法，项目源码负责组合。一个材质保持一个 Shader 引用，没有管线局部替换表或静默回退。

验证见 [本次验收](Asset-Verification-2026-09-16.md)。实现：[ShaderAsset](../../../src/SomeEngine.Assets/Schema/ShaderAsset.cs)、[ShaderSourceBlock](../../../src/SomeEngine.Assets/Schema/ShaderSourceBlock.cs)、[ShaderBlock](../../../src/SomeEngine.Assets/Schema/ShaderBlock.cs)、[项目源码说明](../../../docs/ShaderSources.md)。

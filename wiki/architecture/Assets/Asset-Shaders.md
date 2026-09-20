# Shader 资产与渲染 shader 资源组

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

Shader 文件记录明确的代码、入口和相关数据；应用显式创建 `Shader` 后使用相应后端内容。它遵守 [[Asset-Objects]] 的身份规则和 [[Asset-File-IO]] 的显式读取规则。

## 文件与应用对象

根信息包含后端代码项的描述、入口反射、材质相关元数据与导入记录。代码负载作为独立块保存，打开 `.Asset` 不代表所有后端代码已读出并建立应用对象。

`Shader.CreateAsync` 可以按指定目标创建所需后端；之后通过 `LoadBackendAsync` 明确加载另一个后端。已经保留的文件来源允许这种读取发生在原 `.Asset` 与 Loader 关闭之后，生命周期见 [[Asset-Loading-and-Lifetime]]。

保存未改代码时可从来源流式写出原块；重新组织代码项时，块引用与输出顺序必须由 shader 类型的写出逻辑保持一致。不能把代码的二进制合法性检查塞入普通元数据 getter。

## Shader 引用的含义

`ShaderRef` 表达磁盘 shader 引用、入口名称和阶段。它不表示 PSO、GPU 描述符位置或执行调度。实际 Shader 对象由使用这组引用的类型明确加载。

材质和管线使用用户 shader 决定数据解释。文件中的反射或导入元数据不授权资产系统在渲染热路径新增逐字段动态解释，尤其不能借此为 [[Asset-Meshes]] 固定顶点布局。图形与渲染边界见 [[Render-Boundaries]]、[[Render-Graph]]。

## 两组渲染资源

| 类型 | 记录的数据 | 显式加载后的行为 |
| --- | --- | --- |
| `ClusterShaders.Asset` | 各 Cluster 操作角色及其入口集合 | 建立该资源组使用的 Shader 对象，并按引用访问 |
| `VirtualShadowMapShaders.Asset` | 虚拟阴影各具体阶段的 ShaderRef | 建立虚拟阴影资源组需要的 Shader 对象 |

Cluster 操作需要满足已有角色与阶段组合要求；同一操作中配对入口的 shader 关系也是业务语义。虚拟阴影资源组同样保留自身的必需阶段检查。这些规则属于类型保存或加载边界，不属于通用资产依赖 getter。

资源组是实际可加载的资产类型，不是资产层重新组装图形管线的权限。Shader 对象是否在该组内复用由资源组自己明确处理，不改变 Loader 只合并字节读取的职责。

## 更新与未决部分

显式 `UpdateAsync` 更新同一个 Shader 的内容；提交后通知失败仍需要保留已提交内容，并处理其余通知和旧来源清理。行为边界见 [[Asset-Loading-and-Lifetime]]。

ShaderMetadata、反射集合、代码项和 ShaderRef 等嵌套数据的最终写入形式尚未确认。当前 Importers 的相关编译错误及验证限制由 [[Asset-Open-Questions]]、[[Asset-Verification-2026-09-10]] 记录。

代码入口：[Shader 字段](../../../src/SomeEngine.Assets/Schema/Shader.Fields.cs)、[加载与更新](../../../src/SomeEngine.Assets/Schema/Shader.Loading.cs)、[代码块处理](../../../src/SomeEngine.Assets/Schema/Shader.Programs.cs)、[Cluster 资源组](../../../src/SomeEngine.Assets/Schema/ClusterShaders.Loading.cs)、[虚拟阴影资源组](../../../src/SomeEngine.Assets/Schema/VirtualShadowMapShaders.Loading.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Code-Generation]]、[[Asset-Materials]]、[[Asset-Application-and-Rendering]]。

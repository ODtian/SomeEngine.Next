# Mesh 资产与负载范围

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

`Mesh.Asset` 持有可编辑的 Mesh 描述和负载来源；应用中的 `Mesh` 提供实际几何资源及按页读取能力。文件和对象的区别见 [[Asset-Objects]]，读取边界见 [[Asset-File-IO]]。

## 数据布局

Mesh 的元数据包括边界、顶点编码标识、区域、材质引用、负载位置与长度，以及页摘要等读取所需信息。

**当前 Mesh 的 page 位于一个大的 payload chunk 内。** page 是该负载中的范围，不能在文档中把它说成与 Texture tile 相同的独立文件 chunk。BVH 与页布局也有明确位置关系。

改变页布局可能同时影响页偏移、负载长度、摘要和 BVH。修改这些数据必须遵守 Mesh 的业务含义；通用字节编辑只保证字节分支隔离，不保证任意字节修改后仍是合法 Mesh。

## 顶点解释权

渲染架构不预设一种顶点属性布局。Mesh 可以传输不透明的用户编码内容，材质与 shader 决定怎样解释。

导入工具中的某个具体编码器可以选择自己的输出格式，但不能把该工具的格式升级为引擎管线的默认布局，也不能在运行路径读取字段元数据做动态多态。相关边界见 [[Asset-Shaders]] 和 [[Render-Boundaries]]。

## 读取与保存

打开文件取得根数据与负载来源；创建流式 Mesh 保留所需范围访问。页和 BVH 读取继续写入已有 Cluster 使用的目标缓冲并验证摘要，不要求先物化整份 Mesh。

根信息编辑后的保存可以复制来源中的原负载块。明确请求 `ReadPayloadAsync` 则会读取该负载，不能把这种显式全负载操作和页范围流式读取混为一谈。

已在内存的负载可以通过 `ReadPayload` 保留读取者，通过 `WritePayload` 进行独立写入；文件负载有对应异步入口。共享字节的成本与约束见 [[Asset-Sharing-and-Writes]]。

## 更新和渲染接入

显式更新同一个 Mesh 时，旧的页读取保留自己需要的来源；新内容的 revision 不替代实体身份或资产 ID。打开新文件版本也不要求先销毁旧 Cluster 数据。

Game world 持有实际 Mesh 与材质绑定，通过现有提取进入 Rendering world。不能因资产迁移增加旁路提交接口、固定顶点布局或另一套几何对象。场景连接见 [[Asset-Worlds]]、[[Asset-Application-and-Rendering]]。

## 当前限制与入口

区域、材质列表、页摘要等嵌套元数据的公开写入形式仍在 [[Asset-Open-Questions]] 中。不能以目前的不可变集合改动代替最终决定。

代码入口：[字段声明](../../../src/SomeEngine.Assets/Schema/Mesh.Fields.cs)、[创建与更新](../../../src/SomeEngine.Assets/Schema/Mesh.Loading.cs)、[流式处理](../../../src/SomeEngine.Assets/Schema/Mesh.Streaming.cs)、[负载来源](../../../src/SomeEngine.Assets/Pipeline/MeshPayloadSource.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Textures]]、[[Asset-Materials]]、[[Asset-Validation]]。

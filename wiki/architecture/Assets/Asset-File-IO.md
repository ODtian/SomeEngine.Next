# 资产文件、分块与显式 I/O

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

资产文件访问建立在通用二进制容器上。容器负责字节和编码，具体 `.Asset` 负责可编辑字段；两者不再由旧字段包装层捆绑。归属见 [[Asset-Responsibilities]]。

## 根数据与分块

`BinaryDocument` 保留文件来源、文件结构、根编码及分块访问。具体资产显式解码根数据，较大的负载继续通过块引用访问。打开资产不意味着读取全部块，也不意味着创建应用对象。

Serialization 仍可提供 `BinaryDocument<T>` 给其他使用者；资产 Loader 的缓存与来源保留使用不携带已解码字段的 `BinaryDocument`。这使保留来源不附带字段反序列化或字段复制。

Texture 的 tile、Mesh 的大负载与 Shader 的后端代码并非同一种块语义，分别见 [[Asset-Textures]]、[[Asset-Meshes]]、[[Asset-Shaders]]。

## 哪些操作会做什么

| 操作 | 根数据 | 负载与磁盘 |
| --- | --- | --- |
| `T.Asset.OpenAsync` | 明确读取并解码根字段 | 打开来源；不默认物化全部负载 |
| `Duplicate()` | 建立分支并共享可保留数据 | 不重新解码，不读取整个文件 |
| `RetainSource()` | 不附带字段 | 延长既有字节来源的访问期 |
| 普通属性读取与赋值 | 访问或编辑当前字段 | 不自动进行磁盘同步 |
| 显式读块、读范围 | 按调用所需使用元数据 | 读取指定内容 |
| 显式创建或加载 `T` | 按类型建立应用数据 | 由类型决定所需块和依赖读取 |
| `SaveAsync` | 编码所保留的当前内容 | 写出目标文件，可流式读取原块 |
| `Dispose` | 释放当前持有的访问 | 不自动保存 |

显式读取和来源生命周期之间的关系见 [[Asset-Loading-and-Lifetime]]。

## 保存流程

具体资产负责保留要保存的内容并建立对应 writer；公共保存实现负责同目录临时文件、写出、刷新、失败清理和成功替换。保存返回的条目对应实际写出结果，之后是否登记由 [[Asset-Import-and-Storage]] 的调用方决定。

保存需要保证所保留内容与后续编辑隔离。不能为了达到这一点默认复制整份资产，也不能先把所有未修改负载读入内存。当前来源块的写出能力保留原编码信息，验证并以有界缓冲流式复制；具体类型只提供块的位置和含义。

取消或写出失败必须清理本次临时文件，并保留已有目标文件。成功替换与索引登记是不同步骤；来源版本和输出版本的区别见 [[Asset-Identity-and-Dependencies]]。

## 只读字节与编辑

只读负载视图不授权调用方绕过写入入口修改共享底层内存。显式字节读取可以保留一个读取者；独立写入在需要时复制当前字节存储。详细规则与当前示例由 [[Asset-Sharing-and-Writes]] 维护。

World 用 `RangeReadStream` 将范围读取连接到已有 ECS 同步读取接口。它只借用来源并使用有界缓冲，释放 stream 不替调用方释放来源。失败读取不能把不完整缓冲标为有效。World 的整体写入内存成本另见 [[Asset-Worlds]]，不能由有界读取推导所有保存步骤都只用固定内存。

代码入口：[BinaryDocument](../../../src/SomeEngine.Serialization/Containers/BinaryDocument.cs)、[BinaryDocumentWriter](../../../src/SomeEngine.Serialization/Containers/BinaryDocumentWriter.cs)、[资产保存共同流程](../../../src/SomeEngine.Assets/Asset.cs)、[RangeReadStream](../../../src/SomeEngine.Serialization/IO/RangeReadStream.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Validation]]、[[Asset-Verification-2026-09-10]]。

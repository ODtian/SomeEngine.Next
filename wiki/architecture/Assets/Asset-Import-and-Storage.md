# 资产导入、存储与工程索引

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

导入把源格式转换为资产文件；存储按 ID 定位字节；工程索引记录可查找的输出。这些工作在可编辑资产之外，各有主要持有者，见 [[Asset-Responsibilities]]。

## 从源文件到可加载资产

```mermaid
flowchart LR
    Source[源文件与导入设置] --> Importer[具体导入器]
    Importer --> Asset[具体可编辑资产]
    Asset -->|SaveAsync| Output[文件及返回条目]
    Output -->|明确登记| Index[AssetProject 索引]
    Index --> Storage[IAssetStorage]
    Storage --> Loader[AssetLoader]
```

导入器处理外部源格式、编译工具链和类型业务转换。文件公共保存仍走资产基类，不能在每个导入器重新实现临时文件写出与替换。

## 源身份与输出身份

`SourceGuid` 表示源文件身份；`AssetId<T>` 表示一个输出资产身份。一份源文件可以产生多个输出，以子资产键关联到各自的资产 ID。输入文件依赖与导入设置用于判断源转换是否需要更新；它们不是应用资源之间的资产依赖图。

资产之间的依赖来自当前内容，详见 [[Asset-Identity-and-Dependencies]]。`ImportedAsset` 将输出路径、子资产键和实际保存结果交给工程，不再附带旧字段包装对象。

## 保存与工程登记

`AssetProject.RegisterAsset(AssetEntry)` 接收明确的信息，更新路径、源关联和索引。`Resolve`、列表、依赖与反向依赖查询回答目录问题，不创建应用对象。

手工创建资产的典型步骤是创建具体 `.Asset`、设置属性、保存、登记返回条目。重建已有文件的目录信息时，先使用具体类型打开，再登记其公共描述。不能为这种工作恢复已移除的泛型旧资产入口。

保存不会悄悄重写工程索引。已登记路径与资产 ID 冲突时也不能由低层保存任意改写身份；调用方应明确使用应保留的资产 ID。

## 存储实现

| 实现 | 提供的数据 | 需要理解的边界 |
| --- | --- | --- |
| `LooseAssetStorage` | 目录中已登记文件的范围来源 | 不扫描并发现未登记资产；打开检查所选文件版本与路径边界 |
| `AssetPackStorage` | 已选择资产包中的内容范围 | 使用包优先级和包版本定位内容；包版本与内层文档版本不能混同 |
| 自定义 `IAssetStorage` | 实现方能够定位的 `IRangeSource` | 仍只提供 ID 到字节的边界，遵守来源与条目的一致性 |

Loader 的缓存与取消规则见 [[Asset-Loading-and-Lifetime]]。存储不选择应用对象类型、不解码编辑字段，也不拥有调用方创建的对象。

## 工程校验与导入验证

工程可检查孤立源信息、缺失文件和悬空引用。这是离线目录工作，不能成为每次运行对象属性访问的附加扫描。格式输入、公共 API 与行为测试的统一原则见 [[Asset-Validation]]。

当前 Importers 的完整编译和依赖它的工具验证仍受嵌套元数据未决设计阻塞。这是实施状态，不能据此改成另一套导入模型，详见 [[Asset-Open-Questions]] 与 [[Asset-Verification-2026-09-10]]。

代码入口：[AssetProject](../../../src/SomeEngine.Assets/AssetProject.cs)、[存储与条目](../../../src/SomeEngine.Assets/AssetStorage.cs)、[导入结果](../../../src/SomeEngine.Assets/AssetRecord.cs)、[SourceGuid](../../../src/SomeEngine.Assets/SourceGuid.cs)。

返回 [[Asset-Files]]；关联 [[Asset-File-IO]]、[[Asset-Application-and-Rendering]]。

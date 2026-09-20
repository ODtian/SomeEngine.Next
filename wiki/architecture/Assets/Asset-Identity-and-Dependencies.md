# 资产身份、版本与依赖

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

资产身份、应用对象身份、来源版本和目录记录表达不同事实，不能互相替代。对象身份见 [[Asset-Objects]]，来源的打开与保留见 [[Asset-File-IO]]。

## 各项信息的含义

| 信息 | 表达的事实 | 变化方式 |
| --- | --- | --- |
| `AssetId<T>` | 某个资产家族中的资产身份 | 创建时确定；建立不同身份的分支时显式指定 |
| 应用对象引用 | 某一个实际 `T` 实例 | 普通赋值共享它；显式创建得到另一个实例 |
| `Name` | 当前资产的名称 | 编辑当前资产时修改 |
| `SourceVersion` | 当前保留来源文档的版本 | 打开来源时取得；不是保存后自动追踪的版本 |
| `SourcePath` | 当前打开来源的位置 | 来源信息；保存到另一位置不会悄悄重绑原对象 |
| `Describe()` | 当前内容与当前来源组成的信息 | 每次从当前名称、引用等数据得到 |
| `SaveAsync` 返回的 `AssetEntry` | 本次实际写出的名称、依赖、路径与版本 | 保存成功时返回，供调用方登记 |
| 工程索引中的 `AssetEntry` | 最近一次明确登记的目录信息 | `AssetProject.RegisterAsset` 或导入流程更新 |

空 `AssetId<T>` 表示没有资产引用，不能作为文件打开的目标。材质家族统一使用 `AssetId<Material>`；具体 Standard / Unlit 类型不建立新的身份家族，见 [[Asset-Materials]]。

## 版本与存储

Loader 以存储返回的资产 ID 和版本合并读取。散文件存储在打开时检查所选文件版本；资产包存储按包版本选择内容。**存储条目的版本不必等于包内资产文档的版本**，不能统一套上一个内部 GUID 比较。

保存返回本次输出版本；已经打开的资产仍保留其来源版本。重新打开可以得到新文件，旧对象和旧读取者保留已有访问。这一规则与对象显式更新相配合，见 [[Asset-Loading-and-Lifetime]]。

## 依赖来自当前内容

普通资产的引用枚举由生成代码从实际资产引用产生，再由基类过滤空值、去重并输出。枚举只表达引用事实，不加载依赖，不实例化对象，不判断整张对象图是否变脏。

材质的直接依赖包括父材质、shader 和明确写入的纹理引用。父材质继承后使用的全部资源由加载时解析，不能据此把直接依赖目录和加载结果混成一种数据。

World 的引用由 ECS codec 在写入 `ExternalReferenceKey` 时收集，与实际编码内容一起形成依赖。该流程不扫描一套额外的场景对象图，见 [[Asset-Worlds]]。

普通信息 getter 不承载整套业务验证。外部文件格式与类型合法性属于输入边界，依赖枚举和业务校验各有职责，见 [[Asset-Validation]]。

## 保存与登记分开

```csharp
using var asset = new StandardMaterial.Asset { Name = "stone", Roughness = 0.7f };
AssetEntry written = await asset.SaveAsync(path);
project.RegisterAsset(written);
```

`path` 和 `project` 由应用提供。保存不会更新外部工程目录。保存已经完成但尚未登记时，文件与索引可以暂时处于不同阶段；调用方应使用本次返回的条目完成登记，而不是复制旧来源条目。工程重建和导入关系见 [[Asset-Import-and-Storage]]。

代码入口：[AssetId](../../../src/SomeEngine.Assets/AssetId.cs)、[AssetEntry 与存储](../../../src/SomeEngine.Assets/AssetStorage.cs)、[公共信息与保存](../../../src/SomeEngine.Assets/Asset.cs)、[工程登记](../../../src/SomeEngine.Assets/AssetProject.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Code-Generation]]、[[Asset-Materials]]。

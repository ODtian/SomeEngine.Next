# 资产身份、版本与依赖

2026-09-16。资产身份、Block 类型身份、来源身份、内容版本和对象引用表达不同事实。

## 身份和位置

`AssetId<TAsset>` 限定语义资产家族，例如 Scene 的 `AssetId<MeshAsset>`、材质引用的 `AssetId<MaterialAsset>`。它不是某个管线表示、使用对象或页的身份。空 ID 表示没有引用，不能用于文件打开。

移动、重命名及内容编辑保留资产 ID。`Duplicate()` 建立同一身份的内容分支，`CreateCopy()` 明确产生新身份。普通 C# 引用赋值仍指向同一对象。一个资产 ID 不要求对应唯一 CLR 对象，也不建立全局资产实例缓存。

Block 的持久类型 ID 用来在一个 Asset 内索引某一种内容，不是独立资产 ID。源文件的 SourceGuid 及持久子元素映射用于保持多输出身份；来源路径、内容摘要和临时遍历下标不能替代这些事实。

## 内容版本与条目

| 信息 | 含义 |
| --- | --- |
| Asset 当前名称及 Block 内容 | 当前编辑分支的内容 |
| 来源路径与版本 | 这个对象实际打开并保留的文件 |
| `Describe()` | 当前身份、名称、Block 引用及来源信息 |
| 保存返回的 `AssetEntry` | 本次实际写出的文件、版本和引用 |
| 目录条目 | 最近一次明确登记的内容 |

保存到另一个路径不会悄悄重绑旧对象。新文件可以独立打开，旧读者继续使用其原有来源。存储所选版本和包内文档版本分别核对；包版本不冒充内部资产版本。

## 三种依赖

构建依赖记录实际来源、工具与扩展版本、参数及影响解析的搜索配置。它决定何时重新导入，不改变资产身份。

资产引用由各 Block 的当前内容提供，公共描述过滤空值、去重。它表示直接引用，不创建依赖对象，也不扫描整个工程。未知但可保留的 Block 的已保存引用仍可参与描述。目录保存的是最近登记的引用，不反向覆盖未保存编辑。

使用依赖是具体系统创建对象时真正取得的对象、页和设备资源。材质继承解析后的纹理集合与材质自身的直接引用不是同一项数据。

World 的引用由实际 ECS codec 写入外部引用时收集，不把任意 GUID 或另一套 Scene 图推导为依赖。来源 Importer 只负责自身转换所产生的直接引用；完整遍历和保存由工程流程负责。

## 保存与登记

```csharp
using var mesh = new MeshAsset { Name = "geometry" };
// 在 Block 中写入当前几何及所需表示。
AssetEntry written = await mesh.SaveAsync(path);
project.RegisterAsset(written);
```

目录查询不触发扫描或编译。保存返回值与登记事实必须一致；遇到另一个有效资产占用目标路径应明确失败，不能通过路径悄悄更换身份。来源重命名、复制及伴随文件规则见 [导入流程](Asset-Import-and-Storage.md)。

实现：[AssetId](../../../src/SomeEngine.Assets/AssetId.cs)、[公共描述](../../../src/SomeEngine.Assets/Asset.cs)、[Manifest](../../../src/SomeEngine.Assets/AssetManifest.cs)、[来源配置](../../../src/SomeEngine.Assets/MetaFiles.cs)。

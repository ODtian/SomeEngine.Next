# 资产声明与编译期生成

2026-09-30：SG 只生成编译期已经确定的重复代码。数据与执行边界见 [Asset 与 Block](Asset-Objects.md)，项目能力与一次导入的区别见 [导入架构](Asset-Import-and-Storage.md)。

## 数据声明

Asset 的公共根仅含身份、名称、资产类型和 Block 描述。业务内容直接声明在强类型 Block 上。`[AssetBlock]` 生成普通字段、集合和分支复制，`[BinarySerializable]` 和 `[BinaryFields]` 定义静态编码；需要人类可读表达的 Block 增加 `[TextSerializable]`。

```csharp
[AssetBlock, BinarySerializable, TextSerializable, BinaryFields(nameof(Points))]
public sealed partial class CurveBlock : AssetBlock
{
    public partial IList<Point> Points { get; set; }
}
```

这是外部 Curve 样例采用的声明方式。Points 使用普通 List，允许元素赋值、添加和删除；Duplicate 复制集合结构，共享已证明不可变的元素，不生成编辑包装或第二套字段根。普通集合遵守 BCL 的并发使用规则，异步创建在首次等待前复制需要固定的输入。通用编码仍允许明确的可变对象，但不为其提供自动分支隔离。

Asset 生成器拒绝把业务字段继续放在资产根上。它产生独立资产的身份、打开和保存入口，不从使用对象 T 镜像字段，也不要求资产与 T 一一对应。通用二进制生成器只处理其自己的显式声明，不再调用资产生成器注入字段。

## 类型身份、字段顺序与 AOT

Block 的持久类型 ID 由 `[AssetBlock]` 的语义名称静态生成，与文本或二进制编码的类型 ID 分开。Asset 以这个 ID 建索引，管线请求 `GetBlockAsync<TBlock>()`，由 TBlock 的静态读取函数解释内容。没有装入类型名称字符串后寻找实现、扫描程序集或动态生成代码的步骤。

生成声明明确填写 Asset.SchemaName 和 AssetBlock 的持久名称。Block 的默认二进制编码名称沿用该持久名称，显式 BinarySerializable.LogicalName 可以为编码单独命名。重命名 CLR 类型或命名空间时保留这些声明，文件类型、Block 身份和编码身份保持原值。

资产打开时读取自己的 Block 目录，后续查询复用该目录。没有进程全局的 Block 下标分配、初始化函数或读取委托注册表；`IAssetBlock<T>.ReadAsync` 是静态接口入口。仅内存中的 Block 可以不实现持久读取；已设置的实例仍可通过相同强类型 API 取得。不同项目可以在同一进程使用不同 Block 集合。

字段身份与布局由明确声明决定，不由 partial 文件路径或文件移动决定。`BinaryFields` 的缺失、重复和未知成员在编译期诊断；静态编码继续执行当前 schema 的严格检查。编码视图仅保存字节与必要定位信息，不是资产编辑对象的字段镜像。

## Importer 与项目能力

Importer 使用 `[AssetImporter]` 声明持久 ID、生产的 Block 以及可选的强类型设置提供者，并实现 `IAssetImporter<TSource, TSettings>`。项目声明 `[AssetCapabilities]` 的 partial 静态类型后，SG 检查本项目及其引用中的扩展，生成 `AddAssetCapabilities(IServiceCollection)`。

生成代码注册 Importer 与能力描述，强类型设置工厂保存在 Importer 能力描述中。`[AssetImporterSet]` 声明成员与中立标记；`[AssetCapabilities(params string[] defaultImporterSets)]` 按持久 ID 指定项目默认组合。生成的 `ImporterSetCapabilities` 提供组合 ID、名称、成员 ID、`Neutral` 与 `Default`，并在 DI 中按 ID 合并。组合声明的错误使用 `SEAC001`、重复身份使用 `SEAC002`、不可用默认组合使用 `SEAC003`。来源展开与逐项替换见 [Importer Sets](Importer-Sets.md)。Microsoft DI 用于 Importer 互相发现与按导入作用域共享实例；Importer 具有公开无参构造函数，任一带参数的构造函数使生成器报告 `SEAC004` 编译错误（ADR 0015）。诊断同时覆盖本项目与引用程序集的 Importer，包括可选参数、主构造函数和私有重载。重复注册保持幂等。能力描述可供编辑器显示来源类型、设置类型、产出 Block 和实现版本。安装多个替代生产者是合法项目能力，不等于一次导入同时启用它们。

SG 不遍历来源文件，不创建完整流程，不决定默认选择，不生成依赖业务调用或保存登记。来源持久设置与单次覆盖由项目流程解释，生产者竞争由 Context 在一次调用内检查。

项目同时生成 `BlockKind`、`ImporterKind` 两个 enum，`BlockChoices`、`ImporterChoices` 提供编译时确定的 GUI 选择数据，含枚举值、显示名称和持久 ID。`SelectImporter` 将选择变成来源配置；`ContainsBlock` 和 `GetBlockAsync` 接受 Block 枚举，并以生成的强类型分支调用对应 API。GUI 无需扫描程序集或反射枚举类型。enum 数值只在当前项目编译结果中有效，文件保存持久 ID。

```csharp
var selection = ProjectAssetCapabilities.SelectImporter(
    ProjectAssetCapabilities.ImporterKind.GltfMeshImporter);
var choices = ProjectAssetCapabilities.ImporterChoices;
```

Importer 是在库中完整编译的普通类，glTF 来源遍历及各 Block 的通用转换由 Importers 库实现；项目提供自己的材质解释，顶点编码规则由产出对应 Block 的 Importer 持有。来源设置经 [导入上下文](Asset-Import-and-Storage.md) 读取。

## 材质与使用边界

材质生成器由项目的强类型材质声明生成参数访问和转换。Standard、Unlit 的具体声明随模板交给项目维护，不内置为引擎的全局材料假设。参数访问针对 MaterialBlock，不恢复旧的嵌套资产属性模型。

针对明确声明的使用对象，分析器检查静态成员中直接或间接泄漏 Asset 及资产引用。导入、编辑、工具代码仍可正常使用资产，不用方法名猜测职责。业务程序集无须依赖 Assets。

验证使用外部程序集编译、公开 API、保存重开、诊断与实际原生 AOT 执行。不会用生成源码字符串或私有字段拼写作为验收。入口：[生成器](../../../src/SomeEngine.Generators/)、[能力测试](../../../tests/SomeEngine.Assets.Consumer.Tests/AssetCapabilityBehaviorTests.cs)、[AOT 工具](../../../tools/SomeEngine.Assets.AotSmoke/)。

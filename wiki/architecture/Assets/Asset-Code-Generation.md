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

生成代码向 Microsoft DI 注册 Importer、Importer 能力描述和导入组合描述。Importer 使用公开无参构造，每次来源导入的作用域共用一个实例；重复注册保持幂等。能力描述用具体泛型类型直接取得 Importer，并通过设置提供者的静态 JSON 编码生成强类型设置。编辑器从能力描述读取来源、设置、Block 和实现版本，来源选择决定实际执行的生产者。注册范围见 [Importer Discovery](Importer-Discovery.md)。

`[AssetImporterSet]` 声明包提供的导入组合，生成的 `ImporterSets` 数据保存组合身份、Importer 身份、中立与默认选择。来源集成将组合展开为逐个 Importer 写入 `.meta`。完整来源遍历、依赖执行和保存登记由来源集成与 Context 的对象方法完成，生产者竞争在一次导入中处理。

设置提供者通过 `GetDefaults(SourceMeta)` 取得来源的初始设置，组合展开将这些值保存到实际 Importer 项；已有项继续提供该 Importer 的明确参数。glTF 几何设置沿用来源中的切线生成默认值，后续执行读取自己的已选参数。

项目同时生成 `BlockKind`、`ImporterKind` 两个 enum，`BlockChoices`、`ImporterChoices` 提供编译时确定的 GUI 选择数据，含枚举值、显示名称和持久 ID。`SelectImporter` 将选择变成来源配置；`ContainsBlock` 和 `GetBlockAsync` 接受 Block 枚举，并以生成的强类型分支调用对应 API。GUI 无需扫描程序集或反射枚举类型。enum 数值只在当前项目编译结果中有效，文件保存持久 ID。

```csharp
var selection = ProjectAssetCapabilities.SelectImporter(
    ProjectAssetCapabilities.ImporterKind.GltfMeshImporter);
var choices = ProjectAssetCapabilities.ImporterChoices;
```

Importer 是在库中完整编译的普通类。glTF 来源遍历及各 Block 的通用转换由 Importers 库实现；项目通过自己的 Importer 表达材质解释、算法和顶点编码。编译器会话及其它外部资源由使用它们的 Importer 创建与持有，跟随本次导入作用域释放。

## 材质与使用边界

材质生成器由具体材料声明生成属性、参数解释和 `IMaterial<TMaterial>.Create`、`Update` 静态实现。创建直接构造具体材料，保留类字段默认值并应用已解析的参数与实际纹理引用；更新直接复制该具体类型的字段，并推进一次材料 Revision。`MaterialAsset.CreateAsync<TMaterial>` 与 `UpdateAsync<TMaterial>` 通过这些静态接口操作完整对象。Standard、Unlit 位于引擎 Resources，参数访问针对 MaterialBlock。

针对明确声明的使用对象，分析器检查静态成员中直接或间接泄漏 Asset 及资产引用。导入、编辑、工具代码仍可正常使用资产，不用方法名猜测职责。业务程序集无须依赖 Assets。

验证使用外部程序集编译、公开 API、保存重开、诊断与实际原生 AOT 执行。不会用生成源码字符串或私有字段拼写作为验收。入口：[生成器](../../../src/SomeEngine.Generators/)、[能力测试](../../../tests/SomeEngine.Assets.Consumer.Tests/AssetCapabilityBehaviorTests.cs)、[AOT 工具](../../../tools/SomeEngine.Assets.AotSmoke/)。

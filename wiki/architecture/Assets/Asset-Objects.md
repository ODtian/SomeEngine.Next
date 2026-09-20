# 资产对象与应用对象

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

`T.Asset` 是可编辑资产对象，`T` 是应用实际使用的对象。二者围绕同一类型声明生成数据映射，但各自有不同的身份、状态和生命周期。职责分配见 [[Asset-Responsibilities]]，磁盘身份见 [[Asset-Identity-and-Dependencies]]。

## 三种类型角色

| 类型 | 表达什么 | 主要持有的数据 |
| --- | --- | --- |
| `Asset<T>` | 所有该家族资产共有的文件与生命周期能力 | 资产 ID、名称、来源版本与路径、保留的字节来源 |
| `T.Asset` | 可编辑资产本身 | 生成的具体字段存储，以及类型需要的分块语义 |
| `T` | 应用中的实际对象 | 应用值、已解析的资源引用、对象状态、事件和设备关联 |

不存在一个额外的“文件对象”再包住另一个“可编辑对象”。`.Asset` 自身就是编辑入口。生成器可以产生私有字段存储和内部编码类型，但不能让它们成为另一套公开资产模型。

## 对象身份与数据共享

普通 C# 引用赋值共享对象身份。执行 `var other = material` 后，两者指向同一个 `Material`；调用 setter 就修改该对象。不能为了隔离数据而偷偷复制整个 `T`，尤其不能复制事件、设备关联或 GPU 对象。

资产分支通过显式 `Duplicate()` 建立。当前入口默认保留相同资产 ID，也可以传入新 ID 建立另一个资产身份。新分支先共享可保留的数据和来源；具体写入隔离规则由 [[Asset-Sharing-and-Writes]] 定义。**“建立分支”不代表任意裸嵌套赋值已经实现自动隔离。**

同一文件可以创建多个独立的 `T`，同一 Loader 也可以返回多个可独立关闭的 `.Asset`。是否在某个场景中复用一个 `Mesh` 或 `Material`，由持有这些对象的应用代码明确决定，不是 Loader 的全局对象缓存策略。

## 从资产到应用对象

显式创建或加载时，小型值进入 `T` 自己的数据；需要对象的资产引用由对应类型或集成代码解析；大型只读内容保留共享访问。`T` 仍读取某份大数据时，它就是共享读取者，之后资产上的独立写入必须考虑它。

解析规则由具体类型决定。例如，材质的 `AssetId<Texture>` 会解析为 `Texture`，而启动配置可以保留 GUID，由应用集中打开资源。生成器不能把所有 GUID 都当作需要立即实例化的对象。相关边界见 [[Asset-Code-Generation]] 和 [[Asset-Loading-and-Lifetime]]。

```csharp
using var asset = new StandardMaterial.Asset { Roughness = 0.25f };
using var branch = asset.Duplicate();
branch.Roughness = 0;

// loader 由调用方提供，用于显式解析材质引用。
using Material value = await branch.LoadAsync(loader);
Material sameObject = value;
```

此例只展示已确认的具体材质属性入口；不规定嵌套元数据的写法。材质覆盖与默认值的区别见 [[Asset-Materials]]。

## 当前代码入口

- [资产基类](../../../src/SomeEngine.Assets/Asset.cs)
- [资产身份](../../../src/SomeEngine.Assets/AssetId.cs)
- [具体资产生成](../../../src/SomeEngine.Generators/AssetGenerator.cs)
- [材质资产家族](../../../src/SomeEngine.Assets/Schema/Material.Asset.cs)

返回 [[Asset-Files]]；继续阅读 [[Asset-Sharing-and-Writes]]、[[Asset-Application-and-Rendering]]。

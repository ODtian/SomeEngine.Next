# 资产职责与数据持有

> **重构范围说明（2026-09-12）。** [冻结原则](Asset-Rebuild-Principles.md)与[执行任务](Asset-Rebuild-Tasks.md)只替换本次明确讨论的设计。本页中与之冲突的段落待更新，未涉及的架构继续适用；历史验证仅代表当时结果。现有序列化与存储能力的保持依据见[源码审查](Asset-Serialization-Review.md)。

每项数据或状态有一个主要持有者，共同流程只维护一份。拆分依据是变化原因，不是给旧包装换名字。对象定义见 [[Asset-Objects]]，这一原则与 [[DRY]]、[[SRP]] 一致。

## 职责分配

| 部分 | 负责的内容 | 边界 |
| --- | --- | --- |
| `Asset<T>` | 公共身份与名称、来源保留与释放、文件打开共同流程、原子保存共同流程、依赖去重 | 不持有另一份具体类型字段表，不负责外部索引 |
| 生成的 `T.Asset` | 具体字段、访问、浅复制、引用枚举、编码连接、返回具体类型的打开入口 | 编译期已知关系直接生成代码 |
| 具体类型的业务代码 | 分块含义、依赖解析、应用对象创建、内容更新与释放 | 不重复实现公共文件保存流程 |
| `AssetLoader` | 存储定位、并发读取合并、字节文档缓存、排队与并发限制 | 不创建、缓存或销毁唯一应用对象 |
| `IAssetStorage` | 从资产 ID 和所选条目定位字节范围 | 不解码字段，不协调对象加载 |
| `AssetProject` 与索引 | 源文件导入、输出登记、路径解析、目录依赖查询 | 不成为应用对象的持有者 |
| `BinaryDocument` 与 writer | 文件结构、根编码、分块、压缩与完整性 | 不承担资产编辑对象语义 |
| 应用与场景 | 持有实际资源对象，选择显式复用与更新边界 | 通过既有 Game world / Rendering world 通路使用资源 |

## 数据只维护一次

公共名称、ID、来源版本和来源路径由资产基类持有。生成的字段存储只保存具体类型数据，不能再维护一份独立名称、依赖缓存或修改状态。编码输出里出现 ID 和名称是文件表达，不是第二个编辑状态持有者。

Loader 缓存的是保留了根编码和字节访问的 `BinaryDocument`，不是资产字段副本。具体 `.Asset.OpenAsync` 显式解码；保留文件来源只延长字节访问。两者不能重新捆绑。详见 [[Asset-File-IO]]。

当前依赖来自当前编辑内容；工程索引保存最近一次明确登记的目录信息。它们回答不同时间点的问题，不能用旧索引反向覆盖资产当前内容。详见 [[Asset-Identity-and-Dependencies]]。

## 旧职责的迁移对照

| 旧职责 | 已确认归属 |
| --- | --- |
| `AssetFile<TFields>` 的文件访问、分块来源、释放 | 资产基类直接管理，消除字段包装层 |
| `AssetFile<TFields>` 的可编辑字段 | 生成的具体 `.Asset` |
| `AssetWriter` 的临时文件、失败清理、成功替换 | 资产基类的公共保存实现 |
| `AssetFormat<T>` 的格式信息 | 生成代码中的格式信息 |
| `AssetFormat<T>` 的打开与读写衔接 | 资产基类共同流程与具体生成入口 |
| 各 `XXData.GetDependencies` | 具体引用枚举与基类去重 |
| `AssetFile.Entry` | 当前资产的公共信息入口，如 `Describe()` |
| 各类型重复的文件打开、来源保留、writer 衔接 | 共同能力归基类，类型只保留分块业务语义 |

不保留 `ToAsset` 转换桥、旧操作注册、泛型字段包装或同义别名。这里的删除要求针对旧资产模型，不要求删除 Serialization 中仍有独立用途的泛型文档能力。

## 程序集边界

Serialization 负责通用二进制能力；Assets 建立可编辑资产语义；Importers 负责源格式工具链；Render 和应用负责接入现有渲染系统。程序集的实际引用以项目文件为准，不能因为一个类型需要特殊处理就引入循环依赖。

World 的集成采用 `WorldAsset : Asset<World>`，原因和范围见 [[Asset-Worlds]]。资源进入渲染后的职责继续遵守 [[Render-Boundaries]]，不在资产层另建提交路径。

当前实现入口：[Asset](../../../src/SomeEngine.Assets/Asset.cs)、[AssetLoader](../../../src/SomeEngine.Assets/AssetLoader.cs)、[AssetStorage](../../../src/SomeEngine.Assets/AssetStorage.cs)、[AssetProject](../../../src/SomeEngine.Assets/AssetProject.cs)。

返回 [[Asset-Files]]；关联 [[Asset-Code-Generation]]、[[Asset-Import-and-Storage]]。

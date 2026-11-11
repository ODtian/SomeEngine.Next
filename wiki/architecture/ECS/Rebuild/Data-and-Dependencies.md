# 数据归属、依赖、目录与接口

分析时间：2026-10-22 22:50 +08:00。状态：D 目标布局与接口；业务规则按本轮确认写入 [[Execution]]。

分析方法：每份数据指定唯一持有者及实际消费者；由操作顺序确定程序集依赖、字段、具体方法和生成器输出。源码与适配的比较边界见 [[Adaptation]]。

## 数据归属

| 数据 | 主要持有者 | 消费者与维护 |
|---|---|---|
| 实体位置、代数、空闲链、Used/Count | EntityStore | 随机访问、创建、销毁；死亡记录 Row 复用为 next |
| 类型 ID、大小、存储种类与类型化操作 | ComponentTypes / ComponentType<T> | 建列、复制、释放、动态入口；进程内首次注册 |
| 类型位图、紧密TypeIds、只读列分类 | TypeSet及其ComponentColumns | 泛型组合跨World复用；Archetype和GroupTables按同一组合绑定，首次实际建表时建立分类 |
| 表组合、方向边、已激活分组 | 每World的ArchetypeStore / Archetype / GroupTables | 组合去重、匹配、迁移；首次表/边建立 |
| 普通POD值数组、各Chunk行段与单类型完整性 | World按TypeId持有的PodColumnBuffer | 全局数组扩容重绑各Chunk列地址；Chunk行段增长/搬移后更新该块实际位置项；结构缓存回调使完整性失效 |
| 行身份、含引用值数组、共享编号、存在位、启用位、禁用计数 | Chunk | 行读写、遍历、填洞、增长 |
| 查询描述、CheckedArchetypes、Match[]、AllRows、匹配版本与连续值绑定 | QueryStore | 首次/增量匹配；覆盖状态在刷新时形成，连续值绑定引用POD缓冲区，范围开始重新取得实际地址 |
| 已绑定类型的位置目录 | 每World的CachedLookup数据 | 类型读取/写入范围；建立后随实际行移动/列增长维护 |
| Added/Written 行版本及摘要 | Chunk.Changes | Changed/Added；具体布局与版本规则见 [[Changes]] |
| Removed<T> 旧值 | Core 事实组件列 | 显式旧值消费与 ClearRemoved |
| 销毁事实及 reader 进度 | World 持有的 EventLog<DestroyedEntity> | Relationships、Views；各读取者独立确认，按最慢进度回收前缀 |
| 查询成员进出及 reader 进度 | QueryData 持有的 EventLog<MembershipChange> | 提取、空间维护；结构编辑根据原生匹配事实发布 |
| Parent/endpoints/payload | Core 主列 | 普通查询、直接拓扑入口和显式维护 |
| Children<D>的成员数组与局部顺序 | Core受控Children列 | 普通查询与原生变化事实；Hierarchy执行摘挂 |
| Applied、edge邻接、延迟目标索引 | Hierarchy / Relations<T> | 已应用值比较、双端遍历、即时销毁与维护 |
| 当前端点占用 | Relations<T>声明的Claim/Pair索引 | 延迟邻接期间的即时约束、最终批次校验 |
| 唯一 Shared 主值及桶/链接 | SharedValues<T> | Intern、按编号读取、存档 |
| 唯一 key 组、成员及反向组号/位置 | ValueIndex<T,TKey> | Set 取旧组，Find/Count/有序读取 |
| 缓冲元素及 Count | Core的Buffer<T>行内Items/Count及独立元素数组 | 每实体独立内容；16 B头随迁移移交、Clone独立复制；World池保存已归还的值头 |
| 资源值 | ResourceStore | 每 World 每类型一份；系统、Job、存档 |
| Sparse 值/页映射 | SparseStore<T> | 类型内连续遍历、随机访问 |
| Sparse 类型目录/实体附着链 | SparseStores | 实体销毁按真实附着项清理 |
| 顺序头、typed载荷 | CommandBuffer | 记录、顺序回放和清理 |
| 成功创建身份与完成范围 | 每批创建结果对象 | DeferredEntity跨Clear/Dispose解析，外部handle持有结果寿命 |
| Last/Current 观察版本、系统顺序 | SystemGroup | 系统成功完成后推进基线 |
| 执行连接 | World.Execution | 可选WorldExecution接口；同步/Job入口选择，公开范围一次 |
| 工作队列、资源/依赖、完成/错误 | SomeEngine.Job | 同步/异步工作；ECS提供范围和访问声明 |
| 类型绑定、codec、格式、引用重映射 | ECS.Serialization | 完整保存/读取、检查点 |

World 内嵌实际存储字段并组合跨存储调用。类型/值数据在对应持有者内修改；内部传递已取得 record ref、Chunk、row、列项和旧值，见 [[Execution]]。

## x64 数据布局

| 结构 | 字段 | 数据宽度 |
|---|---|---:|
| Entity | Id int、Generation int | 8 B |
| EntityRecord | Chunk 引用、Row int、Generation int | 16 B |
| ColumnEntry | Values Array、Presence ulong[]、Rows nint、ColumnPlusOne int、GroupPlusOne int | 32 B |
| POD行段 | Chunk引用、StartRow int、Capacity int | 16 B |
| POD复制描述 | TypeId int、Size int | 8 B |
| Match | Archetype引用、Filter引用 | 16 B |
| Query | World引用、QueryData引用、单类型缓冲区引用、Since ulong | 32 B；多列缓冲区引用及请求类型标识由共享QueryData持有 |
| CachedLookup位置目录 | POD字节偏移数组与Generation数组；含引用值为typed数组/Row/Generation项 | POD为8I B，范围一次绑定类型缓冲区基址；含引用值为16I B；类型范围首次建立 |
| ComponentChanges | Rows Array、WordChanges[]、BlockChanges[]、LastWrite ulong | 32 B |
| 行版本 / WordChanges / BlockChanges | 行版本按Written-only或Added+Written选择 | 8或16 / 24 / 24 B |
| Sparse cell / 附着项 | RowPlusOne/AttachmentPlusOne；TypeId/Previous/Next | 8 / 12 B |
| AppliedParent / AppliedEdge | 已应用主值、位置和 Generation | 16 / 28 B |
| Claim | Generation int、Edge Entity | 12 B，每个已声明约束角色 |
| Head / 邻接项 | Items/CountFlags/Generation；Other/Edge | 16 / 16 B |
| Index Membership / KeyGroup | GroupPlusOne/Place；Key/Items/Hash/Next/Count | 8 B / 20 B固定字段加Key及对齐 |
| SharedGroup / Places | 两个内联Entity、成员数组、Count；按实体Id索引的place | 32 B/组；4I B反向位置 |
| Command | Kind、内部目标两个int、TypeId、DataIndex | 20 B |
| DeferredEntity / CommandEntity | 结果引用及两个int；分别表达创建结果/命令目标 | 16 B |
| RangeAccess | World引用、DriverKind、DriverId、Start、End | 24 B |
| QueryRange | Chunk/Filter引用、Start/End | 24 B |
| 存档列绑定 | TypeId、StorageKind、codec引用 | 16 B |
| RemapEntry | SourceGeneration int、Target Entity | 12 B |

ColumnPlusOne−1 索引紧密元数据，GroupPlusOne−1 索引表的分组声明。ColumnPlusOne 表达布局中的列，Presence表达该列各行的组件存在性；Presence为空时该列的全部有效行均存在。DisabledCounts 为首次启用时建立的 int[K]；Changes 为首次存在追踪类型时建立的 ComponentChanges[K]；每类型的实际行/词/块数组按能力建立。整个直接列目录是32U B；上述可选元数据是4K/32K B，完整清零与持有计入 [[Construction]]。

检查点的D保存记录复用16 B步长，Chunk字段保持为空，Capture仅复制Row/Generation；恢复时按独立复制块的Entity行绑定目标Chunk。该表示的有效字段流量、数组容量、空闲链及恢复记录写由[Serialization](Serialization.md#内存检查点完整路径)持有，live World记录继续保存实际Chunk引用。

World按普通POD类型持有一份值缓冲区；各Chunk保存该缓冲区的数组引用和本行段Rows地址。行段保存起始行与容量，实体记录中的Row继续相对所属Chunk。含引用类型使用各Chunk的typed Values，共享编号使用各Chunk的int[]；Shared/Enableable可以组合。Enabled与DisabledCounts按紧密列号访问。数组固定地址、行段增长、缓存地址重绑与释放由[[Construction]]持有。

Chunk具有World内唯一的正int编号，供Job范围编码；ArchetypeStore分配编号并保存HasMultipleChunks。Archetype记录已建立CachedLookup的紧密类型目录，实际位置更新与分离数组费用见[[Storage-and-Queries]]。

## 程序集与外部依赖

| 程序集 | 依赖 | 提供给消费者的能力 |
|---|---|---|
| SomeEngine.ECS | Diagnostics、Job.Resources、Job | 主存储、查询、Buffer、Shared、索引、Sparse、基础命令 |
| SomeEngine.ECS.Relationships | ECS | Parent/edge 操作与派生维护；Within/Between/Product/Where/Except 配对查询；均匀网格、BVH 与空间摘要；供系统和存档调用 |
| SomeEngine.ECS.Systems | ECS、Relationships、Job.Resources、Job | 系统观察窗口、访问声明、范围工作和完成顺序 |
| SomeEngine.ECS.Serialization | ECS、Relationships、SomeEngine.Serialization | 容器格式、类型绑定、引用处理与派生恢复 |
| SomeEngine.ECS.SourceGen | Roslyn；读取上述声明 | 类型化组件/组合/查询/Job/codec 代码 |

Core声明WorldExecution访问边界，Systems提供Job连接并生成资源键。SomeEngine.Job 持有通用 EventLog<T>、读取进度与 StepAccess；具体步结果列继续由生产者持有。Job增加泛型同步范围入口与依赖完成后的分阶段访问准备，共用既有worker/队列；ECS的QueryRange作为泛型数据传入。SomeEngine.Serialization 提供基础字段编码、分段读写和文件服务；ECS.Serialization 提供 World 的具体内容组织。实际队列、BCL 集合、codec 与文件服务按相同输入加入完整账本。

## 目录和具体文件

以下目录对应当前源码；各文件按实际数据或操作划分。

```text
SomeEngine.ECS/
  World.cs, World.Structure.cs, World.Lifecycle.cs
  Entities/Entity.cs
  Storage/ComponentTypes.cs, ComponentBits.cs, Archetype.cs,
          Chunk.cs, ColumnEntry.cs, PodColumnBuffer.cs
  Queries/Query.cs, Query.Binding.cs, Query.Components.cs, Query.Chunks.cs,
          QueryChunk.Values.cs, Query.ValueSpans.cs, ReadLookup.cs
  Changes/ComponentChanges.cs, World.Membership.cs, World.BatchVersions.cs,
          ISystemVersions.cs
  Containers/Buffer.cs, Resources.cs
  Shared/SharedValues.cs
  Indexes/ValueIndex.cs, World.Indexes.cs
  Sparse/SparseStores.cs, SparseStore.cs
  Commands/CommandBuffer.cs
SomeEngine.ECS.Relationships/
  Hierarchy/Hierarchy.cs, Relations/Relations.cs
  Pairing/RelationQuery.cs
  Spatial/SpatialGrid.cs, BoundsTree.cs, SpatialMaintenance.cs
SomeEngine.ECS.Systems/
  SystemGroup.cs, SystemState.cs, QueryRanges.cs, SystemRunner.cs
SomeEngine.ECS.Serialization/
  WorldSerializer.cs, WorldCheckpoint.cs, DataWriter.cs, DataReader.cs,
  ReadIdentityMap.cs, SerializationRegistry.cs
SomeEngine.ECS.SourceGen/
  EcsGenerator.cs, ValueGenerator.cs, BundleGenerator.cs,
  JobGenerator.cs, JobEntityGenerator.cs, SerializationGenerator.cs
```

各级AGENTS.md采用[[Navigation]]的渐进式索引：根级指向领域，项目级描述本层数据与依赖，实际子目录按任务链接对应专题。算法由本页及各专题唯一持有，目录导航随文件迁移更新。

## 核心接口

以下是 D 签名设计；泛型组合的多参数重载由生成器生成。Batch 接口在一批内统一执行 G、Reserve 和绑定。

```csharp
Entity Create<T>(in T value);
void Create<T>(Span<Entity> entities, in T value);
bool IsAlive(Entity entity);
int DataGeneration { get; } // 数据恢复代数；在读取范围内捕获Job条件提交标记
void Destroy(Entity entity);
void Destroy(ReadOnlySpan<Entity> entities);
void DestroyDeferred(Entity entity); // 显式事实发布，Maintain完成拓扑清理

ref readonly T Read<T>(Entity entity);
ref T Get<T>(Entity entity); // 普通可写类型；追踪在取引用时登记
bool TryGet<T>(Entity entity, out T value);
T ReadValue<T>(Entity entity);
bool Has<T>(Entity entity);
void Set<T>(Entity entity, in T value);
void Add<T>(Entity entity, in T value);
void Remove<T>(Entity entity);
void Enable<T>(Entity entity);
void Disable<T>(Entity entity);
bool IsEnabled<T>(Entity entity);

Query Query<T>();
Query Query<T1, T2>();
Query Query<T1, T2, T3>();
Query Query(QueryDescription description);
QueryScope Query.Open();
QueryScope Query.OpenRead();
int Query.Count { get; }
bool Query.Any();

ref readonly T ReadSparse<T>(Entity entity);
ref T GetSparse<T>(Entity entity);
void SetSparse<T>(Entity entity, in T value);
bool RemoveSparse<T>(Entity entity);
SharedId<T> InternShared<T>(in T value);
ref readonly T ReadShared<T>(Entity entity);
void SetShared<T>(Entity entity, SharedId<T> value);
void SetShared<T>(Entity entity, in T value);
```

Read/Get/Set使用泛型类型声明的存储规则；普通查询提供实体/行、Chunk和连续值视图三种访问形态。连续值视图的签名与借用边界由[[Implementation]]持有。Get对索引值的调用由生成器/分析器按已确定的Set规则诊断，动态类型入口返回只读值或走实际Set操作。索引与Shared的修改入口直接维护其派生数据。

Add 表达建立缺失组件；Set 表达写入并确保列存在，已有列沿用启用位；Remove 处理列和值的寿命。Enable/Disable 操作已存列的一位，保留值。Removed、资源、Buffer、拓扑、命令和存档的接口与寿命由对应专题持有。

## 生成器的实际产物

| 生成器 | 注册/首次产物 | 运行时实际执行 |
|---|---|---|
| ComponentGenerator | int类型ID、存储种类、大小、追踪/索引/释放声明；组合ID/类型序列/位图 | Create/Copy/Move/Release/Set 的闭合类型方法 |
| QueryGenerator | 固定条件与读写类型集合，行过滤种类 | 段外绑定列/版本/位；按真实条件执行行或词循环 |
| 组合与 Bundle 生成 | 最终类型集合、按字段类型的赋值方法 | 一次最终布局定位；逐字段写值；每实际索引/版本维护一次 |
| JobGenerator | k项访问声明，闭合工作类型 | QueryRange生成、传共享写版本、类型化行计算、完成时摘要维护 |
| SerializationGenerator | 类型键/schema、列codec、Entity引用字段访问 | 每列一次分派；字段编码/解码、缓冲元素与实际引用修正 |

运行时可注册类型使用类型化操作对象与 int 目录。完整类型集合在运行时继续增长；生成器只闭合已经声明的具体 T。首次注册和 World 首次使用各有独立容量账，见 [[Construction]]。

类型注册的追踪配置分别声明 Written、Added、RemovedValue，默认值为关闭；组合声明在生成时展开为相应具体操作。Added同时需要Written候选版本；RemovedValue使用保留旧值组件。固定类型的这些能力进入闭合方法，动态类型使用已注册的实际类型化入口。

# 实现接口与交付顺序

本页把专题算法连接成可实施的工作顺序；字段和算法由链接页面持有，验收由[[Acceptance]]持有。下列接口与目录属于目标设计。

## 跨层接口

| 提供方 | 入口及输入 | 输出与寿命 | 使用方 |
|---|---|---|---|
| Core实体存储 | Resolve(Entity)、Reserve(needed)、Allocate/Release | record ref、身份；数组增长前完成Reserve | World、关系、存档 |
| Core表存储 | Find(types)、GetGroupTables(types).Get(keys)、ReserveRows、MoveKnown | 最终Archetype/Chunk/row；每次范围重取列基址 | 创建、组合修改、恢复 |
| Core值存储 | 闭合类型Read/Set/Move/Release | 值、索引、版本及容器生命周期一起完成 | 普通入口、批次、命令 |
| 查询 | Bind(description)、TryGetValues、Open、ForEach、Count、Any | 连续值span、Archetype匹配与本次Chunk范围；寿命见下文 | 同步调用、系统、Job |
| 随机类型访问 | OpenRead<T>()、OpenWrite<T>()；OpenCachedRead/Write<T>() | POD绑定基址和字节偏移/代数目录，含引用类型绑定typed位置目录；Shared绑定共享读取；Dispose释放访问 | 重复随机读取/普通值写入 |
| 生命周期 | 注册实体销毁消费者；PrepareDestroy/ApplyDestroy | 实际局部修改集与完成；每个注册服务一次入口 | Hierarchy、Relations |
| 关系 | SetParent/SetParentDeferred、Retarget/RetargetDeferred、Maintain | Core主值、原生Children、Applied、差额索引 | 业务、命令、系统 |
| Job | ExecuteRanges及分阶段访问追加 | 同步返回或工作handle；摘要后释放资源 | SystemRunner |
| 存档 | 绑定注册表与参与服务；Write/Read、Capture/Restore | 完成派生恢复的World或独立检查点 | 应用与文件服务 |

检查点实施同时沿[Serialization的原型持有者与预算适用性](Serialization.md#隔离原型的持有者与预算适用性)区分c391670紧凑基线的Generations/FreeIds表示与D的16 B记录副本、独立数组容量复用。405969c已落实D布局及退出块/服务的释放，030ac09把关系头目录增长改为在最终数组填入源字段并复用旧成员数组。容量复用沿该页的目录引用/CaptureNumber刷新规则与独立有效数量字段同步实施；每次Capture的实际临时目录、主值复制、绑定重建及结束寿命进入对应分配/保留账。D目标的达标结论使用匹配其布局及复用策略的实现证据。

OpenRead/OpenWrite先取得结构读，再用[[Jobs]]的同一资源键登记全部类型值/摘要访问。普通类型范围首次建立位置目录，随后一次绑定Used、POD基址及偏移/代数目录首项ref；含引用类型绑定typed位置目录首项ref。OpenCachedRead/Write直接绑定同一目录。Shared读取由OpenRead绑定共享主值数组与行编号。Get/Read引用随范围结束。普通值拷贝入口在调用末释放，索引和拓扑修改走结构写入口。多类型重载一次登记全部声明。Core同步模式保留相同范围寿命，执行连接选择由E承担。

动态类型调用先绑定已注册的类型化操作，后续整批复用。Parent、Children和endpoints提供普通只读查询，修改由拓扑入口生成相同Core操作；生成器为受控类型与索引类型限定可写入口。内置Children的公开成员为只读目标视图。

含新edge的拓扑批次沿[[Relations]]使用Core的PreviewEntityIds、ReserveEntities和CommitCreatedEntities。Core独立持有空闲链、Used和实体行的发布，关系层持有预选身份与最终端点校验；[[Commands]]把同一预选表接入本producer的创建结果区间。完整提交后由命令结果完成数统一发布可解析前缀。

## 实现批次

| 顺序 | 实际交付 | 依赖 | 对应验收组 |
|---|---|---|---|
| 1 | Entity/类型注册、Archetype/Chunk、方向边、单项及批量创建/迁移/销毁 | [[Data-and-Dependencies]]、[[Construction]]、[[Execution]] | 创建与布局、生命周期 |
| 2 | 查询缓存、单/多块遍历、Enableable、类型Lookup | [[Storage-and-Queries]] | 连续遍历、随机访问、查询、多块、Enableable |
| 3 | 行版本/摘要、Removed/Destroyed保留、变化消费者 | [[Changes]] | 变化与旧值、最终清理 |
| 4 | Buffer/资源、Shared、值索引、Sparse及其移动/销毁接入 | [[Containers]]、[[Shared]]、[[Indexes]]、[[Sparse]] | 四组完整生命周期 |
| 5 | Parent/Children、edge/Applied/差额索引、最终集合校验 | [[Relations]] | 原生变化事实、即时/延迟拓扑与普通销毁 |
| 6 | typed命令、临时身份、顺序回放、Clone/Prefab | [[Commands]]、[[Bulk-and-Copy]] | 命令、Clone/Prefab |
| 7 | 系统基线、Job准备阶段、范围执行、摘要完成、结构回放 | [[Jobs]] | 同步、并行、异步完整完成 |
| 8 | 版本5容器、codec、最终数组恢复、引用修正和进度模式 | [[Serialization]] | 存档、恢复及后台保存 |

第1、2批形成覆盖现有11个Core工作负载的首个完整交付，按Acceptance对四个参照逐项确认机器性能，再复用这些内核推进能力组。各批次同步生成其闭合类型代码及公开入口，生成器与手写算法共用数据布局。已有调用者按相同外部行为迁移；签名统一到本页和专题定义。每个批次使用现有对应行为测试作为首个检查入口，补齐新接口引入的可观察行为。

## 完成边界

存档身份与外部对象分别复用[[Serialization]]列出的WorldLoadOptions/IReferenceRemapper及ExternalReferenceKey/IExternalResolver入口。跨World消费者以具名DestroyedReader关联其进度。Job拓扑条件提交从[[Relations]]的Captured输入进入原CommandBuffer Composite/FIFO链，公开DataGeneration标记由World持有；字段、签名和恢复次序各由对应专题维护。

单次值修改完成主值和实际索引/版本；结构批次完成全部Reserve后向最终布局迁移并重取引用；拓扑批次先校验最终集合再提交；工作完成包含摘要，系统完成继续包含命令、维护和成功基线；读取存档完成包含全部即时派生恢复。跨层复用已取得身份、结构范围和列绑定，费用沿[[Cost-Model]]组合一次。

## 借用签名与普通值写入

以下为设计接口；泛型类型和多类型重载由相应生成器闭合。

```csharp
ReadLookup<T> OpenRead<T>();
WriteLookup<T> OpenWrite<T>();
CachedReadLookup<T> OpenCachedRead<T>();
CachedWriteLookup<T> OpenCachedWrite<T>();

// 四种范围均为ref struct，持有访问至Dispose。
ref readonly T ReadLookup<T>.Read(Entity entity);
ref readonly T ReadLookup<T>.ReadKnown(Entity entity);
ref T WriteLookup<T>.Get(Entity entity);
ref T WriteLookup<T>.GetKnown(Entity entity);
void WriteLookup<T>.Set(Entity entity, in T value);
bool ReadLookup<T>.Has(Entity entity);
void ReadLookup<T>.Dispose();

ReadOnlyBuffer<T> ReadBuffer<T>(Entity entity);
DynamicBuffer<T> GetBuffer<T>(Entity entity);
BufferRows<T> QueryChunk.GetBuffers<T>();
int BufferRows<T>.Count;
DynamicBuffer<T> BufferRows<T>.this[int row];

bool Query.TryGetValues<T>(out Span<T> values);
bool Query.TryGetValues<T1, T2>(out Span<T1> first, out ReadOnlySpan<T2> second);
bool Query.TryGetValues<T1, T2, T3>(out Span<T1> first,
    out ReadOnlySpan<T2> second, out ReadOnlySpan<T3> third);
```

Cached范围提供对应Read/Get/Set/Has/Dispose方法。WriteLookup.Set修改该范围内已经存在的普通列。类型注册限定普通可写值、索引值和受控拓扑值的入口，动态调用使用同一类型操作表。

同步Core的World.Read/Get引用沿既有结构寿命使用。TryGetValues沿同一寿命返回连续值视图：从取得视图到最后一次使用，调用方保持World的结构；下一次结构修改或World释放结束该次借用。连接Job的World.Read/Get从调用方现有访问范围取得凭据；独立调用使用OpenRead/OpenWrite，范围对象持有资源到最后一次引用使用。Open/ForEach返回或传入的ref/span由查询范围持有。值拷贝的TryGet/ReadValue在方法内完成读取与访问释放。

TryGetValues的first为普通可写值列，second/third为只读列。同步Core中，首列的修改直接作用于当前主值；追踪、索引、分组和受控修改继续由各自入口承担。单类型普通值查询可直接使用该类型的完整行缓冲区；多类型查询在匹配覆盖全部World行且各POD缓冲区形成完整行段时取得对应列。单个完整Chunk匹配时取得其实际行范围；其余情况由返回值交给调用方选择ForEach/Open。连接执行时沿ForEach/Open取得所声明的范围与访问。单类型完整性观察及绑定算法由[[Storage-and-Queries]]持有。

调用链为World预留行段与建立主值→Query绑定固定描述并按匹配版本刷新→TryGetValues复用全体行缓冲区或单块列→调用方在返回的span上计算→借用结束后继续结构修改。已绑定连续多列的查询在布局变化时刷新匹配，热调用复用当前覆盖状态和首列完整性。ForEach在回调期间持有读取范围，Open在QueryScope存续期间持有读取范围。查询缓存、完整行段条件和多块算法由[[Storage-and-Queries]]持有。

World.Set<T>的普通已有列路径只取得值Write与相应摘要资源，复用一次定位并写值。涉及附列、Shared、索引成员或受控拓扑时使用结构Write。Job连接下，独立Set先持结构Read定位普通已有列；缺列时结束本次读访问，取得结构Write并重新定位后执行最终布局写入。已有调用范围的结构修改通过其CommandBuffer提交。同步Core沿同一分支执行，普通已有值采用SetCore，结构组合采用G+SetCore。

外部输入检查集中在相应公开入口，已解析record/列/ref沿内部方法传递；Job随机访问还遵循[[Jobs]]的单范围权限。

范围入口在独立调用时取得并最终释放访问；在现有Job/查询上下文中复用已经授予的类型及单范围权限，Dispose只结束当前视图。新的依赖与更宽访问声明由外层提交阶段建立，范围内按既有声明执行。

## 程序集可见性与生成代码

Core内部的EntityRecord、Chunk、ColumnEntry与MoveKnown由核心实现持有，Relationships、Systems及Serialization通过明确的程序集内部访问使用这些数据。应用侧使用World、查询、Buffer/Lookup视图及注册声明。源生成器为应用类型生成类型化值操作、字段codec和工作类型，通过公开注册入口交给Core；操作本身接受T/ref T或实际字段数据。Core/Systems项目内的多参数泛型循环负责内部列绑定，应用工作体接收已经取得的值引用。

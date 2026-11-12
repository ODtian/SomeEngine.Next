# 键关系查询

## 类型关联

`IKeyRelation<TRelation,TComponent,TKey>` 是关系库提供的类型关联。`TRelation` 实现 `IRelation<TComponent,TComponent>`；`TComponent` 实现 ECS 的 `IIndexedComponent<TKey>`。具体实现的 `Matches(in relation, in firstKey, in secondKey)` 只取得语义参数和两个索引键。

这一关联声明：语义判断的结果由这两个组件的键及语义实例参数决定，具体键判断产生相同结果。`TKey` 沿用已有索引的 `notnull, IEquatable<TKey>` 约束。角色实体、字符串分类和业务键值类型均使用同一组接口。

`IKeyRelation` 继承 `IRelationImplementation<TRelation>`。生成器从 Roslyn 类型符号取得该关联，再生成接受语义实例与具体实现实例的 `Where`、`Except`。调用保留 `TRelation`、`TImplementation`、`TComponent`、`TKey` 和输出类型，语义方法及键方法由普通 C# 编译。

## 枚举和组合

对于直接来自 `Within`、`Between`、二元 `Product` 的查询，生成入口返回 `RelationQuery<KeyPairs<…>,(Entity,Entity)>`。`KeyPairs` 在终端打开的 `QueryScope` 中枚举 `QueryChunk`，按列读取参与集合当前使用的键，通过 `World.Index<TComponent,TKey>().Find(key)` 取得已有成员范围，再用各参与范围的 `Contains(entity)` 选取桶成员。缺少该组件的成员形成单独的临时组：其关系判断为 false，`Except` 保留这些实体参与的行。

`KeyPairsWithin`、`KeyPairsBetween`、`KeyProduct2` 分别实现对应的身份行规则，具体顺序与重复含义见[关系查询](Relation-Queries.md)。`Between` 的键判断随输出实体顺序交换输入位置；同一对键的两个方向分别求值，实际实体对使用对应方向的结论。`Within` 按完整实体身份选出升序组合；`Product` 保留输入位置与自身组合。

公开枚举入口以 `RelationQueryViews` 持有本次的原生查询范围，桶循环接收这些输入。`Within` 的单集合读取使用同一成员范围；`Between` 和 `Product` 保留各自两个成员范围。`IKeyPairing.TryPair` 以 `scoped in QueryScope` 接收第一、第二范围，身份判断与重叠去重只读取这两个集合。语义参数和具体实现沿具体泛型值传入桶循环。

对于已经组合过的具体枚举，生成入口返回 `RelationQuery<KeyFilter<…>,(Entity,Entity)>`。`KeyRelation<…>.Bind(firstChunk,secondChunk)` 一次取得两侧可选列，逐行判断接收行号和角色换位标记；来自身份行的随机读取使用同一外层范围中的 `ReadLookup<TComponent>`。两种入口均通过具体 struct 输出，复用 `Count`、`Any`、`Enumerate` 与提前结束规则。外层访问查询声明键组件的可选读取，参与查询沿用原有的成员、启用和变更筛选。

查询描述保存 World、`Query`、语义参数及具体实现，均为普通 struct。键组、组件借用、键判断与输出转发位于终端的原生读取范围中。键到实体的索引以及 typed 索引写入、组件增删和实体销毁维护继续由 ECS 持有；临时键组只描述本次参与集合。

## 代码归属与生成清单

角色头发代码属于业务例子，完整调用见[二元关系业务例子](Relation-Query-Example.md)。其中 `Hair` 提供角色键，`SameCharacterHair` 提供直接语义，`HairCharacterIndex` 提供键判断。

每个具体键实现产生八个入口：四类来源（任意二元枚举、Within、Between、二元 Product）各有 `Where` 与 `Except`。`RelationImplementationGenerator` 单独生成这些入口，与语义入口合入使用方同一命名空间中的 `RelationQueryExtensions`。业务组件专属生成类型数量为零。库共享 `KeyPairs`、`KeyFilter`、临时 `KeyRelation`、三种身份规则以及内部键组实现。

`tests/SomeEngine.ECS.Relations.Tests/KeyRelationTests.cs` 经真实 World 和公开调用检查身份行、实例参数、位置顺序、组合、提前结束和索引生命周期。字符串键的泛型关系同时检查带参数与约束的类型关联。

## 手写对照

`benchmarks/SomeEngine.ECS.Benchmarks/Relations/HandwrittenKeyPairs.cs` 是成对 benchmark 使用的手写键配对夹具，提供 `HandwrittenKeyPairs<TComponent,TKey>.Where` 与 `Except`。其数据输入是同一 World、参与 `Query`、组件键与 `ValueIndex.Find` 成员；其执行直接展开键分组、键相等、桶遍历、完整实体身份去重和输出循环。终端分别打开键组件的可选读取范围与原成员查询范围，读取原生列并用 `QueryScope.Contains` 筛选索引成员。每个 `IndexMembers` 在遍历后释放，输出状态沿 finally 回写，泛型输出接口及提前结束含义相同。

行为检查直接链接该夹具源码，并用独立的五个期望身份行核对手写与关系实现的排除结果。完整 benchmark 在固定输入、相同更新和采样配置下，分别记录两侧初始化、索引维护、分组、输出与端到端成本。

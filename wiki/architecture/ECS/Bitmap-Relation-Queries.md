# 有限类别关系

## 位数据与许可语义

`CategoryBits` 是关系库的共享位数据，包含两个 `ulong`：`Categories` 表示实体所属类别集合，`AllowedCategories` 表示它允许的目标类别集合。两者使用同一有限域，位位置 0–63 各对应一个类别，置位表示集合成员。一个实体可属于多个类别，零表示空集。业务代码选择各位的含义。

具体操作类型与语义逐项对应：

| 操作类型 | 正向条件 | 反向条件 | 成立条件 |
| --- | --- | --- | --- |
| `OneWayPermission` | `(first.AllowedCategories & second.Categories) != 0` | `true` | 正向条件与反向条件均为 true |
| `MutualPermission` | `(first.AllowedCategories & second.Categories) != 0` | `(second.AllowedCategories & first.Categories) != 0` | 正向条件与反向条件均为 true |

`Matches(in CategoryBits first, in CategoryBits second)` 实现已经形成的位数据判断，泛型 `Matches<TRelation,TComponent,TReader>(in relation,ref reader,in first,in second)` 实现按需读取组件的同一算法，Evaluate消费对应结果。库提供表中的两个具体struct，MutualPermission按两个方向短路调用OneWayPermission，位交集公式由`CategoryBits.Allows`持有。空间生成器为这两个具体类型关联对应的组证明，其余许可使用Unknown并在叶子调用自身匹配。实体位置来自最终输出行，因此`Product`保留输入角色，`Within`与`Between`使用完整实体身份顺序。

## 类型化实现

`IBitmapRelation<TRelation,TComponent,TPermission>` 继承 `IRelationImplementation<TRelation>` 和 `ICategoryBitsReader<TRelation,TComponent>`。`TRelation` 是 `IRelation<TComponent,TComponent>`；具体实现通过 `ReadCategories(in relation,in component)` 与 `ReadAllowedCategories(in relation,in component)` 分别返回两个 `ulong`。读取器可把语义实例参数计入投影后的位数据。该关联声明：组件语义与具体位读取、许可操作形成相同判断。

两个标量读取替代原先整体 `Read` 投影。单向许可只读取第一位置的 AllowedCategories 和第二位置的 Categories，双方许可在正向成立后读取反向所需字段。具体读取器以 `ref TReader` 进入静态许可调用，其状态沿原具体实例传递。自定义许可在自己的两个 Matches 入口保持相同算法，空间未知组继续调用其具体许可。

`ICategoryPermission.Evaluate<TPermission,TRelation,TComponent,TReader,TItem,TOutput,TResult>` 把具体匹配结果交给 `ICategoryPermissionOutput<TItem,TOutput>.Matched/Unmatched`。默认实现调用该具体许可的泛型 Matches；库内单向与双方实现按方向判断后直接进入对应结果回调。自定义许可沿其原有算法形成结果。具体结果类型为无状态struct，Matched/Unmatched是static abstract操作；输入以scoped in传递，`TOutput`以scoped ref进入，结果状态继续由原BitmapFilterOutput持有。

末端身份输出先通过BitmapRelation绑定两端组件引用，再把原pair的in参数和当前输出借用交给具体许可及结果操作。组件引用绑定与输出借用各在实际消费处形成。需要向后续筛选转发组件行时，静态 ReadsComponentRows 选择原有Matches和借用行转发，行的有效期继续限定为当前Add。Where/Except由现有选择类型决定匹配或其余行的输出，提前结束继续返回原输出的 bool。

`BitmapRelationGenerator` 读取这三个类型符号，生成接受语义实例与具体实现实例的 `Where`、`Except`。返回类型为 `RelationQuery<BitmapFilter<TEnumerable,TRelation,TImplementation,TComponent,TPermission>,(Entity,Entity)>`，完整保留枚举、语义、读取器、组件、操作及输出的具体类型。`BitmapFilter` 保存普通查询描述；临时 `BitmapRelation` 在回调中通过 `TryReadRef<TComponent>()` 取得组件只读引用，再进入具体许可的泛型标量读取操作。

位筛选补充组件的可选读取，参与集合保持原有成员含义。某个位置缺少该组件时关系判断为 false，`Except` 因而保留该行。计数、存在性、结果输出和提前结束沿用统一关系流程。

每个具体 bitmap 实现生成两个薄入口。下面业务例子有两个语义与两个实现，标准二元实体行生成四个普通语义入口和四个 bitmap 入口；显式边另为这两个二元语义生成四个适配入口，合计十二个方法并合入一个 `RelationQueryExtensions`。业务组件专属生成类型数量为零。

## 业务例子和手写对照

`benchmarks/SomeEngine.ECS.Benchmarks/Relations/BitmapCollisionExample.cs` 中的 `CollisionCategory`、`CollisionLayer`、`OneWayContacts`、`MutualContacts` 与两个读取器属于业务例子。共享位类型、许可操作和筛选器属于库设计。例子的分类分别占据 Character、Scenery、Sensor 三个位。

以下是这一例子的完整选择与消费调用。`candidates` 是调用方持有的有序实体对数组，`members` 是已有查询；每侧输出区容量覆盖该候选数组。

```csharp
var direct = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
var bitmap = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
var handwritten = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);

BitmapCollisionExample.Candidates(world, members, candidates)
    .Where(default(MutualContacts)).Enumerate(ref direct);
BitmapCollisionExample.Candidates(world, members, candidates)
    .Where(default(MutualContacts), default(MutualContactBitmap)).Enumerate(ref bitmap);
HandwrittenBitmapCollision.Mutual(world, members, candidates, ref handwritten);

var contactCounts = new int[entityCapacity];
BitmapCollisionConsumption.CountContacts(bitmap.Pairs, contactCounts);
```

单向例子分别使用 `OneWayContacts`、`OneWayContactBitmap`、`HandwrittenBitmapCollision.OneWay`，保留同一输入和消费流程。`HandwrittenBitmapCollision.cs` 在查询回调中直接读取业务组件字段并展开位运算循环；`BitmapCandidatePairs.cs` 保存候选身份及参与查询；`BitmapCollisionConsumption.cs` 提供 Span 输出和逐实体接触次数累加。调用方为每次完整执行提供相同成员与候选数据，统计查询进入、筛选、输出和消费成本。

`BitmapRelationTests` 通过真实 World 检查三种实现的单向及双方结果。动力学与头发业务例子组合后，单向许可得到十个实体对，双方许可得到九个实体对；`K–D` 的正向许可成立，反向许可将其从双方结果中筛出。其余九对与字面期望逐行对应。checked World 中的查询同时检查 Count、Any 与 Span 输出提前结束。

## 空间摘要的位含义

`SomeEngine.Physics` 的具体类别摘要使用同一读取器与语义实例读取两个标量，形成既有 CategoryBits 和相同节点布局。节点字段、覆盖数量、正反组证明以及与角色键的组合由 [空间筛选下推](Spatial-Filtering.md) 说明。

实际编译产物中的直接位运算、受约束具体调用和生成入口见[bitmap 编译证据](../../../docs/ECS-Relations-Bitmap-IL-2026-10-19.md)。

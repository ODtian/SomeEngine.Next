# 空间筛选下推

`SomeEngine.Physics` 把普通组件语义、类别位和角色键条件组合进 [空间候选](Spatial-Candidates.md) 的同一二叉树遍历。每个条件保持具体的 struct 类型，节点对分别保存两个方向的逐条件结论，叶子按最终实体身份顺序读取对应结论。

## 条件与组级结论

`GroupMatch` 的默认值为 `Unknown`。`None` 表示该有序节点对的全部成员关系被拒绝，`All` 表示全部成员关系成立，`Unknown` 表示继续细分或逐项判断。

`ISpatialFilter<TGroupResults>` 为此空间后端提供组件读取声明、摘要重建、单节点更新、组判断和叶子判断。`TGroupResults` 是 unmanaged 值，遍历栈直接保存它。各条件在父节点已取得的结论随子节点继承。

`SpatialComponentFilter<TRelation,TFirst,TSecond>` 的组判断为 Unknown，构造只接收具体关系值。叶子遍历把已验证成员的两个 `QueryRow` 按身份位置交给现有 `ComponentRelation`，从同一借用行读取组件引用。静态 `ReadsComponentRows` 将这一需求沿组合传递。`SpatialKeyFilter` 和 `SpatialBitmapFilter` 读取各自的具体摘要。`SpatialAnd` 的组结果为 `(TFirstResults,TSecondResults)`，继续组合产生嵌套 ValueTuple；最终组合为 Unknown 时，各条件的 All/None 仍在叶子阶段复用。

`SpatialExcept` 保留内部结果并交换返回的 All/None；Unknown 继续细分。叶子通过内部结果判断该条件，再执行排除。因此组合中已证明的键或类别条件可以直接决定该条件的通过状态。

## 同一拓扑上的具体摘要

| 数据 | 主要持有者 | 保存的内容 | 更新入口 |
| --- | --- | --- | --- |
| 业务组件与既有键桶 | World | 几何、类别、角色键及现有键索引 | 既有 ECS 组件和索引入口 |
| 空间拓扑 | CollisionTree | 节点范围、实体、父子编号、MemberCount、Height | Rebuild 与后续树修改 |
| 角色键摘要 | CollisionKeySummary | KeyCount、HasUniformKey、UniformKey 数组 | 具体持有者的 Rebuild / Refit |
| 类别摘要 | CollisionBitmapSummary | ComponentCount、类别并交集、许可并交集数组 | 具体持有者的 Rebuild / Refit |

两个摘要持有者都是 sealed 类型，各自引用同一个 CollisionTree，以节点编号读取数组。`MemberCount` 直接读取拓扑节点。树的 `CreateKeySummary` 和 `CreateBitmapSummary` 返回具体持有者，筛选描述直接保存这些引用。泛型条件链的 `RebuildSummaries`、`RefitSummary` 是组合的更新入口。

摘要的节点索引器返回数组元素的 `ref readonly`，具体组判断直接读取该元素。组件语义条件和 SpatialAll 显式报告其摘要变化状态；维护回调按块持有具体筛选值，再观察行的组件变化。

角色键叶子读取既有 `IIndexedComponent<TKey>.GetKey()`，分支合并键数和统一键。组判断调用具体 `IKeyRelation.Matches(in relation,in firstKey,in secondKey)`：

- 任一组 KeyCount 为零，结果为 None。
- 两组具有统一键且具体键判断为 false，结果为 None。
- 两组具有统一键、键覆盖全部成员且具体键判断为 true，结果为 All。
- 其余结果为 Unknown。

同角色头发相等关系由此得到同键全覆盖、不同统一键、键缺失和混合组的对应结论。节点摘要只保存从组件派生的键信息，已有 ECS 键桶继续沿其原有生命周期维护。

类别叶子通过同一个 `ICategoryBitsReader.ReadCategories(in relation,in component)` 和 `ReadAllowedCategories(in relation,in component)` 得到 `CategoryBits` 的两值。分支保存 `CategoriesAny`、`CategoriesCommon`、`AllowedAny`、`AllowedCommon`，分别对有组件成员做 OR 和 AND。完整覆盖由 ComponentCount 与节点 MemberCount 比较取得。

| 具体组证明 | None 的条件 | All 的条件 | Unknown 的条件 |
| --- | --- | --- | --- |
| OneWaySpatialPermission | 第一组 AllowedAny 与第二组 CategoriesAny 的交集为零 | 两组组件覆盖完整，且第一组 AllowedCommon 与第二组 CategoriesCommon 的交集有共同位 | 其余情况 |
| MutualSpatialPermission | 正反任一方向为 None | 正反两个方向均为 All | 其余情况 |
| UnknownSpatialPermission | 由叶子判断 | 由叶子判断 | 保持 Unknown |

SourceGen 为内置 OneWayPermission、MutualPermission 选择对应的具体组证明；其它许可保留 Unknown 组判断，叶子执行其具体静态 Matches。多位类别使用集合交集的存在语义。

## 方向与遍历

节点在数组中的分组顺序与最终 `EntityOrder` 位置分别表达空间分组和输出角色。每个栈项保存 first→second、second→first 两份 TGroupResults，逐方向进行组证明。两个方向均为 None 时跳过整组；叶子按完整 Entity 身份排序后选择对应方向。

空间范围相交和当前输入查询成员仍决定实际候选。All 省去相应条件的叶子判断；Unknown 条件继续读取其具体数据。`SpatialCandidates<TGeometry,TBoundsReader,TFilter,TGroupResults>` 保留原始来源、具体条件及结果类型，QueryCount 为一个输入。

## 构建与只读枚举

默认 `Collision.Candidates(world,bodies)` 及泛型读取器入口在终端作用域中构建当前树、填充该描述的摘要，再枚举结果。

调用方持有树时，先构建树，建立完整条件链，再显式调用 `RebuildSummaries()`。之后的 Enumerate、Count、Any 读取已经填充的数组。摘要写入由这一显式入口或条件链的 `RefitSummary(node,access)` 执行；Refit 接收一个叶子，或孩子已更新的分支。这个顺序为持续维护及并行计数、填充提供共同的读取范围。

以下是业务例子的完整组合调用；`bodies` 是既有 Body 查询，`output` 是调用方的具体实体对输出：

```csharp
var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
    .Where(default(DefaultContacts))
    .Where(default(MutualCategories), default(MutualCategoryBitmap))
    .Except(default(SameCharacterHair), default(HairCharacterIndex));
contacts.RebuildSummaries();
contacts.Enumerate(ref output);
int count = contacts.Count();
bool any = contacts.Any();
```

`MotionKind`、`Body`、`Hair`、`BodyBoundsReader`、上述语义与实现是业务例子，定义位于 `benchmarks/SomeEngine.ECS.Benchmarks/Relations/SpatialCollisionExample.cs`。库负责来源、类型化组合、摘要和遍历。

该例子中每个 AABB 的最小坐标为 `(x,0,0)`、最大坐标为 `(x+1,1,1)`。固定业务输入为：

| 实体 | x | 运动类型 | Hair 角色 | Categories | AllowedCategories |
| --- | --- | --- | --- | --- | --- |
| A1 | 0 | Dynamic | A | 1 | 3 |
| A2 | 0.2 | Dynamic | A | 1 | 3 |
| B | 0.6 | Dynamic | B | 1 | 3 |
| S | 1.4 | Static | 普通物体 | 2 | 1 |
| K | 1.7 | Kinematic | 普通物体 | 2 | 2 |
| D | 1.8 | Dynamic | 普通物体 | 1 | 3 |

完整结果为 A1–B、A2–B、B–S、S–D。`SpatialFilterTests` 通过 checked World 比较这四个字面实体对，同时加入由原查询 None 条件筛选的成员，检查已有树与当前集合的配合。其它检查分别观察同键组、不同键组、键缺失、正反单向许可，以及组合排除的 Span 输出提前结束。

## 生成入口与手写对照

`SpatialRelationGenerator` 在使用方引用 Physics 时，按 Roslyn 的语义和实现接口生成空间入口。每个二元语义或 key/bitmap 实现各生成四个薄方法：Where、Except 分别接受原 CollisionCandidates 和已组合 SpatialCandidates。普通入口追加 SpatialComponentFilter，具体实现入口追加 SpatialKeyFilter 或 SpatialBitmapFilter；返回类型保留语义、实现、许可、组证明和嵌套结果类型。当前业务夹具的四个语义与三个实现产生 28 个空间薄方法，合入其命名空间的 RelationQueryExtensions。

`HandwrittenSpatialGroups.cs` 提供独立的实际遍历，直接合写类别组证明、角色组证明、动力学判断与输出。两侧各持有自己的树和具体摘要，节点布局、正反结论、嵌套结果、查询范围和输出格式保持一致。树构建与摘要填充使用相同具体数据操作，手写节点访问与规则组合分别展开。初建、维护、查询准备和结果消费接入统一物理 benchmark 场景。

持续帧的具体摘要更新和 Job 依赖见 [碰撞树维护](Spatial-Maintenance.md)。本次公开行为、实际生成入口和 IL 接点见 [空间筛选交付检查](../../../docs/ECS-Relations-Spatial-Checks-2026-10-19.md)。

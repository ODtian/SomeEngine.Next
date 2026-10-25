# 空间候选

`SomeEngine.Physics` 使用 Game World 的公开查询构建二叉 AABB 树，并通过 `Collision.Candidates` 进入 [关系查询](Relation-Queries.md)。`CollisionBounds` 保存世界坐标下的 Min、Max；两个范围在三个轴上均相交时产生空间候选，接触面相等也计为相交。

## 输入与树

`Collision.Candidates(world, bodies)` 从参与集合读取 `CollisionBounds`。终端在现有 ECS 查询作用域内构建当前输入，然后枚举实体对。复用同一个查询描述时，树继续使用已分配的节点数组。

业务组件内持有几何时，使用 `ICollisionBoundsReader<TComponent>` 的具体 struct 读取器和 `Candidates<TComponent,TBoundsReader>`。几何读取通过 `AddReads` 加入 Access 的可选读取并集；原集合的筛选条件继续由输入查询视图维护。范围读取和 `Where`、`Except` 的组件绑定发生在相同的外层读取范围中。

调用方持有树时，先调用 `Collision.Build(world, bodies)` 或其泛型重载，再把树传入 `Collision.Candidates(world, bodies, tree)`。此入口读取该树的范围并按当前输入查询筛选叶子实体。`tree.Rebuild<TComponent,TBoundsReader>(world, bodies, reader)` 将当前 World 输入重新构建到同一对象。初建、后续修改和候选读取由调用方的执行顺序连接。

`CollisionTree` 集中持有节点数组。节点记录范围、完整 Entity、父节点、两个子节点、子树成员数和高度；叶子按最长中心轴的中位数分割，分支范围由两个子节点合并。节点编号同时用于读取这棵树的附加摘要，父子编号为成员插入、移除和祖先范围更新提供连接。`Nodes` 公开已建树的只读节点范围，读取放在构建完成与下一次修改之间。

初建先集中保存参与实体身份，再由 `ReadOnlyQueryPairJobs.ExecuteParallel` 在原 Query 读取范围内按实体取得组件，调用具体 `ICollisionBoundsReader<TComponent>` 产生叶 Bounds。节点数组保存实体与派生范围；原几何继续由 World 的组件列持有。范围工作使用所属 World 的 Scheduler，并在借用返回前完成。

`CollectBranchRanges` 先按中心轴分割上层，把下层子树分配到预留的节点区段。各 `BuildSubtreesJob` 独立排序自己的叶范围并建立子树，全部完成后从下向上合并上层节点，最后更新实体到叶的对应关系。叶范围读取、子树建立和上层合并依次完成后，公开构建入口返回可继续维护的树。实现位于 [CollisionTree.Building](../../../src/SomeEngine.Physics/CollisionTree.Building.cs)，公开行为由 [CollisionCandidatesTests](../../../tests/SomeEngine.Physics.Tests/CollisionCandidatesTests.cs) 覆盖。

遍历使用值类型节点对的栈，从 `(root, root)` 开始。同一分支展开两个子树内部和两个子树之间的组合；不同分支先比较 AABB，再按树高细分，叶子对调用 `EntityOrder.OrderPair`。每个无序实体对访问一次，输出携带完整身份。`QueryCount` 为一个输入查询，其二元行使用 `(Entity, Entity)`。

类别与键摘要、逐条件组证明及显式摘要构建见 [空间筛选下推](Spatial-Filtering.md)。普通语义和具体实现入口均可继续组合为同一个空间遍历。

## 完整业务例子

下面的 `MotionKind`、`Body`、`Hair`、`BodyBoundsReader`、`DefaultContacts`、`SameCharacterHair`、`PairList` 是业务例子代码。`CollisionBounds`、`CollisionTree`、`Collision.Candidates` 和关系接口来自库。

```csharp
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

using var world = new World();
var characterA = world.CreateEntity();
var characterB = world.CreateEntity();
var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0) });
var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.2f) });
var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(0.6f) });
var s = world.CreateEntity(new Body { Kind = MotionKind.Static, Bounds = Box(1.4f) });
var k = world.CreateEntity(new Body { Kind = MotionKind.Kinematic, Bounds = Box(1.7f) });
var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic, Bounds = Box(1.8f) });
world.Add(a1, new Hair { Character = characterA });
world.Add(a2, new Hair { Character = characterA });
world.Add(b, new Hair { Character = characterB });
var bodies = world.Query(world.QueryDefinition().All<Body>());
var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
var pairs = new List<(Entity, Entity)>();
var output = new PairList(pairs);

Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
    .Where(default(DefaultContacts))
    .Except(default(SameCharacterHair))
    .Enumerate(ref output);

world.ReleaseQuery(bodies);
Console.WriteLine(pairs.Count); // 5

static CollisionBounds Box(float x) => new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

public enum MotionKind { Dynamic, Static, Kinematic }
public struct Body : IComponent
{
    public MotionKind Kind;
    public CollisionBounds Bounds;
}
public struct Hair : IComponent { public Entity Character; }
public readonly struct BodyBoundsReader : ICollisionBoundsReader<Body>
{
    public CollisionBounds Read(in Body component) => component.Bounds;
}
public readonly struct DefaultContacts : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second) =>
        first.Kind == MotionKind.Dynamic || second.Kind == MotionKind.Dynamic;
}
public readonly struct SameCharacterHair : IRelation<Hair, Hair>
{
    public bool Matches(in Hair first, in Hair second) => first.Character == second.Character;
}
public struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
{
    public bool Add(in (Entity, Entity) pair) { pairs.Add(pair); return true; }
}
```

该业务输入输出 A1–B、A2–B、B–S、S–D、K–D。对应可执行调用位于 `tests/SomeEngine.Physics.Tests/CollisionCandidatesTests.cs`，同时覆盖当前查询对已有树的成员筛选。

## 手写宽相对照

`benchmarks/SomeEngine.ECS.Benchmarks/Relations/HandwrittenSpatialCollision.cs` 提供同算法、同节点布局的实际手写遍历，动力学与角色键判断直接写在叶子对处理中。两侧分别调用 `Collision.Build` 持有自己的树，进入相同的 World 读取范围、当前集合成员检查和实体对输出。行为检查将两侧结果分别与上面的五个字面实体对比较。

这个夹具作为后续成对测量的输入：初建计入各自的 `Build`，持续成本计入各自的维护、查询准备、宽相、筛选与输出，接触消费使用同一步骤。性能证据按 [性能证据](../../harness/Harness-Performance-Evidence.md) 的公共队列形成。

成员、范围及具体摘要的持续帧更新见 [碰撞树维护](Spatial-Maintenance.md)。

真实三角形的最近命中、球形扫掠与支撑变换读取见 [场景表面查询](Scene-Surface-Queries.md)。

候选分区到静态检测类型的 count、prefix、write 及用户检测入口见 [碰撞类型分桶](Collision-Pair-Bins.md)。

均匀网格与分层网格的配置、单元维护、跨层枚举和关系入口见 [碰撞网格](Collision-Grids.md)。

# Lists

## Current design
- 父计算通过 `Each(query)` 只依赖查询成员；每行是按实体做 key 的子视图，读取自己实体的数据，某一行变化只重跑该行。
- `Each(query)` 的成员依赖读取 `World.MembershipVersion(query)`，观察范围与版本比较见 [[architecture/someui/Dependencies|Dependencies]] 的 Query membership。
- `VirtualList` 根据自身 `LayoutRect`、滚动偏移与固定或估计的行高给出可见范围：

```csharp
foreach (int i in Visible(list, count))
    using (Key(items[i].Id)) Row(items[i]);
```

- 滚动偏移是组件，滚动时列表视图重跑，只创建或释放进出视口的行。

## 查询列表实现（#12）

父视图在 `Build` 中声明查询结果的全部行。`Scope.Each` 返回 `QueryRows`，枚举值是 `Entity`。
显式 key 包含实体的 index 与 generation，同一个实体在结果中的位置变化时保留行节点和局部状态。

```csharp
public readonly record struct EnemyList(QueryHandle Query) : IView, IComponent
{
    public BuildTask Build()
    {
        foreach (Entity enemy in Scope.Each(Query))
        {
            ulong key = ((ulong)(uint)enemy.Generation << 32) | (uint)enemy.Index;
            using (Scope.Key(key))
                Scope.View(new EnemyRow(enemy));
        }
        return default;
    }
}

public readonly record struct EnemyRow(Entity Source) : IView, IComponent
{
    public BuildTask Build()
    {
        int health = Scope.Read<Health>(Source).Value;
        // 在本行节点上写入由 health 得到的显示数据。
        return default;
    }
}
```

查询成员增删、顺序变化或启用过滤结果变化，使父视图在下一帧重新声明行。
行参数保留源实体身份，血量变化由该行的 `Scope.Read` 依赖触发。
新行在父构建中完成首次构建，离开结果集的行沿 `ElementTree` 释放；
重排沿既有 key 与层级匹配保留节点。成员观察和依赖缓冲由
[[architecture/someui/Dependencies|Dependencies]] 维护。

公开验收：[EachDependencyTests](../../../tests/SomeEngine.Views.Tests/EachDependencyTests.cs)。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Node-Identity|Node Identity]]

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

## Solid 查询列表（#57）

Solid 的 `For` 与 `Index` 可直接接收 `() => Scope.Each(query)`，行元素为 `Entity`。
API、元素或位置身份、getter 读取和行生命周期由
[[architecture/someui/Solid-Frontend|Solid Frontend]] 的 For 与 Index 节维护。

```csharp
For(() => Scope.Each(enemies), (enemy, index) =>
{
    Text(() => Scope.Read<Health>(enemy).Current);
    Text(index);
});
```

## 滚动容器与可见行（#45）

`SomeEngine.Elements` 提供 sparse 视图 `ScrollView` 与 `VirtualList(float RowHeight)`，
两者的 `Children` 为 `ChildSource.Caller`。`ScrollView.Build` 使用公开的 `Scrollable`
声明滚轮和拖动行为；调用方通过 `Content()` 声明内容，通过 Box 修饰设置外框和内边距。
视口的范围、偏移和子节点坐标沿 [[architecture/someui/Layout|Layout]] 的滚动布局处理。
后代的命中与 Surface 绘制沿祖先视口外框裁剪，绘制规则由
[[architecture/someui/Render-Path|Render Path]] 维护。

```csharp
using static SomeEngine.Elements.ScrollViewFunctions;
using static SomeEngine.Elements.VirtualListFunctions;

// 在列表视图的 Build 中，用外层布局分配视口尺寸。
using var content = VirtualList(32).Size(240, 160).Content();
foreach (int i in Visible(content.Entity, items.Count))
{
    using (Scope.Key(items[i].Id))
        Scope.View(new ItemRow(items[i]));
}
```

`Visible(Entity list, int count)` 返回可直接 `foreach` 的 `VisibleRange`，
公开 `Start`、`End` 与 `Count`，区间为 `[Start, End)`。它通过 Scope 读取列表的 RowHeight、
LayoutRect、ScrollOffset 和 Box，把读取依赖登记到调用它的计算。视口高度为 LayoutRect
高度扣除上下内边距；索引以 Y 偏移和统一行高计算，包含部分可见行，并在两端各保留一条
缓冲行。范围限制在总项数内；项数或可见高度为零时返回空范围。
列表缩短时按新的内容高度计算范围，布局阶段同时收紧实际偏移。

`Visible` 在列表实体上写入总项数与首行索引，由列表测量读取。
`VirtualList.Measure` 返回 `count * RowHeight` 的完整内容高度，
按首行索引及声明顺序摆放现有行，各行使用统一的 RowHeight 约束。
行提供一个参与布局的根节点，复合行在这个布局根内继续组合。
行宽由行自身的自然测量给出，内边距沿统一 Box 路径加入内容原点。
RowHeight 同时用于可见性和摆放，可由应用提供固定值或估计值。

首次构建通过空范围登记内容长度；布局产生 LayoutRect 后，下一次重建补齐可见行。
帧顺序沿输入 → 重建 → 布局：输入在布局阶段形成 ScrollOffset，读取范围的计算在
下一次重建重新声明行。Key 匹配保留交集行实体，内容作用域收尾释放离开的子树，
新增行完成首次构建。保留行的相等参数沿既有参数比较保持行显示数据，
行自己的数据依赖变化时独立更新。

开发构建通过 Core 的 DevelopmentCheck 报告行高、项数、列表实体与作用域使用错误。
公开验收为 [VirtualListTests](../../../tests/SomeEngine.Elements.Consumer.Tests/VirtualListTests.cs)、
[ScrollViewTests](../../../tests/SomeEngine.Elements.Consumer.Tests/ScrollViewTests.cs) 与
[ScrollClippingTests](../../../tests/SomeEngine.Elements.Render.Tests/ScrollClippingTests.cs)。
[ListChecks](../../../tests/SomeEngine.Elements.Consumer.Tests/ListChecks.cs) 将内置和公开 API
重写的两个控件共同编入 Views NativeAOT 冒烟，覆盖 10000 项、滚动行生命周期和独立行数据更新。

此前独立 SomeUI 的 VirtualizedListSemantics 用于核对索引、偏移和一行缓冲的计算。
本实现以视图参数、公开读取依赖、Content 与 Key 表达调用方行声明，布局阶段统一写入 ScrollOffset。

## Relations
[[architecture/someui/Dependencies|Dependencies]] · [[architecture/someui/Node-Identity|Node Identity]]

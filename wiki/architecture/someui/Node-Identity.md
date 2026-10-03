# Node Identity

## Definition
节点身份决定一次构建声明的节点与上次的哪个实体对应，由 key 加出现次数确定。
调用位置 key 由源生成器的拦截器提供，各种写法沿同一组身份规则匹配实体。

| 场景 | 身份来源 |
|---|---|
| 普通 struct、Compose、SwiftUI 写法 | 调用位置常量加出现次数；`if`/`else` 两个分支的调用位置不同，身份互相独立 |
| Solid 写法 | 组件只运行一次，身份在创建时确定；`Show`、`For`、`Index` 自行决定子节点身份 |
| 列表 | 显式 `using (Key(id))` |

## Invariants
- 实际身份是 key 加该 key 在当前父节点下本次的出现次数，保存在子实体的 `ElementKey` 组件中。上次的子节点就是层级中的现有子节点，不另存组合记录。
- 底层公开 `View<T>(in T view, ulong key)`；未经拦截的 `View<T>(in T view)` 使用 key 0，等同于按出现顺序匹配。调用位置常量和未经拦截调用的诊断由 #19 接入。
- 一个产出带状态节点的辅助函数，在同一位置被有条件地多次调用时，需要 `Key` 或 `[Inline]` 分组。
- 同一父节点下重复 key 的兄弟数量变化且它们带有局部状态时，由开发检查报告。每项使用独立 `Key(id)` 的列表按各项身份增删。

## 身份与子节点匹配实现（#8）

```csharp
public struct ElementKey : IComponent
{
    public ulong Key;
    public int Occurrence;
}

public static Entity View<T>(in T view, ulong key)
    where T : struct, IView, IEquatable<T>;
public static KeyScope Key(ulong key);
```

- 出现次数从 0 开始，在当前父节点本次构建中按最终 key 分别计数。
  默认调用和显式 key 0 共用出现次数；相同 key 的多个 `Key` 块也共用出现次数。
- `using (Scope.Key(id)) Scope.View(new Row(...))` 将该子节点的 key 设为 `id`。
  嵌套 `Key` 按进入顺序组合前缀；块内显式的非零调用 key 与该前缀组合，
  保留同一列表项内各调用位置的身份。最终的 64 位 key 写入 `ElementKey`。
  `KeyScope.Dispose` 恢复外层前缀；子视图自身的构建从自己的父节点上下文开始。
- 每次声明从层级中尚待匹配的子节点查找 `(Key, Occurrence)`。
  类型相同的匹配项复用实体，参数更新沿
  [[architecture/someui/Build-Modes|Build Modes]] 的比较与构建规则处理。
  匹配项的类型变化时，使用 `Hierarchy<ElementTree>.DestroySubtree` 释放旧实体和后代，
  随后创建新视图；新增声明同样创建并当场完成首次构建。
- 声明顺序由 `Hierarchy<ElementTree>.SetParent(world, child, parent, insertIndex)` 维护。
  按 [[architecture/someui/Child-Source|Child Source]] 结束一次子节点声明时，
  尚待匹配的旧子节点及其整个子树通过同一层级接口释放。
  视图参数、`ElementKey` 和 `ElementTree` 持有节点数据；出现次数只在本次构建期间计数。
- `Views.Spawn` 创建的根也带有 `ElementKey`，其值为 key 0、出现次数 0。

`LocalState : ITag` 标记视图自有的局部状态。自定义视图用 ECS 组件保存状态时，
在该视图实体上调用 `world.AddTag<LocalState>(entity)`；状态值继续由其组件持有。
开发检查沿 `ElementTree` 查找该节点和后代中的标记。
已有同 key 的兄弟组数量变化，且变化前或声明后的数量大于 1、组内带局部状态时，
Core `DevelopmentCheck` 报告 key、前后数量与使用独立 `Key` 的提示。
首次创建兄弟组与独立 key 的单项增删沿正常创建、释放路径处理。
数量检查及其构建前计数由 `DEBUG` 条件编译控制，shipping 使用同一身份匹配规则。

公开行为测试见 `tests/SomeEngine.Views.Tests/NodeIdentityTests.cs`；
`FirstBuildChecks.cs` 将 keyed 重排、状态保留、删除和类型替换同步纳入 ECS NativeAOT 冒烟。

## Relations
[[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Compose-Frontend|Compose Frontend]] · ADR 0013

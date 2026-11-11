# 可存储的访问描述

`QueryDescription` 表达组件、共享值、分组和变化筛选；`World.Query(description)` 绑定为可复用 `Query`。`Query.Since(version)` 保存本次观察窗口，`Query.MembershipVersion` 读取该查询的成员版本。`SystemState` 保存系统级版本和生命周期状态。

这些值用于稍后建立访问范围。实际引用与列由 `Query.OpenRead()`、`Query.Open()` 或具体 `IComponentAction` 执行时取得。

```csharp
Query moving = world.Query(new QueryDescription().With<Position>().With<Velocity>());
var action = new Move();
moving.ForEach<Position, Velocity, Move>(ref action);

struct Move : IComponentAction<Position, Velocity>
{
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        for (int row = 0; row < positions.Length; row++)
            positions[row].Value += velocities[row].Value;
    }
}
```

只读消费使用 `using QueryScope rows = moving.OpenRead()` 并枚举 `QueryChunk`。可写消费使用 `Open()`；按实体读取和写入使用类型 lookup。数据引用的寿命由 [活跃范围](Active-Owner-Borrow-Creation.md) 持有。

源码：[Query 与描述](../../../src/SomeEngine.ECS/Queries/Query.cs)、[具体动作](../../../src/SomeEngine.ECS/Queries/Query.Components.cs)。

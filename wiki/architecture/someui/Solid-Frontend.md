# Solid Frontend

## Definition
Solid 写法的组件是普通函数，只运行一次；变化由计算直接写到目标节点的目标字段。

```csharp
static void Counter(string label, Entity player)
{
    var count = new State<int>(0);
    var hp = Memo(() => Read<Health>(player).Current);

    using (Row().Content())
    {
        Text(() => $"{label}: {count.Value}  HP {hp()}");
        Button("+", onClick: () => count.Value++);
        Show(() => count.Value > 10, () => Text("太多了"), fallback: () => Text("还不够"));
        For(() => Each(enemies), (enemy, index) => EnemyBar(enemy, index));
    }
}
```

## Invariants
- 原语：`Effect`、`Memo`、`Show`、`Switch`/`Match`、`For`、`Index`、`Dynamic`、`Insert`、`Untrack`。状态直接使用 `State<T>`。
- `Show`、`Switch` 只在登记依赖的状态下求条件；每个分支是 `Once` 模式的子视图，子内容不登记依赖。条件真假变化时释放并重建分支。
- `For` 按元素做 key，每行创建一次、重排时移动；`Index` 按位置做 key，元素以 getter 提供。行内容同样不登记依赖。
- `Insert` 是 `Rerun` 模式的子视图，用于动态插入与跨前端传值。
- 可响应的属性提供 `T` 与 `Func<T>` 两个重载；lambda 不能隐式转换为自定义包装类型（实测 CS1660）。
- 写入 `State<T>` 立即可读；Effect 在重建阶段执行，按帧合并；Memo 读取时当场重算。
- 导出给其它前端使用的组件标记 `[Component]`：只有来自 Compose 或 SwiftUI 函数体的调用被拦截为 `Once` 模式的视图；Solid 组件之间的调用保持内联。
- 闭包只在组件运行时分配一次，计算重跑调用同一委托。

## Relations
[[architecture/someui/Computations|Computations]] · [[architecture/someui/Frontends|Frontends]]

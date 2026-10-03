# Compose Frontend

## Definition
Compose 写法使用 `[Composable] static` 方法。源生成器为不返回值的函数生成视图 struct，拦截器把调用点改写为 `View(new X_View(...))`。

```csharp
[Composable]
static void Game(Entity player)
{
    var paused = Remember(static () => MutableStateOf(false));
    Hud(player, paused.Value, v => paused.Value = v);
}
```

## Invariants
- 参数可以是普通值，也可以是 `State<T>`；比较规则见 [[architecture/someui/Build-Modes|Build Modes]]。
- 返回值或标记 `[Inline]` 的 `[Composable]` 函数只形成 key 前缀分组，不创建实体，可以有返回值。
- `Remember` 的值保存在视图实体的状态存储中，按调用位置常量加出现次数识别；调用 `Remember` 的函数必须是 `[Composable]`。
- `MutableStateOf` 产出 `State<T>`。
- `[Composable]` 内容参数由组件调用，作为独立子视图执行，按引用比较；捕获变量的 lambda 允许使用，分析器提示每次调用方重跑都会分配。
- 拦截覆盖 `[Composable]` 调用；函数体内产出节点的调用与其它写法一样获得调用位置常量。

## Current design
分析器在以下情况报告错误：`[Composable]` 以方法组、委托或取地址方式使用；非 `static`；在其它生成器产出的代码中调用；项目未开启生成命名空间的 `InterceptorsNamespaces`（由包内 buildTransitive props 和仓库 `Directory.Build.props` 设置）。编译通过即每个调用都已拦截。

## Relations
[[architecture/someui/Frontends|Frontends]] · [[architecture/someui/Node-Identity|Node Identity]] · ADR 0013

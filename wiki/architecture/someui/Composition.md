# Composition

## Definition
库与用户代码对等：内置元素、控件、布局和行为都是用公开部件组合出来的普通代码。用户需要不同的东西时，用同样的部件组合出自己的版本。

```csharp
public record struct IconButton(string IconName, string Label) : IView, ISparseComponent
{
    public BuildTask Build()
    {
        Pressable(Entity);
        using (Row().Gap(4).Padding(8).Content()) { Icon(IconName); Text(Label); }
        return default;
    }
}

using (Dialog().Content())
{
    DialogTitle("设置");
    VolumeSlider(settings);
    using (DialogActions().Content()) { Button("确定"); IconButton("star", "收藏"); }
}
```

## Invariants
- 行为与外观分开：`Pressable`、`Draggable`、`Focusable`、`Scrollable`、`TextEditing`、`Selectable` 是可挂到任意节点的行为部件。
- 内置控件是行为部件、元素与布局的薄组合；复合控件拆成由调用方拼装的复合部件，部件之间通过 `ReadUp<T>()` 共享数据。
- 组件需要决定内容放置时机、次数或参数时，接收内容函数参数（推荐 `static` lambda 加状态参数）。
- 共享的是数据：主题等环境数据由 `entity.Provide(value)` 写在祖先上，通过 `ReadUp<T>()` 读取；用户可以定义自己的主题类型。
- 内置控件的函数按控件放在各自的静态类中，用户可以只导入需要的部分，或在自己的命名空间提供同名组件。

## Non-goals
- 替换（`Replace`/`Map`）、覆盖层（`Override`）、具名内容槽。

## 环境数据入口（#13）

```csharp
public static class EnvironmentExtensions
{
    public static Entity Provide<T>(this Entity node, in T value) where T : struct;
}

// 当前构建的调用方在节点上提供主题数据。
Scope.Entity.Provide(new Theme(Accent: 0xff336699));
// 后代构建读取最近节点的数据，Theme 为用户定义的 ECS 组件。
ref readonly Theme theme = ref Scope.ReadUp<Theme>();
```

`Provide` 将值写入当前构建的 `Scope.Write<T>(node)` 临时副本，并返回原节点以继续链式调用。
成功构建结束时按 `Write` 的比较提交规则保存组件并推进写版本；表格与 sparse 使用相同入口。
同一节点的同一环境组件由调用方写入，最近提供者的整个组件作为读取结果。
读取依赖和父链变化处理见 [[architecture/someui/Dependencies|Dependencies]]。

公开验收：[EnvironmentTests](../../../tests/SomeEngine.Views.Tests/EnvironmentTests.cs)。

## Evidence
对等由程序集边界保证，见 [[architecture/someui/Assemblies|Assemblies]]；外部重写测试见 [[architecture/someui/Testing|Testing]]。

`Group` 是已实现的透明容器，用户视图也可声明 `ChildSource.Caller`。
当前底层组合入口是 `using (Scope.View(new Group()).Content()) { Scope.View(...); }`；
内容作用域和公开行为证据见 [[architecture/someui/Child-Source|Child Source]]。

Compose 组件通过 `[Composable] Action<T>` 等内容参数选择内容的放置时机和数据。
内容调用形成独立子视图，委托按引用比较，推荐 `static` lambda 加显式状态参数。
API、节点身份、跨程序集转交及公开行为验收见
[[architecture/someui/Compose-Frontend|Compose Frontend]] 的内容函数参数章节。

## Relations
ADR 0011 · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]

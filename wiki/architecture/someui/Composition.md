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

## Evidence
对等由程序集边界保证，见 [[architecture/someui/Assemblies|Assemblies]]；外部重写测试见 [[architecture/someui/Testing|Testing]]。

`Group` 是已实现的透明容器，用户视图也可声明 `ChildSource.Caller`。
当前底层组合入口是 `using (Scope.View(new Group()).Content()) { Scope.View(...); }`；
内容作用域和公开行为证据见 [[architecture/someui/Child-Source|Child Source]]。

## Relations
ADR 0011 · [[architecture/someui/Child-Source|Child Source]] · [[architecture/someui/Input-and-Behaviors|Input and Behaviors]]

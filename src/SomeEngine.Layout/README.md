# SomeEngine.Layout

从 Canvas 的视图根测量布局，写入画布坐标的 `LayoutRect`。
将 `LayoutSystem` 放在 `ViewRebuildSystem` 后，每个父布局通过 `LayoutPass`
测量并摆放自己的子节点。用户视图实现 `ILayout`，使用与内置 `Absolute` 相同的类型分派。

```csharp
using static SomeEngine.Layout.AbsoluteFunctions;
using SomeEngine.Layout;
using SomeEngine.Views;

// 在视图的 Build 中：
using (Absolute().Padding(16).Content())
{
    Absolute().Size(100, 50).Position(10, 20);
    Absolute().MinSize(80, 40).MaxSize(200, 120).Position(200, 20);
}
```

协议、约束与节点组件使用 `SomeEngine.Layout` 命名空间，由 Views 基础层提供，
使构建与测量共用视图类型表，并保持 Layout → Views 的程序集依赖。
`Rect` 和 `Thickness` 来自 Core。应用持有 Canvas 的目标、尺寸和缩放；
修饰通过 `Scope.Write` 提交，布局系统持有 `LayoutRect` 的写入职责。

详细接口、尺寸语义、透明组合与公共行为验收见
[Layout](../../wiki/architecture/someui/Layout.md)。

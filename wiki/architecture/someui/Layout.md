# Layout

## Definition
布局采用协议：布局数据（视图参数）实现 `ILayout`，布局系统按类型分派，不识别具体布局。模型为约束向下、尺寸向上、父节点摆放子节点的单遍计算。

```csharp
public interface ILayout
{
    Size Measure(ref LayoutPass pass, in Constraints constraints);
}

public ref struct LayoutPass
{
    public Entity Node { get; }
    public LayoutChildren Children { get; }                      // 已跳过透明节点
    public Size Measure(Entity child, in Constraints constraints);
    public void Place(Entity child, Vector2 position);
    public ref readonly T Read<T>(Entity entity) where T : struct;
}
```

## Invariants
- 多种布局并存且对等：内置 `Flex`、`Grid`、`Stack`、`Absolute`，叶子 `Text`、`Image` 在自己的 `Measure` 中测量内容；用户布局（例如环形布局）写法相同。`Row()`、`Column()` 是产出 `Flex` 的普通函数。
- 分派经 `ViewType` 的类型表取得 `Measure` 函数指针，不装箱。没有实现 `ILayout` 的节点对布局透明，其子节点直接参与上一级布局。
- 嵌套不受限制：父布局只调用 `pass.Measure(child, ...)`。
- 子节点的布局参数由父布局类型定义（`FlexItem`、`GridCell`、`AbsolutePosition`），由调用方通过修饰写入。
- `Box` 的尺寸限制与内边距由 `LayoutPass` 在每个节点自身 `Measure` 前后统一处理。
- `LayoutRect` 与 `LayoutCache` 只由布局系统写入。约束相同且节点及子节点数据版本未变时复用缓存；收到固定尺寸约束的节点成为重排边界。
- 根节点带 `Canvas` 组件，记录渲染目标（屏幕、render texture 或世界空间实体）、尺寸与缩放；分屏与世界空间 UI 即多个 `Canvas`。

## Relations
[[architecture/someui/Modifiers|Modifiers]] · [[architecture/someui/Render-Path|Render Path]] · [[architecture/someui/Lists|Lists]]

# Value Ops and Curves

## Definition
值运算由静态抽象接口描述。求值系统以 `(T, TOps)` 为泛型参数生成专门化代码，没有接口调用，也不装箱。

```csharp
public interface IValueOps<T> where T : unmanaged
{
    static abstract T Lerp(in T a, in T b, float t);
    static abstract T Add(in T a, in T b);
    static abstract T Subtract(in T a, in T b);
    static abstract T Scale(in T a, float s);
    static abstract float Distance(in T a, in T b);

    static virtual void Lerp(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float t, Span<T> result) { /* 默认逐值实现 */ }
    static virtual void Add(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float weight, Span<T> result) { /* 默认逐值实现 */ }
}
```

## Invariants
- 内置提供 `float`、`Vector2/3/4`、`Quaternion`、`Color`、`Thickness`、`Rect`、`Matrix3x2` 与变换类型的 ops struct。用户类型写自己的 ops struct 即可，地位与内置相同，不需要注册表。
- 姿势动画使用批量版本，并为常用类型提供 SIMD 实现；属性动画使用逐值版本。
- 曲线是数据：`Curve` 记录种类（线性、缓动、弹簧、关键帧、函数）与参数。关键帧引用实例缓冲或资产数据；自定义曲线以 `delegate*<float, float>` 保存，不装箱。弹簧用 `Distance` 判断是否收敛。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/animation/Pose-Animation|Pose Animation]]

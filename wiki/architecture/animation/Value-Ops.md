# Value Ops and Curves

## Definition
值运算由静态抽象接口描述。求值系统以 `(T, TOps)` 为泛型参数生成专门化代码，内置与用户的运算类型都采用 struct。

```csharp
public interface IValueOps<T> where T : unmanaged
{
    static abstract T Lerp(in T a, in T b, float t);
    static abstract T Add(in T a, in T b);
    static abstract T Subtract(in T a, in T b);
    static abstract T Scale(in T a, float s);
    static abstract float Distance(in T a, in T b);

    static virtual void Lerp<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float t, Span<T> result)
        where TOps : struct, IValueOps<T> { /* 调用 TOps.Lerp 的默认逐值实现 */ }
    static virtual void Add<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float weight, Span<T> result)
        where TOps : struct, IValueOps<T> { /* a + b * weight */ }
}
```

C# 要求通过类型参数调用静态抽象成员，所以默认批量方法显式带上 `TOps`。调用方使用 `ValueOps<T, TOps>.Lerp(a, b, t, result)` 等入口，入口把同一个运算类型传给默认方法。`Subtract`、`Scale` 和 `Distance` 也有批量默认方法；用户可以实现对应的静态泛型方法来专门化批量运算。

## Invariants
- 当前内置运算为 `FloatOps`、`Vector2Ops`、`Vector3Ops`、`Vector4Ops`、`QuaternionOps`、`ColorOps`、`ThicknessOps`、`RectOps` 与 `Matrix3x2Ops`。用户类型实现同一个 `IValueOps<T>` 即可。
- `Color`、`Thickness`、`Rect` 是 `SomeEngine.Core.Math` 的通用 unmanaged 值；颜色按线性 RGBA，厚度按左上右下，矩形按位置与宽高逐分量运算。向量与这些值的距离为欧氏距离，矩阵的距离为六个分量的欧氏距离。四元数插值采用 `Quaternion.Lerp` 的归一化最短弧插值，加减与缩放采用四分量代数，距离为四分量欧氏距离。
- 属性动画使用逐值版本；姿势动画可使用批量版本并专门化批量运算。当前默认批量方法按逐值运算处理每个元素，数值向量运算复用 `System.Numerics`。
- 曲线是数据：`Curve` 记录 `Kind`（线性、缓动、弹簧、关键帧、函数）与参数；`Curve.Sample(progress, keyframes)` 接收归一化进度。缓动提供正弦、二至五次幂、指数、圆形、回退、弹性与反弹的 In/Out/InOut 形式。
- `Keyframe` 是实例缓冲元素，存放归一化时间、曲线值和从该点开始的区间缓动；时间按递增排列。`Spec.Keyframes(duration, start, count)` 选择传入缓冲的区间；`count = 0` 使用从 `start` 开始的剩余元素。时间位于区间两端之外时返回端点值。资产调用者也可直接传入对应的只读 span。
- `Curve.FromFunction(delegate*<float, float>)` 接入用户函数指针，采样前将进度限制到 `[0, 1]`。曲线数据由调用方所在的组件或缓冲持有。
- `Spec.Tween(duration, easing)`、`Spec.Spring(stiffness, damping, tolerance)` 与 `Spec.Keyframes(duration, start, count)` 创建参数值。自定义函数曲线可用 `new Spec(duration, curve)`。
- `Curve.Step<T, TOps>(delta, target, ref current, ref velocity)` 采样质量为 1 的弹簧。采用最多 `1/120` 秒的小步，以新速度推进位置；状态由调用方的值数据持有，重定目标时继续使用该速度。位移距离和速度到零值的距离都小于容差时，写入目标并归零速度，返回收敛结果。默认刚度 180、阻尼 20、容差 `1e-5`。
- 运算跨度、曲线参数与采样方式的使用错误由 `Core.DevelopmentCheck` 报告，其调用随 shipping 构建配置消失。

## Verification
`tests/SomeEngine.Animation.Tests/ValueOpsTests.cs` 经公开运算 API 验证九种内置类型、用户类型和所有默认批量方法。`CurveTests.cs` 验证参考缓动值、实例关键帧、用户函数指针，以及参考弹簧的两个 `1/120` 秒小步（刚度 180、阻尼 20，从 0 到 1：值 `0.035260417`，速度 `2.73125`）与最终收敛。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/animation/Pose-Animation|Pose Animation]]

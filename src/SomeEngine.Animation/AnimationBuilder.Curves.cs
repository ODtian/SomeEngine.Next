using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

internal static unsafe class GraphCurves<T> where T : unmanaged
{
    private static readonly delegate* managed<ref AnimationBuilder, World, Entity, ref byte, ref byte, Spec, byte, TrackEndpoints, void> Add = Select();

    internal static bool TryAdd(ref AnimationBuilder builder, World world, Entity target,
        Channel<T> channel, T value, Spec spec, byte layer, TrackEndpoints endpoints)
    {
        if (Add == null)
            return false;
        Add(ref builder, world, target, ref Unsafe.As<Channel<T>, byte>(ref channel), ref Unsafe.As<T, byte>(ref value), spec, layer, endpoints);
        return true;
    }

    private static delegate* managed<ref AnimationBuilder, World, Entity, ref byte, ref byte, Spec, byte, TrackEndpoints, void> Select()
    {
        if (typeof(T) == typeof(float)) return &AddCurve<float, FloatOps>;
        if (typeof(T) == typeof(Vector2)) return &AddCurve<Vector2, Vector2Ops>;
        if (typeof(T) == typeof(Vector3)) return &AddCurve<Vector3, Vector3Ops>;
        if (typeof(T) == typeof(Vector4)) return &AddCurve<Vector4, Vector4Ops>;
        if (typeof(T) == typeof(Quaternion)) return &AddCurve<Quaternion, QuaternionOps>;
        if (typeof(T) == typeof(Color)) return &AddCurve<Color, ColorOps>;
        if (typeof(T) == typeof(Thickness)) return &AddCurve<Thickness, ThicknessOps>;
        if (typeof(T) == typeof(Rect)) return &AddCurve<Rect, RectOps>;
        if (typeof(T) == typeof(Matrix3x2)) return &AddCurve<Matrix3x2, Matrix3x2Ops>;
        return null;
    }

    private static void AddCurve<TValue, TOps>(ref AnimationBuilder builder, World world, Entity target,
        ref byte channelBytes, ref byte valueBytes, Spec spec, byte layer, TrackEndpoints endpoints)
        where TValue : unmanaged
        where TOps : struct, IValueOps<TValue>
    {
        Channel<TValue> channel = Unsafe.As<byte, Channel<TValue>>(ref channelBytes);
        TValue value = Unsafe.As<byte, TValue>(ref valueBytes);
        switch (spec.Curve.Kind)
        {
            case CurveKind.Linear:
                AddMotion(ref builder, target, channel, value, Spec.Tween<TValue, TOps, Linear>(spec.Duration), layer, endpoints);
                break;
            case CurveKind.Easing:
                AddMotion(ref builder, target, channel, value, Spec.FromFunction<TValue, TOps>(spec.Duration, Easing.Resolve(spec.Curve.Easing)), layer, endpoints);
                break;
            case CurveKind.Function:
                AddMotion(ref builder, target, channel, value, Spec.FromFunction<TValue, TOps>(spec.Duration, spec.Curve.Function), layer, endpoints);
                break;
            case CurveKind.Spring:
                AddMotion(ref builder, target, channel, value,
                    Spec.Spring<TValue, TOps>(spec.Curve.Stiffness, spec.Curve.Damping, spec.Curve.Tolerance), layer, endpoints);
                break;
            case CurveKind.Keyframes:
                var keys = (Start: spec.Curve.KeyframeStart, Count: spec.Curve.KeyframeCount, Values: Array.Empty<Keyframe>());
                world.ExecuteBufferRead<Keyframe, (int Start, int Count, Keyframe[] Values)>(target, ref keys, static (buffer, ref keys) =>
                {
                    int count = keys.Count == 0 ? buffer.Count - keys.Start : keys.Count;
                    keys.Values = buffer.AsSpan().Slice(keys.Start, count).ToArray();
                });
                AddMotion(ref builder, target, channel, value, Spec.Keyframes<TValue, TOps>(spec.Duration, keys.Values), layer, endpoints);
                break;
        }
    }

    private static void AddMotion<TValue, TOps, TParameters, TState, TEvaluator>(ref AnimationBuilder builder,
        Entity target, Channel<TValue> channel, TValue value, Spec<TValue, TOps, TParameters, TState, TEvaluator> spec,
        byte layer, TrackEndpoints endpoints)
        where TValue : unmanaged
        where TOps : struct, IValueOps<TValue>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<TValue, TOps, TParameters, TState>
    {
        var parameters = new PropertyLeafParameters<TValue, TOps, TParameters, TState, TEvaluator>(channel, value, spec, endpoints);
        builder.Leaf<PropertyLeafParameters<TValue, TOps, TParameters, TState, TEvaluator>, PropertyLeaf<TValue, TOps, TParameters, TState, TEvaluator>>(
            target, in parameters, layer);
    }
}

using System.Runtime.CompilerServices;

namespace SomeEngine.Animation;

public readonly struct Linear : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t;
}

public readonly struct SineIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Cos(t * MathF.PI / 2);
}

public readonly struct SineOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : MathF.Sin(t * MathF.PI / 2);
}

public readonly struct SineInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (1 - MathF.Cos(MathF.PI * t)) / 2;
}

public readonly struct QuadIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t;
}

public readonly struct QuadOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - (1 - t) * (1 - t);
}

public readonly struct QuadInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? 2 * t * t : 1 - MathF.Pow(2 - 2 * t, 2) / 2);
}

public readonly struct CubicIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * t;
}

public readonly struct CubicOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Pow(1 - t, 3);
}

public readonly struct CubicInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(2 - 2 * t, 3) / 2);
}

public readonly struct QuartIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * t * t;
}

public readonly struct QuartOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Pow(1 - t, 4);
}

public readonly struct QuartInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? 8 * t * t * t * t : 1 - MathF.Pow(2 - 2 * t, 4) / 2);
}

public readonly struct QuintIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : MathF.Pow(t, 5);
}

public readonly struct QuintOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Pow(1 - t, 5);
}

public readonly struct QuintInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? 16 * MathF.Pow(t, 5) : 1 - MathF.Pow(2 - 2 * t, 5) / 2);
}

public readonly struct ExpoIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : MathF.Pow(2, 10 * t - 10);
}

public readonly struct ExpoOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Pow(2, -10 * t);
}

public readonly struct ExpoInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? MathF.Pow(2, 20 * t - 10) / 2 : (2 - MathF.Pow(2, 10 - 20 * t)) / 2);
}

public readonly struct CircIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - MathF.Sqrt(1 - t * t);
}

public readonly struct CircOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : MathF.Sqrt(1 - (t - 1) * (t - 1));
}

public readonly struct CircInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? (1 - MathF.Sqrt(1 - 4 * t * t)) / 2 : (MathF.Sqrt(1 - MathF.Pow(2 * t - 2, 2)) + 1) / 2);
}

public readonly struct BackIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 2.70158f * t * t * t - 1.70158f * t * t;
}

public readonly struct BackOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 + 2.70158f * MathF.Pow(t - 1, 3) + 1.70158f * MathF.Pow(t - 1, 2);
}

public readonly struct BackInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? MathF.Pow(2 * t, 2) * (3.5949095f * 2 * t - 2.5949095f) / 2 : (MathF.Pow(2 * t - 2, 2) * (3.5949095f * (2 * t - 2) + 2.5949095f) + 2) / 2);
}

public readonly struct ElasticIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : -MathF.Pow(2, 10 * t - 10) * MathF.Sin((10 * t - 10.75f) * (2 * MathF.PI / 3));
}

public readonly struct ElasticOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : MathF.Pow(2, -10 * t) * MathF.Sin((10 * t - 0.75f) * (2 * MathF.PI / 3)) + 1;
}

public readonly struct ElasticInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? -MathF.Pow(2, 20 * t - 10) * MathF.Sin((20 * t - 11.125f) * (2 * MathF.PI / 4.5f)) / 2 : MathF.Pow(2, 10 - 20 * t) * MathF.Sin((20 * t - 11.125f) * (2 * MathF.PI / 4.5f)) / 2 + 1);
}

public readonly struct BounceIn : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : 1 - BounceOut.Sample(1 - t);
}

public readonly struct BounceInOut : IEasing
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(float t) => t <= 0 ? 0 : t >= 1 ? 1 : (t < 0.5f ? (1 - BounceOut.Sample(1 - 2 * t)) / 2 : (1 + BounceOut.Sample(2 * t - 1)) / 2);
}

public readonly struct BounceOut : IEasing
{
    public static float Sample(float t)
    {
        const float factor = 7.5625f;
        const float width = 2.75f;
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        if (t < 1 / width) return factor * t * t;
        if (t < 2 / width) return factor * (t -= 1.5f / width) * t + 0.75f;
        if (t < 2.5f / width) return factor * (t -= 2.25f / width) * t + 0.9375f;
        return factor * (t -= 2.625f / width) * t + 0.984375f;
    }
}


using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

public interface IValueOps<T> where T : unmanaged
{
    static abstract T Lerp(in T a, in T b, float t);
    static abstract T Add(in T a, in T b);
    static abstract T Subtract(in T a, in T b);
    static abstract T Scale(in T a, float scale);
    static abstract float Distance(in T a, in T b);

    static virtual bool Equals(in T a, in T b) => EqualityComparer<T>.Default.Equals(a, b);

    static virtual void Equals<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<bool> result)
        where TOps : struct, IValueOps<T>
    {
        for (int i = 0; i < a.Length; i++)
            result[i] = TOps.Equals(in a[i], in b[i]);
    }

    static virtual void Lerp<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, ReadOnlySpan<float> progress, Span<T> result)
        where TOps : struct, IValueOps<T>
    {
        for (int i = 0; i < a.Length; i++)
            result[i] = TOps.Lerp(in a[i], in b[i], progress[i]);
    }

    static virtual void Lerp<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float t, Span<T> result)
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(a.Length == b.Length && result.Length >= a.Length, "Value operation spans must have matching input lengths and sufficient output space.");
        for (int i = 0; i < a.Length; i++)
            result[i] = TOps.Lerp(in a[i], in b[i], t);
    }

    static virtual void Add<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float weight, Span<T> result)
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(a.Length == b.Length && result.Length >= a.Length, "Value operation spans must have matching input lengths and sufficient output space.");
        for (int i = 0; i < a.Length; i++)
        {
            T scaled = TOps.Scale(in b[i], weight);
            result[i] = TOps.Add(in a[i], in scaled);
        }
    }

    static virtual void Subtract<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> result)
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(a.Length == b.Length && result.Length >= a.Length, "Value operation spans must have matching input lengths and sufficient output space.");
        for (int i = 0; i < a.Length; i++)
            result[i] = TOps.Subtract(in a[i], in b[i]);
    }

    static virtual void Scale<TOps>(ReadOnlySpan<T> values, float scale, Span<T> result)
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(result.Length >= values.Length, "Value operation output must have sufficient space.");
        for (int i = 0; i < values.Length; i++)
            result[i] = TOps.Scale(in values[i], scale);
    }

    static virtual void Distance<TOps>(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<float> result)
        where TOps : struct, IValueOps<T>
    {
        DevelopmentCheck.Require(a.Length == b.Length && result.Length >= a.Length, "Value operation spans must have matching input lengths and sufficient output space.");
        for (int i = 0; i < a.Length; i++)
            result[i] = TOps.Distance(in a[i], in b[i]);
    }
}

/// <summary>Dispatches batch operations through the selected struct and its interface defaults.</summary>
public static class ValueOps<T, TOps>
    where T : unmanaged
    where TOps : struct, IValueOps<T>
{
    public static bool Equals(in T a, in T b) => TOps.Equals(in a, in b);

    public static void Equals(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<bool> result) =>
        TOps.Equals<TOps>(a, b, result);

    public static void Lerp(ReadOnlySpan<T> a, ReadOnlySpan<T> b, ReadOnlySpan<float> progress, Span<T> result) =>
        TOps.Lerp<TOps>(a, b, progress, result);

    public static void Lerp(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float t, Span<T> result) =>
        TOps.Lerp<TOps>(a, b, t, result);

    public static void Add(ReadOnlySpan<T> a, ReadOnlySpan<T> b, float weight, Span<T> result) =>
        TOps.Add<TOps>(a, b, weight, result);

    public static void Subtract(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<T> result) =>
        TOps.Subtract<TOps>(a, b, result);

    public static void Scale(ReadOnlySpan<T> values, float scale, Span<T> result) =>
        TOps.Scale<TOps>(values, scale, result);

    public static void Distance(ReadOnlySpan<T> a, ReadOnlySpan<T> b, Span<float> result) =>
        TOps.Distance<TOps>(a, b, result);
}

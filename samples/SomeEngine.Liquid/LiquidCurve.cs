using System.Numerics;

namespace SomeEngine.Liquid;

public readonly record struct LiquidInlet(int Point, Vector3 Nozzle, Vector3 Front);

/// <summary>One free liquid volume between neighbouring cell faces. Squared radii are in m².</summary>
public struct LiquidCurve
{
    public Vector3 Start, End, FirstTangent, LastTangent, SquaredRadius;
    public float Length, Neck;

    public readonly Vector3 Position(float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return Start + (-2 * t3 + 3 * t2) * (End - Start) + (t3 - 2 * t2 + t) * FirstTangent +
            (t3 - t2) * LastTangent;
    }

    public readonly Vector3 Derivative(float t) => (-6 * t * t + 6 * t) * (End - Start) + (3 * t * t - 4 * t + 1) * FirstTangent +
        (3 * t * t - 2 * t) * LastTangent;

    public readonly float RadiusSquared(float t)
    {
        float blend = t * t * (3 - 2 * t), wave = MathF.Sin(MathF.PI * t), narrowing = 1 - Neck * Neck;
        float middle = 1 - narrowing * wave * wave;
        float shoulders = 1 - narrowing + 7 * narrowing * (2 * t - 1) * (2 * t - 1);
        float bubble = 30 * t * t * (1 - t) * (1 - t) * shoulders;
        return float.Lerp(SquaredRadius.X, SquaredRadius.Y, blend) * middle + SquaredRadius.Z * bubble;
    }
}

/// <summary>Reconstructs each current liquid volume; resolution is independent of scene area.</summary>
public static class LiquidGeometry
{
    public static void BuildCurves(ReadOnlySpan<LiquidPoint> points, LiquidInlet inlet, Span<LiquidCurve> curves)
    {
        Span<int> next = stackalloc int[LiquidFlow.Capacity]; next.Fill(-1);
        for (int index = 0; index < points.Length; index++)
            if (points[index].Previous >= 0) next[points[index].Previous] = index;
        for (int index = 0; index < points.Length; index++)
        {
            ref readonly LiquidPoint point = ref points[index];
            curves[index] = default;
            if (point.Surface != LiquidContactSurface.None) continue;
            int previous = point.Previous, following = next[index];
            bool supplied = inlet.Point == index;
            if (previous < 0 && following < 0 && !supplied) continue;
            Vector3 older = previous >= 0 ? points[previous].Position :
                following >= 0 ? 2 * point.Position - points[following].Position : 2 * point.Position - inlet.Nozzle;
            Vector3 newer = following >= 0 ? points[following].Position : supplied ? inlet.Nozzle : 2 * point.Position - older;
            Vector3 start = supplied ? inlet.Front : (older + point.Position) * .5f;
            Vector3 end = supplied ? inlet.Nozzle : following == inlet.Point && following >= 0 ? inlet.Front : (newer + point.Position) * .5f;
            float span = Vector3.Distance(start, end);
            Vector3 firstVelocity = previous >= 0 ? (point.Velocity + points[previous].Velocity) * .5f : point.Velocity;
            Vector3 lastVelocity = following >= 0 ? (point.Velocity + points[following].Velocity) * .5f : point.Velocity;
            Vector3 firstTangent = firstVelocity.LengthSquared() > 0 ? -Vector3.Normalize(firstVelocity) * span : end - start;
            Vector3 lastTangent = lastVelocity.LengthSquared() > 0 ? -Vector3.Normalize(lastVelocity) * span : end - start;
            curves[index] = new() { Start = start, End = end, FirstTangent = firstTangent, LastTangent = lastTangent, Neck = point.Neck };
            float length = 0;
            for (int sample = 0; sample <= 16; sample++)
                length += curves[index].Derivative(sample / 16f).Length() * (sample is 0 or 16 ? 1 : sample % 2 == 0 ? 2 : 4) / 48;
            curves[index].Length = length;
        }
        for (int index = 0; index < points.Length; index++)
        {
            ref LiquidCurve curve = ref curves[index];
            if (curve.Length == 0) continue;
            ref readonly LiquidPoint point = ref points[index];
            float radius2 = (float)(point.Volume / (Math.PI * curve.Length)), start = 0, end = 0;
            if (point.Previous >= 0 && curves[point.Previous].Length > 0)
                start = Math.Min(radius2, (float)(points[point.Previous].Volume / (Math.PI * curves[point.Previous].Length)));
            int following = next[index];
            if (following >= 0 && curves[following].Length > 0)
                end = Math.Min(radius2, (float)(points[following].Volume / (Math.PI * curves[following].Length)));
            else if (inlet.Point == index) end = radius2;
            double baseIntegral = 0, bubbleIntegral = 0;
            for (int sample = 0; sample <= 16; sample++)
            {
                float t = sample / 16f, blend = t * t * (3 - 2 * t), wave = MathF.Sin(MathF.PI * t), narrowing = 1 - curve.Neck * curve.Neck;
                float middle = 1 - narrowing * wave * wave;
                float shoulders = 1 - narrowing + 7 * narrowing * (2 * t - 1) * (2 * t - 1);
                float bubble = 30 * t * t * (1 - t) * (1 - t) * shoulders;
                float weight = curve.Derivative(t).Length() * (sample is 0 or 16 ? 1 : sample % 2 == 0 ? 2 : 4) / 48;
                baseIntegral += float.Lerp(start, end, blend) * middle * weight; bubbleIntegral += bubble * weight;
            }
            curve.SquaredRadius = new(start, end, (float)((point.Volume / Math.PI - baseIntegral) / bubbleIntegral));
        }
    }
}

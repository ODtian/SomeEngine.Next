using System.Numerics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;

namespace SomeEngine.Runtime;

internal readonly record struct RuntimeViewFrame(RenderView View, Vector2 JitterPixels)
{
    internal static RuntimeViewFrame Create(
        Matrix4x4 view,
        Matrix4x4 projection,
        int width,
        int height,
        uint temporalFrameIndex,
        bool temporalResolveEnabled = true,
        bool cameraCut = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Vector2 jitter = temporalResolveEnabled
            ? TemporalJitter.SamplePixels(temporalFrameIndex)
            : Vector2.Zero;
        Matrix4x4 outputProjection = temporalResolveEnabled
            ? TemporalJitter.ApplyToProjection(
                projection,
                jitter,
                checked((uint)width),
                checked((uint)height))
            : projection;
        return new RuntimeViewFrame(
            new RenderView(
                view,
                outputProjection,
                checked((uint)width),
                checked((uint)height),
                cameraCut),
            jitter);
    }
}

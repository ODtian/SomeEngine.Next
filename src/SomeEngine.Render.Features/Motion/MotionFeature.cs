using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Motion;

/// <summary>
/// Motion is emitted by the material shade invocation that already owns vertex and domain
/// evaluation. This value only exposes that output to temporal consumers; it owns no pass,
/// shader, allocation, or duplicate geometry evaluation. Both forward and deferred shading
/// supply the same R16G16Float texture: current projected UV minus previous projected UV.
/// Pixels without valid previous geometry history contain zero motion; temporal consumers
/// apply the current/previous camera jitter difference separately.
/// </summary>
public readonly record struct MotionFeatureOutput(
    GraphTextureId MotionVectors,
    int Width,
    int Height,
    uint ViewCount)
{
    public bool IsValid =>
        MotionVectors != default && Width > 0 && Height > 0 && ViewCount != 0;
}

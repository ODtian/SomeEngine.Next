using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Motion;

/// <summary>
/// Motion is emitted by the material shade invocation that already owns vertex and domain
/// evaluation. This value only exposes that output to temporal consumers; it owns no pass,
/// shader, allocation, or duplicate geometry evaluation.
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

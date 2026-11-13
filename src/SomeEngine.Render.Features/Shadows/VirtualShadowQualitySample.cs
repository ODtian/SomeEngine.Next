namespace SomeEngine.Render.Features.Shadows;

public enum VirtualShadowQualityKind : byte
{
    ResolutionBias,
    WorkTime,
}

public readonly record struct VirtualShadowQualitySample(VirtualShadowQualityKind Kind, float Value);

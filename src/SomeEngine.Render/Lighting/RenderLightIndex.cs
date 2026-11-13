namespace SomeEngine.Render.Lighting;

public enum RenderLightKind : uint
{
    Point,
    Spot,
}

/// <summary>Identifies a light in one of the typed collections in a render-light set.</summary>
public readonly record struct RenderLightIndex(RenderLightKind Kind, int Index);

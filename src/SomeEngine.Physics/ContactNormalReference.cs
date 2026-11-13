namespace SomeEngine.Physics;

/// <summary>The body-local point whose displacement defines the normal separation.</summary>
public enum ContactNormalReference : uint
{
    MaterialPoint,
    BodyOrigin,
}

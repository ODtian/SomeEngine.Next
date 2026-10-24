using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Completed world-space scene queries and current support data, also usable by terrain adapters.</summary>
public interface ICollisionQueries
{
    void Raycast(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found);
    bool TryGetSurface(Entity entity, out CollisionSurface surface);
}

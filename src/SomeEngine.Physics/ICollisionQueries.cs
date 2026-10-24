using System.Numerics;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Physics;

/// <summary>Completed world-space scene queries and current support data, also usable by terrain adapters.</summary>
public interface ICollisionQueries
{
    void Raycast(ReadOnlySpan<CollisionRay> rays, Span<CollisionHit> hits, Span<bool> found);
    void SweepSphere(ReadOnlySpan<CollisionSphereSweep> sweeps, Span<CollisionHit> hits, Span<bool> found);
    bool TryGetTransform(Entity entity, out Matrix4x4 transform, out ulong surfaceVersion);
}

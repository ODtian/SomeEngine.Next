using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Components;

/// <summary>
/// Links one render-world entity to the authoritative entity in the main world. The index is
/// owned by the render world; extraction never writes a reverse link into the main world.
/// </summary>
public readonly struct RenderSource : IIndexedComponent<Entity>
{
    public RenderSource(Entity entity)
    {
        Entity = entity;
    }

    public Entity Entity { get; }

    public Entity GetKey() => Entity;
}

/// <summary>
/// World-space transform snapshot owned by the render world. Its memory layout is the exact
/// 48-byte device ABI, so a query chunk can be copied directly into a final mapped instance
/// column without a second CPU representation or conversion buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = SizeInBytes)]
public readonly struct RenderTransform : IComponent, IEquatable<RenderTransform>
{
    public const int SizeInBytes = 48;

    public RenderTransform(in TransformQvvs value)
    {
        Rotation = value.Rotation;
        Position = value.Position;
        Scale = value.Scale;
        Stretch = value.Stretch;
        Padding = 0.0f;
    }

    public RenderTransform(
        Quaternion rotation,
        Vector3 position,
        float scale,
        Vector3 stretch)
    {
        Rotation = rotation;
        Position = position;
        Scale = scale;
        Stretch = stretch;
        Padding = 0.0f;
    }

    public readonly Quaternion Rotation;

    public readonly Vector3 Position;

    public readonly float Scale;

    public readonly Vector3 Stretch;

    private readonly float Padding;

    public bool Equals(RenderTransform other) =>
        Rotation == other.Rotation
        && Position == other.Position
        && Scale == other.Scale
        && Stretch == other.Stretch;

    public override bool Equals(object? obj) =>
        obj is RenderTransform other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Rotation, Position, Scale, Stretch);

    public static bool operator ==(RenderTransform left, RenderTransform right) =>
        left.Equals(right);

    public static bool operator !=(RenderTransform left, RenderTransform right) =>
        !left.Equals(right);
}

/// <summary>
/// Previous RenderWorld transform with the same exact device ABI. This is temporal scene state,
/// not a second instance identity or an upload cache.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = RenderTransform.SizeInBytes)]
public readonly record struct RenderPreviousTransform(RenderTransform Value) : IComponent;

/// <summary>
/// Marks a render-world entity as eligible for pipeline-specific instance batches. Extraction
/// modules add or remove this semantic facet; it creates no stable slot or persistent GPU-row
/// identity, and batch-local rows are never written back to either world.
/// </summary>
public readonly record struct RenderInstance : IComponent;

/// <summary>
/// Pipeline-independent mesh semantics owned by the render world. Pipeline algorithms may add
/// their own components and contribute fixed fields to the shared render-instance property store
/// without creating a second instance store.
/// </summary>
public readonly record struct RenderMesh(Mesh Mesh, float BoundsExpansion) : IComponent;

/// <summary>
/// Pipeline-neutral view input stored as an ordinary RenderWorld component. A view entity may
/// carry additional pipeline-owned components, while queue/bin state remains transient output of
/// the consuming frame system. Camera position is derived from <see cref="View"/> so the ECS does
/// not store a second copy of the same transform fact.
/// </summary>
public readonly record struct RenderView(
    Matrix4x4 View,
    Matrix4x4 Projection,
    uint ViewportWidth,
    uint ViewportHeight,
    bool CameraCut = false) : IComponent;

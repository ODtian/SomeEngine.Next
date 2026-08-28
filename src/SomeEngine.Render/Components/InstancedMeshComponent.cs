using SomeEngine.ECS;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Components;

/// <summary>
/// Main-world reference to one user-owned instanced-mesh resource. The component carries only
/// stable logical identity; mesh, materials, instance properties, and physical GPU storage remain
/// owned by the resource and render systems.
/// </summary>
public readonly struct InstancedMesh : IComponent, IEquatable<InstancedMesh>
{
    public InstancedMesh(RenderMeshInstanceHandle resource)
    {
        if (!resource.IsValid)
            throw new ArgumentException("An instanced-mesh component requires a live resource handle.", nameof(resource));
        Resource = resource;
    }

    public RenderMeshInstanceHandle Resource { get; }

    public bool Equals(InstancedMesh other) => Resource == other.Resource;

    public override bool Equals(object? obj) => obj is InstancedMesh other && Equals(other);

    public override int GetHashCode() => Resource.GetHashCode();

    public static bool operator ==(InstancedMesh left, InstancedMesh right) => left.Equals(right);

    public static bool operator !=(InstancedMesh left, InstancedMesh right) => !left.Equals(right);
}

/// <summary>
/// RenderWorld snapshot of an <see cref="InstancedMesh"/> reference. It contains no copied instance
/// values and no renderer-specific binding; prepare systems resolve the handle against their
/// explicitly owned collection snapshot.
/// </summary>
public readonly struct RenderInstancedMesh : IComponent, IEquatable<RenderInstancedMesh>
{
    public RenderInstancedMesh(RenderMeshInstanceHandle resource)
    {
        if (!resource.IsValid)
            throw new ArgumentException("A render instanced-mesh reference requires a live resource handle.", nameof(resource));
        Resource = resource;
    }

    public RenderMeshInstanceHandle Resource { get; }

    public bool Equals(RenderInstancedMesh other) => Resource == other.Resource;

    public override bool Equals(object? obj) =>
        obj is RenderInstancedMesh other && Equals(other);

    public override int GetHashCode() => Resource.GetHashCode();

    public static bool operator ==(
        RenderInstancedMesh left,
        RenderInstancedMesh right) => left.Equals(right);

    public static bool operator !=(
        RenderInstancedMesh left,
        RenderInstancedMesh right) => !left.Equals(right);
}

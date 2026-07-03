using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Creates a new entity in this world and shallow-copies the source entity's standard logical storage surface.
    /// Cleanup components, outgoing relations, incoming relations, child/subtree state, and Entity field remapping are excluded.
    /// </summary>
    public Entity CloneEntity(Entity source)
    {
        return Copy.Clone(source);
    }

    /// <summary>
    /// Creates a new entity in this world and shallow-copies the selected source entity storage surface.
    /// Passing <see cref="EntityCopyOptions.Default"/> uses <see cref="EntityCopyOptions.Standard"/>.
    /// </summary>
    public Entity CloneEntity(Entity source, EntityCopyOptions options)
    {
        return Copy.Clone(source, options);
    }

    /// <summary>
    /// Replaces the target entity's standard logical storage surface with a shallow copy of the source entity's surface.
    /// The target entity identity is preserved.
    /// </summary>
    public void CopyEntity(Entity source, Entity target)
    {
        Copy.CopyInto(source, target);
    }

    /// <summary>
    /// Replaces the target entity's selected logical storage surface with a shallow copy of the source entity's surface.
    /// The target entity identity is preserved. Incoming relation edges are never copied.
    /// </summary>
    public void CopyEntity(Entity source, Entity target, EntityCopyOptions options)
    {
        Copy.CopyInto(source, target, options);
    }
}


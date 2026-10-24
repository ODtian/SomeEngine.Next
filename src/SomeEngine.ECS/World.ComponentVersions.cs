using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Reads this World's write counter for a table or sparse component type. Each addition,
    /// value replacement, writable borrow, or removal advances the counter, including writes
    /// in the same system tick.
    /// Compare versions for equality to detect changes; the counter wraps as a uint.
    /// </summary>
    public uint ComponentWriteVersion<T>() where T : struct
    {
        ThrowIfUnavailable();
        WorldStructureRoot structure = CurrentStructureRoot;
        if (ComponentMetadata<T>.Storage == StoragePath.Sparse)
        {
            return structure.Sparse.TrySet<T>(out SparseSet<T> sparseSet)
                ? sparseSet.ComponentWriteVersion
                : 0;
        }
        return structure.ComponentWriteVersions.Read(ComponentMetadata<T>.Id);
    }

    internal uint ComponentWriteVersion(ushort component)
    {
        ThrowIfUnavailable();
        WorldStructureRoot structure = CurrentStructureRoot;
        return ComponentRegistry.Get(component).Storage == StoragePath.Sparse
            ? structure.Sparse.ComponentWriteVersion(component)
            : structure.ComponentWriteVersions.Read(component);
    }
}

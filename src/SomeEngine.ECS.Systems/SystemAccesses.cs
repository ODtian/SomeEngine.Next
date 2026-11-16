using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Systems;

public sealed class SystemAccesses
{
    internal readonly List<WorldValueAccess> Values = [];
    internal bool HasRandomWrites;
    internal bool HasTrackedWrites;

    internal bool RequiresSerial<T>() where T : struct
    {
        if (HasRandomWrites) return true;
        foreach (WorldValueAccess value in Values)
            if (value.Kind == WorldValueKind.Table && value.Type == ComponentType<T>.TypeId && !value.Write) return true;
        return false;
    }

    /// <summary>Declares reads of the component on any entity; the component type decides its storage.</summary>
    public SystemAccesses Read<T>() where T : struct => Add<T>(false);
    /// <summary>Declares writes of the component on any entity; the component type decides its storage.</summary>
    public SystemAccesses Write<T>() where T : struct => Add<T>(true);

    public SystemAccesses ReadResource<T>()
    {
        Values.Add(new(ResourceType<T>.Id, WorldValueKind.Resource, false, false));
        return this;
    }

    public SystemAccesses WriteResource<T>()
    {
        Values.Add(new(ResourceType<T>.Id, WorldValueKind.Resource, true, false));
        HasRandomWrites = true;
        return this;
    }

    private SystemAccesses Add<T>(bool write) where T : struct
    {
        WorldValueKind kind = ComponentType<T>.IsSparseType ? WorldValueKind.Sparse : WorldValueKind.Table;
        bool structural = kind == WorldValueKind.Table && ComponentType<T>.Value.UsesStructureWrites;
        if (structural && write) throw new InvalidOperationException("Record structural writes in the command producer.");
        Values.Add(new(ComponentType<T>.TypeId, kind, write, ComponentType<T>.TracksWritesType, structural));
        HasRandomWrites |= write;
        HasTrackedWrites |= write && ComponentType<T>.TracksWritesType;
        return this;
    }
}

using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Systems;

public sealed class SystemAccesses
{
    internal readonly List<WorldValueAccess> Values = [];
    internal bool HasRandomWrites;
    internal bool HasTrackedWrites;
    internal int Revision;

    internal bool RequiresSerial<T>() where T : struct
    {
        if (HasRandomWrites) return true;
        foreach (WorldValueAccess value in Values)
            if (value.Kind == WorldValueKind.Table && value.Type == ComponentType<T>.TypeId && !value.Write) return true;
        return false;
    }

    public SystemAccesses ReadRandom<T>() where T : struct => Add<T>(WorldValueKind.Table, false);
    public SystemAccesses WriteRandom<T>() where T : struct => Add<T>(WorldValueKind.Table, true);
    public SystemAccesses ReadSparse<T>() where T : struct => Add<T>(WorldValueKind.Sparse, false);
    public SystemAccesses WriteSparse<T>() where T : struct => Add<T>(WorldValueKind.Sparse, true);

    public SystemAccesses ReadResource<T>()
    {
        Values.Add(new(ResourceType<T>.Id, WorldValueKind.Resource, false, false));
        Revision++;
        return this;
    }

    public SystemAccesses WriteResource<T>()
    {
        Values.Add(new(ResourceType<T>.Id, WorldValueKind.Resource, true, false));
        Revision++;
        HasRandomWrites = true;
        return this;
    }

    private SystemAccesses Add<T>(WorldValueKind kind, bool write) where T : struct
    {
        bool structural = kind == WorldValueKind.Table && ComponentType<T>.Value.UsesStructureWrites;
        if (structural && write) throw new InvalidOperationException("Record structural writes in the command producer.");
        Values.Add(new(ComponentType<T>.TypeId, kind, write, ComponentType<T>.TracksWritesType, structural));
        Revision++;
        HasRandomWrites |= write;
        HasTrackedWrites |= write && ComponentType<T>.TracksWritesType;
        return this;
    }
}

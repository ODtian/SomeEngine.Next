using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Components;

internal interface IBufferCopier
{
    int HeaderComponentId { get; }

    int InlineComponentId { get; }

    void AddCopy(SomeEngine.ECS.Owners.Buffers buffers, Entity source, Entity target);

    void ReplaceCopy(SomeEngine.ECS.Owners.Buffers buffers, Entity source, Entity target);
}

internal static class BufferRegistry
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<int, IBufferCopier> s_byHeader = new();
    private static readonly HashSet<int> s_bufferComponentIds = new();

    public static void Register<T>()
        where T : struct, IBufferElement
    {
        int headerId = ComponentMetadata<DynamicBufferHeader<T>>.Id;
        int inlineId = ComponentMetadata<DynamicBufferInline<T>>.Id;

        lock (s_gate)
        {
            if (!s_byHeader.ContainsKey(headerId))
                s_byHeader.Add(headerId, new BufferCopier<T>(headerId, inlineId));

            s_bufferComponentIds.Add(headerId);
            s_bufferComponentIds.Add(inlineId);
        }
    }

    public static bool TryHeader(int headerComponentId, out IBufferCopier operations)
    {
        lock (s_gate)
            return s_byHeader.TryGetValue(headerComponentId, out operations!);
    }

    public static bool IsBufferId(int componentId)
    {
        lock (s_gate)
            return s_bufferComponentIds.Contains(componentId);
    }
}

internal sealed class BufferCopier<T> : IBufferCopier
    where T : struct, IBufferElement
{
    public BufferCopier(int headerComponentId, int inlineComponentId)
    {
        HeaderComponentId = headerComponentId;
        InlineComponentId = inlineComponentId;
    }

    public int HeaderComponentId { get; }

    public int InlineComponentId { get; }

    public void AddCopy(SomeEngine.ECS.Owners.Buffers buffers, Entity source, Entity target)
    {
        buffers.CopyStorage<T>(
            source,
            target,
            SerializationChangeKind.BufferAdded);
    }

    public void ReplaceCopy(SomeEngine.ECS.Owners.Buffers buffers, Entity source, Entity target)
    {
        buffers.CopyStorage<T>(
            source,
            target,
            SerializationChangeKind.BufferChanged);
    }
}


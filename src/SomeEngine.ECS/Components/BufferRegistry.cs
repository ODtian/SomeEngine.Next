using SomeEngine.ECS.Entities;
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
    private static RegistryState s_state = RegistryState.Empty;

    public static void Register<T>()
        where T : struct, IBufferElement
    {
        int headerId = ComponentMetadata<DynamicBufferHeader<T>>.Id;
        int inlineId = ComponentMetadata<DynamicBufferInline<T>>.Id;

        lock (s_gate)
        {
            RegistryState current = Volatile.Read(ref s_state);
            IBufferCopier?[] currentCopiers = current.ByHeader;
            if ((uint)headerId < (uint)currentCopiers.Length && currentCopiers[headerId] is not null)
                return;

            int requiredLength = Math.Max(headerId, inlineId) + 1;
            var copiers = new IBufferCopier?[Math.Max(requiredLength, currentCopiers.Length)];
            currentCopiers.CopyTo(copiers, 0);
            copiers[headerId] = new BufferCopier<T>(headerId, inlineId);

            int[] currentHeaders = current.HeaderByStorageComponent;
            var headers = new int[Math.Max(requiredLength, currentHeaders.Length)];
            Array.Fill(headers, -1);
            currentHeaders.CopyTo(headers, 0);
            headers[headerId] = headerId;
            headers[inlineId] = headerId;

            Volatile.Write(ref s_state, new RegistryState(copiers, headers));
        }
    }

    public static bool TryHeader(int headerComponentId, out IBufferCopier operations)
    {
        IBufferCopier?[] copiers = Volatile.Read(ref s_state).ByHeader;
        if ((uint)headerComponentId < (uint)copiers.Length &&
            copiers[headerComponentId] is IBufferCopier found)
        {
            operations = found;
            return true;
        }

        operations = null!;
        return false;
    }

    public static bool IsGraphId(int componentId)
    {
        int[] headers = Volatile.Read(ref s_state).HeaderByStorageComponent;
        return (uint)componentId < (uint)headers.Length && headers[componentId] >= 0;
    }

    public static bool TryGetHeaderComponentId(int componentId, out int headerComponentId)
    {
        int[] headers = Volatile.Read(ref s_state).HeaderByStorageComponent;
        if ((uint)componentId < (uint)headers.Length && headers[componentId] >= 0)
        {
            headerComponentId = headers[componentId];
            return true;
        }

        headerComponentId = -1;
        return false;
    }

    private sealed record RegistryState(
        IBufferCopier?[] ByHeader,
        int[] HeaderByStorageComponent)
    {
        internal static RegistryState Empty { get; } = new(
            Array.Empty<IBufferCopier?>(),
            Array.Empty<int>());
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
            added: true);
    }

    public void ReplaceCopy(SomeEngine.ECS.Owners.Buffers buffers, Entity source, Entity target)
    {
        buffers.CopyStorage<T>(
            source,
            target,
            added: false);
    }

}


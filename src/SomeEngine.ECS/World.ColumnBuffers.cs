using SomeEngine.ECS.Storage;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private PodColumnBuffer?[] _podBuffers = [];

    internal PodColumnBuffer PodBuffer(ComponentType type, int initialRows = 0)
    {
        int id = type.Id;
        if (id >= _podBuffers.Length) Array.Resize(ref _podBuffers, Math.Max(id + 1, Math.Max(16, _podBuffers.Length * 2)));
        return _podBuffers[id] ??= new PodColumnBuffer(id, type.Size, initialRows);
    }

    internal PodColumnBuffer ObservePodValues<T>() where T : struct
    {
        PodColumnBuffer buffer = PodBuffer(ComponentType<T>.Value);
        if (!buffer.IsObserved)
        {
            for (int i = 0; i < ArchetypeCount; i++)
            {
                Archetype archetype = Archetypes[i];
                if (archetype.Types.Bits.Has(buffer.TypeId)) archetype.AddCachedComponent(buffer);
            }
            _cachedTypes.Add(buffer);
            buffer.IsObserved = true;
        }
        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryPodValues<T>(out Span<T> values) where T : struct
    {
        int type = ComponentType<T>.TypeId;
        PodColumnBuffer?[] buffers = _podBuffers;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            && (uint)type < (uint)buffers.Length && buffers[type] is { } buffer)
            return buffer.TryAll(_count, out values);
        values = default;
        return false;
    }

}

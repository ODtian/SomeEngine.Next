using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

internal static class NavigationJobAccess
{
    internal static JobResourceAccess Read<T>(Scheduler scheduler, ReadOnlyMemory<T> values)
    {
        MemoryMarshal.TryGetArray(values, out ArraySegment<T> segment);
        JobResourceToken token = scheduler.GetResourceToken(segment.Array ?? Array.Empty<T>());
        return segment.Count == 0 ? JobResourceAccess.Read(token) : JobResourceAccess.Read(token, segment.Offset, segment.Count);
    }

    internal static JobResourceAccess Write<T>(Scheduler scheduler, T[] values) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(values));
}

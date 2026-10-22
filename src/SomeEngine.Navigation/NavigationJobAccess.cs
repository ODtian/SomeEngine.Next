using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

internal static class NavigationJobAccess
{
    internal static JobResourceAccess Read<T>(Scheduler scheduler, ReadOnlyMemory<T> values)
    {
        MemoryMarshal.TryGetArray(values, out ArraySegment<T> segment);
        return JobResourceAccess.Read(scheduler.GetResourceToken(segment.Array ?? Array.Empty<T>()), segment.Offset, segment.Count);
    }

    internal static JobResourceAccess Write<T>(Scheduler scheduler, T[] values) =>
        JobResourceAccess.Write(scheduler.GetResourceToken(values));
}

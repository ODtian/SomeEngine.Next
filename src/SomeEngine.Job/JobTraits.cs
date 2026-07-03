using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal static class JobTraits
{
    internal static JobPayloadLane GetPayloadLane<T>()
        where T : struct
    {
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? JobPayloadLane.RefContaining
            : JobPayloadLane.RefFree;
    }
}


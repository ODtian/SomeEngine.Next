using SomeEngine.ECS.Collections;

namespace SomeEngine.ECS;

/// <summary>Root-local bundle lookup data and the currently executing bundle write.</summary>
internal sealed class BundleData
{
    internal readonly Dictionary<SortedValueKey, BundleSpawnMap> Maps = new(SortedValueComparer.Instance);
    internal BundleSpawnMap? LastMap;
    internal long NextWrite;
    internal long ActiveWrite;
    internal int WritingThread;
    internal BundleWriteRuntime? WriteData;
}

using SomeEngine.ECS.Components;

namespace SomeEngine.ECS;

public partial class World
{
    public BundleBatch SpawnBatch<T>(int count)
        where T : struct, IComponent
    {
        return Bundles.SpawnBatch<T>(count);
    }

    public BundleBatch SpawnBatch(ReadOnlySpan<int> componentIds, int count)
    {
        return Bundles.SpawnBatch(componentIds, count);
    }
}


using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Core.ECS;

public sealed class BakeContext
{
    public BakeContext(World authoringWorld, World runtimeWorld)
    {
        ArgumentNullException.ThrowIfNull(authoringWorld);
        ArgumentNullException.ThrowIfNull(runtimeWorld);

        AuthoringWorld = authoringWorld;
        RuntimeWorld = runtimeWorld;
    }

    public World AuthoringWorld { get; }

    public World RuntimeWorld { get; }

    public EntityId CreateRuntimeEntity()
    {
        return RuntimeWorld.CreateEntity();
    }
}


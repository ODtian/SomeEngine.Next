using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SystemsConsumerTests
{
    [Fact]
    public void SystemGroupCanRunAgainstWorldAndJobContext()
    {
        var world = new World();
        var context = new SystemContext();
        using var group = new SystemGroup<SystemContext>(new EngineDriver(world, context));

        group.Update();

        context.GlobalDependency.Complete();
    }
}

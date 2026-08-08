using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SystemsConsumerTests
{
    [Fact]
    public void SystemGroupCanRunAgainstImmediateWorldContext()
    {
        var world = new World();
        using var group = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        group.Add<NoOpSystem>();

        group.Update();

        Assert.Equal(2u, world.CurrentTick);
    }

    private readonly struct NoOpSystem : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context)
        {
            Assert.NotNull(context.World);
        }
    }
}

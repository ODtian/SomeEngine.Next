using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class UnboundStructuralMutationIsolationTests
{
    [Fact]
    public async Task ConcurrentBatchesDoNotLoseIndependentEntityUpdates()
    {
        var world = new World();
        Entity[] entities = Enumerable.Range(0, 32).Select(_ => world.CreateEntity(new Value(0))).ToArray();
        using var start = new ManualResetEventSlim();
        Task[] writers = entities.Select((entity, index) => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            using var commands = new CommandBuffer(world);
            commands.Replace(entity, new Value(index + 1));
            commands.Playback();
        })).ToArray();
        start.Set();
        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(Enumerable.Range(1, 32), entities.Select(entity => world.Read<Value>(entity).Number));
        Assert.Equal(32, world.EntityCount);
    }
    private readonly record struct Value(int Number) : IComponent;
}

using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class AsyncSaveSchedulerTests
{
    [Fact]
    public async Task A_world_with_a_scheduler_saves_asynchronously_and_reads_back()
    {
        SaveRegistry registry = CreateRegistry();
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity root = world.Create();
        Entity row = world.Create(new SavedRow { Value = 7, Target = root });
        using var stream = new MemoryStream();

        await new WorldSerializer(registry).SaveAsync(world, stream);

        stream.Position = 0;
        using World restored = WorldSerializer.ReadWorld(stream, registry);
        Assert.Equal(7, restored.Read<SavedRow>(row).Value);
        Assert.Equal(root, restored.Read<SavedRow>(row).Target);
    }

    [Fact]
    public void A_world_without_a_scheduler_finishes_its_save_before_SaveAsync_returns()
    {
        SaveRegistry registry = CreateRegistry();
        using var world = new World();
        Entity root = world.Create();
        Entity row = world.Create(new SavedRow { Value = 7, Target = root });
        using var stream = new MemoryStream();

        Task save = new WorldSerializer(registry).SaveAsync(world, stream);

        Assert.True(save.IsCompletedSuccessfully);
        stream.Position = 0;
        using World restored = WorldSerializer.ReadWorld(stream, registry);
        Assert.Equal(7, restored.Read<SavedRow>(row).Value);
    }

    [Fact]
    public async Task A_failed_save_of_a_world_without_a_scheduler_faults_the_returned_task()
    {
        SaveRegistry registry = CreateRegistry();
        using var world = new World();
        world.Create(new SavedRow { Value = 7 });
        using var readOnly = new MemoryStream([], writable: false);

        Task save = new WorldSerializer(registry).SaveAsync(world, readOnly);

        Assert.True(save.IsFaulted);
        await Assert.ThrowsAsync<NotSupportedException>(() => save);
    }

    [Fact]
    public async Task SaveAsync_writes_the_same_bytes_as_Save()
    {
        SaveRegistry registry = CreateRegistry().RegisterResource<SupplyBefore,
            global::__SomeEngine_ECSSerializer_global__SomeEngine_ECS_Serialization_Tests_SupplyBefore>(
            new SavedTypeKey(Guid.Parse("0c4f8e2a-61d3-4b97-a5e0-3f9d2c7b1100"), "Supply", 1));
        using var world = new World();
        Entity root = world.Create();
        Entity row = world.Create(new SavedRow { Value = 7, Target = root });
        world.Set(row, new NoteBefore { Page = 1, Text = "note" });
        world.Set(row, new ShelfBefore { Row = 2 });
        world.SetResource(new SupplyBefore { Stock = 9 });
        var options = new SerializeOptions(Purpose: SavePurpose.DurableSave);
        using var direct = new MemoryStream();
        using var captured = new MemoryStream();

        new WorldSerializer(registry).Save(world, direct, options);
        await new WorldSerializer(registry).SaveAsync(world, captured, options);

        Assert.Equal(direct.ToArray(), captured.ToArray());
    }

    private static SaveRegistry CreateRegistry()
    {
        var registry = new SaveRegistry();
        GeneratedSerializers.RegisterAll(registry);
        return registry;
    }
}

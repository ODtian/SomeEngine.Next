using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class WorldControlPlaneAdmissionTests : JobTest
{
    [Fact]
    public async Task AcquiredVersionsRemainUniqueWhileAStructureRangeIsOpen()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task mutation = Task.Run(() =>
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            world.Add(entity, new VersionedValue(1));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            ulong acquired = await Task.Run(world.AcquireSystemVersion).WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(acquired + 1, world.ComponentDataWriteVersion<VersionedValue>(entity));
            Assert.Equal(acquired + 1, world.Version);
        }
        finally { release.Set(); await mutation.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task QueryReadsTheVersionAndValuesAfterTheStructureWriteCompletes()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        Query query = world.Query<VersionedValue>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ulong readVersion = 0;
        int value = 0;
        Task mutation = Task.Run(() =>
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            world.Add(entity, new VersionedValue(7));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            events.Enqueue("write range ended");
        });
        Task reading = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            reading = Task.Run(() =>
            {
                attempted.Set();
                using QueryScope rows = query.OpenRead();
                readVersion = world.Version;
                foreach (QueryChunk chunk in rows)
                    foreach (VersionedValue item in chunk.Read<VersionedValue>()) value += item.Value;
                events.Enqueue("query read");
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            await Task.WhenAll(mutation, reading).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["write range ended", "query read"], events);
            Assert.Equal(7, value);
            Assert.Equal(world.ComponentDataWriteVersion<VersionedValue>(entity), readVersion);
            Assert.Equal(world.Version, readVersion);
        }
        finally { release.Set(); await Task.WhenAll(mutation, reading).WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private readonly record struct VersionedValue(int Value) : ITrackedComponent;
}

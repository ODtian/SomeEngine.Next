using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SerializationConsumerTests
{
    [Fact]
    public void SerializationContextCanRoundTripEmptyWorld()
    {
        var world = new World();
        var registry = new SerializationRegistry();
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(world, stream);

        stream.Position = 0;
        using World restored = WorldSerializer.ReadWorld(stream, registry);

        Assert.Equal(world.EntityCount, restored.EntityCount);
    }
    [Theory]
    [InlineData(SavePurpose.RawCheckpoint)]
    [InlineData(SavePurpose.DurableSave)]
    public void NamedDestroyedReaderResumesUnconsumedFactsAfterFileRoundTrip(SavePurpose purpose)
    {
        Guid readerId = Guid.Parse("7c876104-242a-45fd-a557-5a809b496193");
        using var source = new World();
        using DestroyedReader reader = source.WatchDestroyed(readerId);
        var first = source.Create();
        var second = source.Create();
        var third = source.Create();
        source.Destroy(first);
        source.Destroy(second);
        using (DestroyedRead read = reader.Open())
        {
            Assert.Equal(2, read.Count);
            Assert.Equal(2UL, read.Through);
        }
        reader.Advance(1);
        source.Destroy(third);
        var registry = new SerializationRegistry();
        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(source, output, purpose, CheckpointProgress.Preserve);
        output.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(output, registry, new WorldLoadOptions(RequiredPurpose: purpose));
        using DestroyedReader resumed = loaded.WatchDestroyed(readerId);
        Assert.Equal(1UL, resumed.Position);
        using (DestroyedRead read = resumed.Open())
        {
            Assert.Equal(new[] { new DestroyedEntity(second, 2), new DestroyedEntity(third, 3) }, read.First.ToArray());
            Assert.Equal(3UL, read.Through);
        }
        using DestroyedReader newlyRegistered = loaded.WatchDestroyed(Guid.Parse("a86e0645-3ad3-49b3-846c-f5dcbf35f8bb"));
        Assert.Equal(3UL, newlyRegistered.Position);
        using (DestroyedRead read = newlyRegistered.Open()) Assert.Equal(0, read.Count);
        resumed.Advance(3);
        using (DestroyedRead read = resumed.Open()) Assert.Equal(0, read.Count);
    }
    [Fact]
    public void CandidateCommandsKeepSourceMembershipFactsUntilSuccessfulRestore()
    {
        using var source = new World();
        Query query = source.Query(new QueryDescription().With<ObservedMember>());
        using EventReader<MembershipChange> reader = source.WatchMembership(query);
        source.Create(new ObservedMember { Value = 1 });
        Entity second = source.Create(new ObservedMember { Value = 2 });
        reader.Advance(1);
        using var original = new WorldCheckpoint();
        original.Capture(source, CheckpointProgress.Preserve);
        using (var failedCandidate = new World())
        {
            original.Restore(failedCandidate);
            using var commands = new CommandBuffer(failedCandidate);
            DeferredEntity prefix = commands.Create(new ObservedMember { Value = 3 });
            commands.Set(Entity.Null, new ObservedMember { Value = 4 });
            Assert.Throws<ArgumentException>(commands.Playback);
            Assert.Equal(1, commands.CompletedCommands);
            Assert.True(failedCandidate.IsAlive(prefix.Resolve()));
            Assert.Equal(2, query.Count);
            using EventRead<MembershipChange> read = reader.Open(reader.Capture());
            Assert.Equal([new MembershipChange(second, MembershipChangeKind.Added, 2)], read.First.ToArray());
        }
        using var successfulCandidate = new World();
        original.Restore(successfulCandidate);
        Entity created;
        using (var commands = new CommandBuffer(successfulCandidate))
        {
            DeferredEntity result = commands.Create(new ObservedMember { Value = 3 });
            commands.Playback();
            created = result.Resolve();
        }
        using var completed = new WorldCheckpoint();
        completed.Capture(successfulCandidate, CheckpointProgress.Preserve);
        completed.Restore(source);
        Assert.Equal(3, query.Count);
        Assert.Equal(1UL, reader.Position);
        using (EventRead<MembershipChange> read = reader.Open(reader.Capture()))
            Assert.Equal([new MembershipChange(second, MembershipChangeKind.Added, 2),
                new MembershipChange(created, MembershipChangeKind.Added, 3)], read.First.ToArray());
    }

    [Fact]
    public void WorldSaveKeepsEveryEntityAndOnlyRegisteredValues()
    {
        using var source = new World();
        Entity selected = source.Create(new SerPosition { X = 11, Y = 17 }, new SerVelocity { X = 23, Y = 29 });
        source.SetShared(selected, new SerScene { Value = 31 });
        source.SetSparse(selected, new SerSparse { Value = 37 });
        Entity other = source.Create(new SerVelocity { X = 41, Y = 43 });
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var direct = new MemoryStream();
        using var captured = new MemoryStream();
        var serializer = new WorldSerializer(registry);
        serializer.Save(source, direct);
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(source);
        serializer.Save(checkpoint, captured);
        foreach (MemoryStream input in new[] { direct, captured })
        {
            input.Position = 0;
            using World loaded = WorldSerializer.ReadWorld(input, registry);
            Assert.Equal(2, loaded.EntityCount);
            Assert.True(loaded.IsAlive(selected));
            Assert.True(loaded.IsAlive(other));
            Assert.Equal(11, loaded.Read<SerPosition>(selected).X);
            Assert.False(loaded.Has<SerVelocity>(selected));
            Assert.False(loaded.Has<SerVelocity>(other));
            Assert.False(loaded.Has<SerScene>(selected));
            Assert.False(loaded.HasSparse<SerSparse>(selected));
        }
    }

    private struct ObservedMember : IComponent
    {
        public int Value;
    }
}

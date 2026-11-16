using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Tests;

// The World learns how to release, group and remap a value from the interfaces the value type declares.
public sealed class ComponentDeclarationTests
{
    [Fact]
    public void A_disposable_component_is_disposed_when_it_is_removed()
    {
        using var world = new World();
        var releases = new ReleaseCount();
        Entity entity = world.Create(new CountedResource { Releases = releases });

        world.Remove<CountedResource>(entity);

        Assert.Equal(1, releases.Value);
    }

    [Fact]
    public void A_loaded_save_groups_the_world_by_its_saved_group_component()
    {
        using var source = new World();
        source.GroupBy<SavedZone>();
        source.Create(new SavedZone { Value = 2 });
        var registry = new SerializationRegistry().Register<SavedZone, SavedZoneCodec>(
            new SerializationTypeKey(Guid.Parse("8d1f6a2c-3b4e-4f5a-9c6d-7e8f9a0b1c00"), "SavedZone", 1));
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;

        using World loaded = WorldSerializer.ReadWorld(stream, registry);

        var groups = new List<int>();
        using (QueryScope scope = loaded.Query<SavedZone>().OpenRead())
            foreach (QueryChunk chunk in scope)
                groups.Add(chunk.Group<SavedZone>());
        Assert.Equal([2], groups);
    }

    [Fact]
    public void A_hand_written_entity_reference_is_remapped_when_an_entity_block_is_loaded()
    {
        using var source = new World();
        Entity target = source.Create();
        Entity link = source.Create(new TargetLink { Target = target });
        var registry = new SerializationRegistry().Register<TargetLink, TargetLinkCodec>(
            new SerializationTypeKey(Guid.Parse("8d1f6a2c-3b4e-4f5a-9c6d-7e8f9a0b1d00"), "TargetLink", 1));
        using var stream = new MemoryStream();
        WorldSerializer.WriteEntities(stream, source, [target, link], registry, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        using var loaded = new World();
        loaded.Create();
        loaded.Create();

        Entity[] members = WorldSerializer.ReadEntities(stream.ToArray().AsMemory(), loaded, registry);

        Assert.Equal(members[0], loaded.Read<TargetLink>(members[1]).Target);
    }

    private sealed class ReleaseCount
    {
        public int Value;
    }

    private struct CountedResource : IComponent, IDisposableComponent<CountedResource>
    {
        public ReleaseCount Releases;
        public readonly void Dispose() => Releases.Value++;
    }

    private struct SavedZone : IGroupComponent<SavedZone>
    {
        public int Value;
        public readonly int GetGroup() => Value;
    }

    private readonly struct SavedZoneCodec : IDurableComponentCodec<SavedZone>
    {
        public void Write(ref DataWriter writer, in SavedZone value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out SavedZone value) => value = new SavedZone { Value = reader.ReadInt32() };
    }

    private struct TargetLink : IComponent, IEntityReferenceValue<TargetLink>
    {
        public Entity Target;
        static void IEntityReferenceValue<TargetLink>.Remap(ref TargetLink value, in EntityMap entities)
            => value.Target = entities.Map(value.Target);
    }

    private readonly struct TargetLinkCodec : IDurableComponentCodec<TargetLink>
    {
        public void Write(ref DataWriter writer, in TargetLink value) => writer.WriteEntity(value.Target);
        public void Read(ref DataReader reader, out TargetLink value) => value = new TargetLink { Target = reader.ReadEntity() };
    }
}

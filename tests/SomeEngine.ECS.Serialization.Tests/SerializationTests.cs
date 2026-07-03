using System.Buffers.Binary;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;
using Xunit;

namespace SomeEngine.ECS.Serialization.Tests;

public struct SerPosition : SomeEngine.ECS.Components.IComponent
{
    public float X;
    public float Y;
}

public struct SerVelocity : SomeEngine.ECS.Components.IComponent
{
    public float X;
    public float Y;
}

public struct SerName : SomeEngine.ECS.Components.IComponent
{
    public string? Value;
    public int Id;
}

public struct SerVisible : SomeEngine.ECS.Components.IEnableableComponent
{
    public int Value;
}

public struct SerPlayerTag : SomeEngine.ECS.Components.ITag { }

public struct SerEnemyTag : SomeEngine.ECS.Components.ITag { }

public struct SerScene : SomeEngine.ECS.Components.ISharedComponent, IEquatable<SerScene>
{
    public int Value;

    public bool Equals(SerScene other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is SerScene other && Equals(other);
    public override int GetHashCode() => Value;
}

[BufferCapacity(2)]
public struct SerElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct SerSparse : SomeEngine.ECS.Components.ISparseComponent
{
    public int Value;
}

public struct SerRelation : SomeEngine.ECS.Components.IRelation
{
    public int Value;
}

public struct SerExternal : SomeEngine.ECS.Components.IComponent
{
    public ExternalReferenceKey Id;
}

[SerializableComponent("55555555-5555-5555-5555-555555555555")]
public partial struct GeneratedNestedRef : SomeEngine.ECS.Components.IComponent
{
    public Entity Target;
}

[SerializableComponent("66666666-6666-6666-6666-666666666666")]
public partial struct GeneratedManagedRefComponent : SomeEngine.ECS.Components.IComponent
{
    public int Value;
    public string? Name;
    public Entity Target;
    public GeneratedNestedRef Nested;
}

public struct SerNameCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerName>
{
    public void Write(ref DataWriter writer, in SerName value)
    {
        writer.WriteString(value.Value);
        writer.WriteInt32(value.Id);
    }

    public void Read(ref DataReader reader, out SerName value)
    {
        value = new SerName
        {
            Value = reader.ReadString(),
            Id = reader.ReadInt32(),
        };
    }
}

public struct SerPositionFullCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerPosition>
{
    public void Write(ref DataWriter writer, in SerPosition value)
    {
        writer.WriteSingle(value.X);
        writer.WriteSingle(value.Y);
    }

    public void Read(ref DataReader reader, out SerPosition value)
    {
        value = new SerPosition
        {
            X = reader.ReadSingle(),
            Y = reader.ReadSingle(),
        };
    }
}

public struct SerPositionXOnlyCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerPosition>
{
    public void Write(ref DataWriter writer, in SerPosition value)
    {
        writer.WriteSingle(value.X);
    }

    public void Read(ref DataReader reader, out SerPosition value)
    {
        value = new SerPosition
        {
            X = reader.ReadSingle(),
        };
    }
}

public struct SerExternalCodec : SomeEngine.ECS.Serialization.IComponentCodec<SerExternal>
{
    public void Write(ref DataWriter writer, in SerExternal value)
    {
        writer.WriteExternalReference(value.Id);
    }

    public void Read(ref DataReader reader, out SerExternal value)
    {
        value = new SerExternal { Id = reader.ReadExternalReference() };
    }
}

public sealed class SerPositionXMigration : IMigrationStep
{
    public SerPositionXMigration(SerializationTypeKey from, SerializationTypeKey to)
    {
        From = from;
        To = to;
    }

    public SerializationTypeKey From { get; }
    public SerializationTypeKey To { get; }

    public void Migrate(ref MigrationReader reader, ref MigrationWriter writer)
    {
        writer.WriteSingle(reader.ReadSingle());
        writer.WriteSingle(0);
    }
}

public class SerializationTests
{
    [Fact]
    public void Component_UnmanagedValue_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1.5f, Y = 2.5f }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerPosition>(stream, registry);

        Assert.Equal(1.5f, value.X);
        Assert.Equal(2.5f, value.Y);
    }

    [Fact]
    public void Component_ManagedValue_RequiresCodec()
    {
        var registry = new SerializationRegistry().Register<SerName>();
        using var stream = new MemoryStream();

        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteComponent(stream, new SerName { Value = "alpha", Id = 7 }, registry));
    }

    [Fact]
    public void Component_ManagedValue_WithCodec_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerName, SerNameCodec>();
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerName { Value = "beta", Id = 8 }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerName>(stream, registry);

        Assert.Equal("beta", value.Value);
        Assert.Equal(8, value.Id);
    }

    [Fact]
    public void Entity_AddOrSetIncluded_PreservesUnrelatedComponents()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var sourceWorld = new World();
        var source = sourceWorld.CreateEntity(new SerPosition { X = 3, Y = 4 });
        sourceWorld.Add(source, new SerVelocity { X = 100, Y = 200 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteEntity(stream, sourceWorld, source, registry);

        var targetWorld = new World();
        var target = targetWorld.CreateEntity(new SerVelocity { X = 5, Y = 6 });

        stream.Position = 0;
        WorldSerializer.ApplyEntity(stream, targetWorld, target, registry);

        Assert.True(targetWorld.Has<SerPosition>(target));
        Assert.True(targetWorld.Has<SerVelocity>(target));
        Assert.Equal(3, targetWorld.Read<SerPosition>(target).X);
        Assert.Equal(5, targetWorld.Read<SerVelocity>(target).X);
    }

    [Fact]
    public void Entity_ReplaceEntity_ReplacesLogicalStorage()
    {
        var registry = FullRegistry().Register<SerVelocity>().RegisterTag<SerEnemyTag>();
        var world = new World();
        var relationTarget = world.CreateEntity();
        var staleRelationTarget = world.CreateEntity();

        var source = world.CreateEntity(new SerPosition { X = 10, Y = 20 });
        world.AddTag<SerPlayerTag>(source);
        world.AddShared(source, new SerScene { Value = 42 });
        world.AddBuffer<SerElement>(source);
        var sourceBuffer = world.GetBuffer<SerElement>(source);
        sourceBuffer.Add(new SerElement { Value = 1 });
        sourceBuffer.Add(new SerElement { Value = 2 });
        sourceBuffer.Add(new SerElement { Value = 3 });
        world.AddSparse(source, new SerSparse { Value = 99 });
        world.AddRelation(source, relationTarget, new SerRelation { Value = 77 });

        var target = world.CreateEntity(new SerVelocity { X = 5, Y = 6 });
        world.AddTag<SerEnemyTag>(target);
        world.AddShared(target, new SerScene { Value = 1 });
        world.AddBuffer<SerElement>(target);
        world.GetBuffer<SerElement>(target).Add(new SerElement { Value = 123 });
        world.AddSparse(target, new SerSparse { Value = 3 });
        world.AddRelation(target, staleRelationTarget, new SerRelation { Value = 4 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteEntity(stream, world, source, registry);

        stream.Position = 0;
        WorldSerializer.ApplyEntity(
            stream,
            world,
            target,
            registry,
            new EntityApplyOptions(ApplyMode: EntityApplyMode.ReplaceEntity));

        Assert.True(world.Has<SerPosition>(target));
        Assert.False(world.Has<SerVelocity>(target));
        Assert.True(world.Has<SerPlayerTag>(target));
        Assert.False(world.Has<SerEnemyTag>(target));
        Assert.Equal(42, world.GetShared<SerScene>(target).Value);
        Assert.Equal([1, 2, 3], world.GetBuffer<SerElement>(target).AsSpan().ToArray().Select(x => x.Value));
        Assert.Equal(99, world.GetSparse<SerSparse>(target).Value);
        var relation = Assert.Single(world.GetRelations<SerRelation>(target).ToArray());
        Assert.Equal(relationTarget, relation.Target);
        Assert.Equal(77, relation.Value.Value);
    }

    [Fact]
    public void Entity_ReplaceIncluded_RemovesMissingRegisteredItemsOnly()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .Register<SerVelocity>();
        var sourceWorld = new World();
        var source = sourceWorld.CreateEntity(new SerPosition { X = 7, Y = 8 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteEntity(stream, sourceWorld, source, registry);

        var targetWorld = new World();
        var target = targetWorld.CreateEntity(new SerVelocity { X = 1, Y = 2 });
        targetWorld.AddTag<SerEnemyTag>(target);

        stream.Position = 0;
        WorldSerializer.ApplyEntity(
            stream,
            targetWorld,
            target,
            registry,
            new EntityApplyOptions(ApplyMode: EntityApplyMode.ReplaceIncluded));

        Assert.True(targetWorld.Has<SerPosition>(target));
        Assert.False(targetWorld.Has<SerVelocity>(target));
        Assert.True(targetWorld.Has<SerEnemyTag>(target));
        Assert.Equal(7, targetWorld.Read<SerPosition>(target).X);
    }

    [Fact]
    public void ApplyEntity_DoesNotPopulateDeltaJournal()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var sourceWorld = new World();
        var source = sourceWorld.CreateEntity(new SerPosition { X = 11, Y = 12 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteEntity(stream, sourceWorld, source, registry);

        var targetWorld = new World();
        var target = targetWorld.CreateEntity();
        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, targetWorld, registry, new DeltaSerializeOptions(ClearJournal: true));

        stream.Position = 0;
        WorldSerializer.ApplyEntity(stream, targetWorld, target, registry);

        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, targetWorld, registry);
        delta.Position = 0;

        Assert.Empty(WorldSerializer.ReadDeltaEvents(delta));
        Assert.Equal(11, targetWorld.Read<SerPosition>(target).X);
    }

    [Fact]
    public void WorldSnapshot_PreservesIdentityAndLogicalStorage()
    {
        var registry = FullRegistry();
        var world = new World();
        var live = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var dead = world.CreateEntity();
        var target = world.CreateEntity(new SerPosition { X = 9, Y = 10 });
        world.DestroyEntity(dead);

        world.Add(live, new SerVisible { Value = 5 });
        world.Disable<SerVisible>(live);
        world.AddTag<SerPlayerTag>(live);
        world.AddShared(live, new SerScene { Value = 11 });
        world.AddBuffer<SerElement>(live);
        var buffer = world.GetBuffer<SerElement>(live);
        buffer.Add(new SerElement { Value = 4 });
        buffer.Add(new SerElement { Value = 5 });
        buffer.Add(new SerElement { Value = 6 });
        world.AddSparse(live, new SerSparse { Value = 12 });
        world.AddRelation(live, target, new SerRelation { Value = 13 });

        using var first = new MemoryStream();
        using var second = new MemoryStream();
        WorldSerializer.WriteWorld(first, world, registry);
        WorldSerializer.WriteWorld(second, world, registry);

        Assert.Equal(first.ToArray(), second.ToArray());

        first.Position = 0;
        var loaded = WorldSerializer.ReadWorld(first, registry);

        Assert.True(loaded.IsAlive(live));
        Assert.True(loaded.IsAlive(target));
        Assert.False(loaded.IsAlive(dead));
        Assert.Equal(1, loaded.Read<SerPosition>(live).X);
        Assert.False(loaded.IsEnabled<SerVisible>(live));
        Assert.True(loaded.Has<SerPlayerTag>(live));
        Assert.Equal(11, loaded.GetShared<SerScene>(live).Value);
        Assert.Equal([4, 5, 6], loaded.GetBuffer<SerElement>(live).AsSpan().ToArray().Select(x => x.Value));
        Assert.Equal(12, loaded.GetSparse<SerSparse>(live).Value);
        var relation = Assert.Single(loaded.GetRelations<SerRelation>(live).ToArray());
        Assert.Equal(target, relation.Target);
        Assert.Equal(13, relation.Value.Value);

        var reused = loaded.CreateEntity();
        Assert.Equal(dead.Index, reused.Index);
        Assert.NotEqual(dead, reused);
    }

    [Fact]
    public void Read_FailsForCorruptHeader()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var stream = new MemoryStream([1, 2, 3]);

        Assert.Throws<EndOfStreamException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, registry));
    }

    [Fact]
    public void Read_FailsForTruncatedRawPayload()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var valid = new MemoryStream();
        WorldSerializer.WriteComponent(valid, new SerPosition { X = 1, Y = 2 }, registry);

        var bytes = valid.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 12, 4), 4);
        using var truncated = new MemoryStream(bytes.AsSpan(0, bytes.Length - 4).ToArray());

        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(truncated, registry));
    }

    [Fact]
    public void Read_FailsForSchemaMismatch()
    {
        var stableId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition>(new SerializationTypeKey(stableId, "SerPosition", 1));
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition>(new SerializationTypeKey(stableId, "SerPosition", 2));
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerPosition { X = 1, Y = 2 }, writeRegistry);

        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadComponent<SerPosition>(stream, readRegistry));
    }

    [Fact]
    public void ReadWorld_FailsForInconsistentSlotPayloadIdentity()
    {
        AssertInvalidWorldPayload(writer =>
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write(0);
            writer.Write(false);

            writer.Write(1);
            writer.Write(1);
            writer.Write(0);
            writer.Write(0);
        });

        AssertInvalidWorldPayload(writer =>
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write(0);
            writer.Write(true);

            writer.Write(0);
        });
    }

    [Fact]
    public void ReadWorld_RemapMode_ImportsIntoFreshIdentities()
    {
        var registry = FullRegistry();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, world, registry);

        stream.Position = 0;
        var loaded = WorldSerializer.ReadWorld(
            stream,
            registry,
            new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap));

        Assert.Equal(1, loaded.EntityCount);
    }

    [Fact]
    public void LoadInto_ReusedEmptyWorld_ClearsOwnerState()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        var sourceEntity = source.CreateEntity(new SerPosition { X = 7, Y = 8 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, source, registry);

        var target = new World();
        var staleParent = target.CreateEntity();
        var stale = target.CreateEntity();
        target.AddSparse(stale, new SerSparse { Value = 99 });
        target.Add(stale, new Parent { Value = staleParent });
        _ = target.Query(target.QueryDefinition().Read<SerVelocity>());
        Span<int> bundleIds = [ComponentMetadata<SerVelocity>.Id];
        target.ReserveBundle(bundleIds, 1);
        UnorderedHierarchy.Update(target);
        target.DestroyEntity(stale);
        target.DestroyEntity(staleParent);
        UnorderedHierarchy.Update(target);

        stream.Position = 0;
        WorldSerializer.LoadInto(stream, target, registry);

        Assert.True(target.IsAlive(sourceEntity));
        Assert.Equal(7, target.Read<SerPosition>(sourceEntity).X);
        Assert.False(target.HasSparse<SerSparse>(sourceEntity));

        var parent = target.CreateEntity();
        target.Add(sourceEntity, new Parent { Value = parent });
        UnorderedHierarchy.Update(target);
        Assert.Equal(new[] { sourceEntity }, UnorderedHierarchy.GetChildren(target, parent).ToArray());

        var query = target.Query(target.QueryDefinition().Read<SerPosition>());
        int matched = 0;
        foreach (var _ in target.RunQuery(query).Rows)
            matched++;
        Assert.Equal(1, matched);

        var writer = target.CreateAddWriter(sourceEntity, bundleIds);
        writer.Write(new SerVelocity { X = 1, Y = 2 });
        Assert.Equal(1, target.Read<SerVelocity>(sourceEntity).X);
    }

    [Fact]
    public void GeneratedCodec_ManagedNestedAndEntityReferences_RoundTrip()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        using var stream = new MemoryStream();
        var idWorld = new World();
        var target = idWorld.CreateEntity();

        WorldSerializer.WriteComponent(
            stream,
            new GeneratedManagedRefComponent
            {
                Value = 12,
                Name = "generated",
                Target = target,
                Nested = new GeneratedNestedRef { Target = target },
            },
            registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<GeneratedManagedRefComponent>(stream, registry);

        Assert.Equal(12, value.Value);
        Assert.Equal("generated", value.Name);
        Assert.Equal(target, value.Target);
        Assert.Equal(target, value.Nested.Target);
    }

    [Fact]
    public void EntitySetImport_RemapGeneratedEntityReferences()
    {
        var registry = new SerializationRegistry();
        GameSerializationModule.RegisterAll(registry);
        var sourceWorld = new World();
        var referenced = sourceWorld.CreateEntity();
        var source = sourceWorld.CreateEntity(new GeneratedManagedRefComponent
        {
            Value = 42,
            Name = "imported",
            Target = referenced,
            Nested = new GeneratedNestedRef { Target = referenced },
        });

        using var stream = new MemoryStream();
        Span<Entity> entities = [source, referenced];
        WorldSerializer.WriteEntities(stream, sourceWorld, entities, registry);

        var targetWorld = new World();
        stream.Position = 0;
        var created = WorldSerializer.CreateEntities(stream, targetWorld, registry);

        Assert.Equal(2, created.Length);
        var imported = targetWorld.Read<GeneratedManagedRefComponent>(created[0]);
        Assert.Equal(42, imported.Value);
        Assert.Equal("imported", imported.Name);
        Assert.Equal(created[1], imported.Target);
        Assert.Equal(created[1], imported.Nested.Target);

        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, targetWorld, registry);
        delta.Position = 0;
        Assert.Empty(WorldSerializer.ReadDeltaEvents(delta));
    }

    [Fact]
    public void RegisteredMigration_ConvertsEntityItemBeforeApply()
    {
        var stableId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var oldKey = new SerializationTypeKey(stableId, "SerPosition", 1);
        var newKey = new SerializationTypeKey(stableId, "SerPosition", 2);
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionXOnlyCodec>(oldKey);
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(newKey)
            .RegisterMigration(new SerPositionXMigration(oldKey, newKey));
        var sourceWorld = new World();
        var source = sourceWorld.CreateEntity(new SerPosition { X = 9, Y = 99 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteEntity(stream, sourceWorld, source, writeRegistry);

        var targetWorld = new World();
        var target = targetWorld.CreateEntity();
        stream.Position = 0;
        WorldSerializer.ApplyEntity(
            stream,
            targetWorld,
            target,
            readRegistry,
            new EntityApplyOptions(SchemaMismatchMode: SchemaMismatchMode.UseRegisteredMigration));

        var migrated = targetWorld.Read<SerPosition>(target);
        Assert.Equal(9, migrated.X);
        Assert.Equal(0, migrated.Y);
    }

    [Fact]
    public void ReadWorld_RegisteredMigration_ConvertsEntityItemsDuringLoad()
    {
        var stableId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var oldKey = new SerializationTypeKey(stableId, "SerPosition", 1);
        var newKey = new SerializationTypeKey(stableId, "SerPosition", 2);
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionXOnlyCodec>(oldKey);
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(newKey)
            .RegisterMigration(new SerPositionXMigration(oldKey, newKey));
        var sourceWorld = new World();
        var source = sourceWorld.CreateEntity(new SerPosition { X = 12, Y = 99 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteWorld(stream, sourceWorld, writeRegistry);

        stream.Position = 0;
        var loaded = WorldSerializer.ReadWorld(
            stream,
            readRegistry,
            new WorldLoadOptions(SchemaMismatchMode: SchemaMismatchMode.UseRegisteredMigration));

        var migrated = loaded.Read<SerPosition>(source);
        Assert.Equal(12, migrated.X);
        Assert.Equal(0, migrated.Y);
    }

    [Fact]
    public void QueryResult_SnapshotsCurrentMatches()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var sourceWorld = new World();
        var first = sourceWorld.CreateEntity(new SerPosition { X = 1, Y = 10 });
        sourceWorld.CreateEntity(new SerVelocity { X = 99, Y = 99 });
        var second = sourceWorld.CreateEntity(new SerPosition { X = 2, Y = 20 });
        var query = sourceWorld.Query(sourceWorld.QueryDefinition().Read<SerPosition>());

        using var stream = new MemoryStream();
        WorldSerializer.WriteQuery(stream, sourceWorld, query, registry);

        sourceWorld.Replace(first, new SerPosition { X = 100, Y = 100 });
        var targetWorld = new World();
        stream.Position = 0;
        var created = WorldSerializer.CreateQueryResult(stream, targetWorld, registry);

        Assert.Equal(2, created.Length);
        Assert.Equal(1, targetWorld.Read<SerPosition>(created[0]).X);
        Assert.Equal(2, targetWorld.Read<SerPosition>(created[1]).X);
        Assert.False(targetWorld.IsAlive(second));
    }

    [Fact]
    public void ExternalReferenceKey_CodecHelper_RoundTrips()
    {
        var registry = new SerializationRegistry().Register<SerExternal, SerExternalCodec>();
        var id = new ExternalReferenceKey(Guid.Parse("88888888-8888-8888-8888-888888888888"));
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, new SerExternal { Id = id }, registry);

        stream.Position = 0;
        var value = WorldSerializer.ReadComponent<SerExternal>(stream, registry);

        Assert.Equal(id, value.Id);
    }

    [Fact]
    public void DeltaEvents_RecordRequiredMutationCategories()
    {
        var registry = FullRegistry().Register<SerVelocity>();
        var world = new World();
        var target = world.CreateEntity();
        var entity = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var bufferEntity = world.CreateEntity();
        var sharedEntity = world.CreateEntity();

        world.Add(entity, new SerVisible { Value = 1 });
        world.Disable<SerVisible>(entity);
        world.Replace(entity, new SerPosition { X = 3, Y = 4 });
        world.AddTag<SerPlayerTag>(entity);
        world.RemoveTag<SerPlayerTag>(entity);
        world.AddShared(entity, new SerScene { Value = 5 });
        world.ReplaceShared(entity, new SerScene { Value = 6 });
        world.AddShared(sharedEntity, new SerScene { Value = 7 });
        world.RemoveShared<SerScene>(sharedEntity);
        world.AddBuffer<SerElement>(entity);
        world.GetBuffer<SerElement>(entity).Add(new SerElement { Value = 6 });
        world.AddBuffer<SerElement>(bufferEntity);
        world.RemoveBuffer<SerElement>(bufferEntity);
        world.AddSparse(entity, new SerSparse { Value = 7 });
        world.GetSparse<SerSparse>(entity).Value = 8;
        world.RemoveSparse<SerSparse>(entity);
        world.AddRelation(entity, target, new SerRelation { Value = 9 });
        world.ReplaceRelation(entity, target, new SerRelation { Value = 10 });
        world.RemoveRelation<SerRelation>(entity, target);
        world.Remove<SerVisible>(entity);
        world.DestroyEntity(entity);
        world.ClearRemoved<SerVisible>(world.CurrentTick);

        using var stream = new MemoryStream();
        WorldSerializer.WriteDelta(stream, world, registry, new DeltaSerializeOptions(ClearJournal: true));

        stream.Position = 0;
        var events = WorldSerializer.ReadDeltaEvents(stream);
        var kinds = events.Select(static e => e.Kind).ToHashSet();

        Assert.Contains(DeltaEventKind.EntityCreated, kinds);
        Assert.Contains(DeltaEventKind.EntityDestroyed, kinds);
        Assert.Contains(DeltaEventKind.ComponentAdded, kinds);
        Assert.Contains(DeltaEventKind.ComponentRemoved, kinds);
        Assert.Contains(DeltaEventKind.ComponentChanged, kinds);
        Assert.Contains(DeltaEventKind.TagAdded, kinds);
        Assert.Contains(DeltaEventKind.TagRemoved, kinds);
        Assert.Contains(DeltaEventKind.EnabledChanged, kinds);
        Assert.Contains(DeltaEventKind.SharedAdded, kinds);
        Assert.Contains(DeltaEventKind.SharedChanged, kinds);
        Assert.Contains(DeltaEventKind.SharedRemoved, kinds);
        Assert.Contains(DeltaEventKind.BufferAdded, kinds);
        Assert.Contains(DeltaEventKind.BufferChanged, kinds);
        Assert.Contains(DeltaEventKind.BufferRemoved, kinds);
        Assert.Contains(DeltaEventKind.SparseAdded, kinds);
        Assert.Contains(DeltaEventKind.SparseRemoved, kinds);
        Assert.Contains(DeltaEventKind.SparseChanged, kinds);
        Assert.Contains(DeltaEventKind.RelationAdded, kinds);
        Assert.Contains(DeltaEventKind.RelationRemoved, kinds);
        Assert.Contains(DeltaEventKind.RelationChanged, kinds);

        using var empty = new MemoryStream();
        WorldSerializer.WriteDelta(empty, world, registry);
        empty.Position = 0;
        Assert.Empty(WorldSerializer.ReadDeltaEvents(empty));
    }

    [Fact]
    public void DeltaEvents_RecordMutableTableRefAsComponentSet()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        var target = new World();
        var sourceEntity = source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var targetEntity = target.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Assert.Equal(sourceEntity, targetEntity);

        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, source, registry, new DeltaSerializeOptions(ClearJournal: true));

        ref var position = ref source.Get<SerPosition>(sourceEntity);
        position.X = 10;
        position.Y = 20;

        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, source, registry);
        delta.Position = 0;

        var deltaEvent = Assert.Single(WorldSerializer.ReadDeltaEvents(delta));
        Assert.Equal(DeltaEventKind.ComponentChanged, deltaEvent.Kind);
        Assert.Equal(sourceEntity, deltaEvent.Entity);

        delta.Position = 0;
        WorldSerializer.ApplyDelta(delta, target, registry);

        var applied = target.Read<SerPosition>(targetEntity);
        Assert.Equal(10, applied.X);
        Assert.Equal(20, applied.Y);
    }

    [Fact]
    public void DeltaEvents_RecordQueryMutableTableAccessAsComponentSet()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        var entity = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, world, registry, new DeltaSerializeOptions(ClearJournal: true));

        var query = world.Query(world.QueryDefinition().ReadWrite<SerPosition>());
        foreach (var row in world.RunQuery(query).Rows)
            row.ReadWrite<SerPosition>().X = 30;

        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, world, registry);
        delta.Position = 0;

        var deltaEvent = Assert.Single(WorldSerializer.ReadDeltaEvents(delta));
        Assert.Equal(DeltaEventKind.ComponentChanged, deltaEvent.Kind);
        Assert.Equal(entity, deltaEvent.Entity);
    }

    [Fact]
    public void BundleBufferWrite_RecordsSingleBufferChangedEvent()
    {
        var registry = new SerializationRegistry().RegisterBuffer<SerElement>();
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SerElement>(entity);
        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, world, registry, new DeltaSerializeOptions(ClearJournal: true));

        Span<int> componentIds =
        [
            BufferComponents.Header<SerElement>(),
            BufferComponents.Inline<SerElement>(),
        ];
        var writer = world.CreateReplaceWriter(entity, componentIds);
        writer.WriteBuffer(new BufferValues<SerElement>(new SerElement { Value = 1 }));

        using var stream = new MemoryStream();
        WorldSerializer.WriteDelta(stream, world, registry);

        stream.Position = 0;
        var events = WorldSerializer.ReadDeltaEvents(stream);
        var bufferEvent = Assert.Single(events);
        Assert.Equal(DeltaEventKind.BufferChanged, bufferEvent.Kind);
        Assert.Equal(entity, bufferEvent.Entity);
    }

    [Fact]
    public void BundleBufferAdd_RecordsSingleBufferAddedEvent()
    {
        var registry = new SerializationRegistry().RegisterBuffer<SerElement>();
        var world = new World();
        var entity = world.CreateEntity();
        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, world, registry, new DeltaSerializeOptions(ClearJournal: true));

        Span<int> componentIds =
        [
            BufferComponents.Header<SerElement>(),
            BufferComponents.Inline<SerElement>(),
        ];
        var writer = world.CreateAddWriter(entity, componentIds);
        writer.WriteBuffer(new BufferValues<SerElement>(new SerElement { Value = 1 }));

        using var stream = new MemoryStream();
        WorldSerializer.WriteDelta(stream, world, registry);

        stream.Position = 0;
        var events = WorldSerializer.ReadDeltaEvents(stream);
        var bufferEvent = Assert.Single(events);
        Assert.Equal(DeltaEventKind.BufferAdded, bufferEvent.Kind);
        Assert.Equal(entity, bufferEvent.Entity);
    }

    [Fact]
    public void BundleSparseWrite_RecordsSingleSparseSetEvent()
    {
        var registry = new SerializationRegistry().RegisterSparse<SerSparse>();
        var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new SerSparse { Value = 1 });
        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, world, registry, new DeltaSerializeOptions(ClearJournal: true));

        Span<int> sparseComponentIds = [ComponentMetadata<SerSparse>.Id];
        var writer = world.CreateReplaceWriter(entity, Span<int>.Empty, sparseComponentIds);
        writer.WriteSparse(new SerSparse { Value = 2 });

        using var stream = new MemoryStream();
        WorldSerializer.WriteDelta(stream, world, registry);
        stream.Position = 0;

        var deltaEvent = Assert.Single(WorldSerializer.ReadDeltaEvents(stream));
        Assert.Equal(DeltaEventKind.SparseChanged, deltaEvent.Kind);
        Assert.Equal(entity, deltaEvent.Entity);

        using var repeated = new MemoryStream();
        WorldSerializer.WriteDelta(repeated, world, registry);
        repeated.Position = 0;
        Assert.Single(WorldSerializer.ReadDeltaEvents(repeated));
    }

    [Fact]
    public void ReadWorld_DoesNotPopulateDeltaJournal()
    {
        var registry = FullRegistry();
        var source = new World();
        var target = source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var entity = source.CreateEntity(new SerPosition { X = 3, Y = 4 });
        source.AddRelation(entity, target, new SerRelation { Value = 5 });

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, source, registry);

        snapshot.Position = 0;
        var loaded = WorldSerializer.ReadWorld(snapshot, registry);

        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, loaded, registry);
        delta.Position = 0;
        Assert.Empty(WorldSerializer.ReadDeltaEvents(delta));
    }

    [Fact]
    public void ReadWorld_DefersRelationsUntilLaterPayloadTargetsExist()
    {
        var registry = FullRegistry();
        var source = new World();
        var relationSource = source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var relationTarget = source.CreateEntity(new SerPosition { X = 3, Y = 4 });
        source.AddRelation(relationSource, relationTarget, new SerRelation { Value = 9 });

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, source, registry);

        snapshot.Position = 0;
        var loaded = WorldSerializer.ReadWorld(snapshot, registry);

        var relation = Assert.Single(loaded.GetRelations<SerRelation>(relationSource).ToArray());
        Assert.Equal(relationTarget, relation.Target);
        Assert.Equal(9, relation.Value.Value);
    }

    [Fact]
    public void ApplyDelta_DoesNotPopulateDeltaJournal()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        var entity = source.CreateEntity(new SerPosition { X = 1, Y = 2 });

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, source, registry);
        snapshot.Position = 0;
        var target = WorldSerializer.ReadWorld(snapshot, registry);

        using (var clear = new MemoryStream())
            WorldSerializer.WriteDelta(clear, source, registry, new DeltaSerializeOptions(ClearJournal: true));

        source.Replace(entity, new SerPosition { X = 10, Y = 20 });
        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, source, registry);

        delta.Position = 0;
        WorldSerializer.ApplyDelta(delta, target, registry);

        using var echoedDelta = new MemoryStream();
        WorldSerializer.WriteDelta(echoedDelta, target, registry);
        echoedDelta.Position = 0;
        Assert.Empty(WorldSerializer.ReadDeltaEvents(echoedDelta));
        Assert.Equal(10, target.Read<SerPosition>(entity).X);
    }

    [Fact]
    public void ApplyDelta_ReplaysChangedEntityPayloadOntoBaseline()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        var target = new World();
        var sourceEntity = source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var targetEntity = target.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Assert.Equal(sourceEntity, targetEntity);

        using (var baseline = new MemoryStream())
            WorldSerializer.WriteDelta(baseline, source, registry, new DeltaSerializeOptions(ClearJournal: true));

        source.Replace(sourceEntity, new SerPosition { X = 10, Y = 20 });
        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, source, registry);

        delta.Position = 0;
        WorldSerializer.ApplyDelta(delta, target, registry);

        var applied = target.Read<SerPosition>(targetEntity);
        Assert.Equal(10, applied.X);
        Assert.Equal(20, applied.Y);
    }

    [Fact]
    public void ApplyDelta_CreatesEntitiesAddedAfterBaselineWhenIdentitySequenceMatches()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var source = new World();
        var target = new World();

        using (var baseline = new MemoryStream())
            WorldSerializer.WriteDelta(baseline, source, registry, new DeltaSerializeOptions(ClearJournal: true));

        var createdInSource = source.CreateEntity(new SerPosition { X = 30, Y = 40 });
        using var delta = new MemoryStream();
        WorldSerializer.WriteDelta(delta, source, registry);

        delta.Position = 0;
        WorldSerializer.ApplyDelta(delta, target, registry);

        Assert.True(target.IsAlive(createdInSource));
        Assert.Equal(30, target.Read<SerPosition>(createdInSource).X);
    }

    private static SerializationRegistry FullRegistry()
    {
        return new SerializationRegistry()
            .Register<SerPosition>()
            .Register<SerVisible>()
            .RegisterTag<SerPlayerTag>()
            .RegisterShared<SerScene>()
            .RegisterBuffer<SerElement>()
            .RegisterSparse<SerSparse>()
            .RegisterRelation<SerRelation>();
    }

    private static MemoryStream CreateWorldPayload(Action<BinaryWriter> writeBody)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0x53434553u);
            writer.Write((ushort)1);
            writer.Write((byte)0);
            writer.Write((byte)SnapshotPayloadKind.World);
            writer.Write(0);
            writer.Write(0u);
            writeBody(writer);
        }

        stream.Position = 0;
        return stream;
    }

    private static void AssertInvalidWorldPayload(Action<BinaryWriter> writeBody)
    {
        using var stream = CreateWorldPayload(writeBody);
        Assert.Throws<InvalidDataException>(() =>
            WorldSerializer.ReadWorld(stream, new SerializationRegistry()));
    }
}

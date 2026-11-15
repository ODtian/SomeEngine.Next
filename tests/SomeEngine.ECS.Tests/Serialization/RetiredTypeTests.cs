using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;
using CargoBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_CargoBefore;
using GateBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_GateBefore;
using HolderMarkCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_HolderMark;
using NoteBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_NoteBefore;
using ScrapBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ScrapBefore;
using ShelfBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ShelfBefore;
using SupplyBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_SupplyBefore;

namespace SomeEngine.ECS.Serialization.Tests;

// A save written while the game had a component type loads after the type is gone, once the registry retires it.
public sealed class RetiredTypeTests
{
    private const string Mark = "7b0e4f21-5c3a-4d2e-8f61-2a9c0d4e0100";
    private const string Retired = "7b0e4f21-5c3a-4d2e-8f61-2a9c0d4e0200";

    [Fact]
    public void A_retired_component_is_skipped_and_the_entity_keeps_its_other_components()
    {
        using World loaded = SaveThenLoad(
            world => WithMark(world, entity => world.Add(entity, new CargoBefore { Count = 3 })),
            registry => registry.RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    [Fact]
    public void A_saved_type_that_is_neither_registered_nor_retired_fails_the_load()
    {
        var error = Assert.Throws<InvalidDataException>(() => SaveThenLoad(
            world => WithMark(world, entity => world.Add(entity, new CargoBefore { Count = 3 })),
            registry => registry.RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Retired)),
            out _, retire: false).Dispose());

        Assert.Equal(SerializationError.UnknownType, SerializationErrors.GetError(error));
    }

    [Fact]
    public void A_retired_sparse_component_is_skipped()
    {
        using World loaded = SaveThenLoad(
            world =>
            {
                Entity entity = world.Create(new HolderMark { Number = 5 });
                world.Set(entity, new NoteBefore { Page = 1, Text = "gone" });
                return entity;
            },
            registry => registry.Register<NoteBefore, NoteBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    [Fact]
    public void A_retired_shared_component_is_skipped()
    {
        using World loaded = SaveThenLoad(
            world =>
            {
                Entity entity = world.Create(new HolderMark { Number = 5 });
                world.Set(entity, new ShelfBefore { Row = 2 });
                return entity;
            },
            registry => registry.Register<ShelfBefore, ShelfBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    [Fact]
    public void A_retired_resource_is_skipped()
    {
        using World loaded = SaveThenLoad(
            world =>
            {
                world.SetResource(new SupplyBefore { Stock = 9 });
                return world.Create(new HolderMark { Number = 5 });
            },
            registry => registry.RegisterResource<SupplyBefore, SupplyBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    [Fact]
    public void A_retired_component_saved_disabled_is_skipped()
    {
        using World loaded = SaveThenLoad(
            world =>
            {
                Entity entity = WithMark(world, entity => world.Add(entity, new GateBefore { Height = 3 }));
                world.Disable<GateBefore>(entity);
                return entity;
            },
            registry => registry.RegisterDurable<GateBefore, GateBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    [Fact]
    public void A_retired_component_with_a_kept_removed_value_is_skipped()
    {
        using World loaded = SaveThenLoad(
            world =>
            {
                Entity entity = WithMark(world, entity => world.Add(entity, new ScrapBefore { Grade = 6 }));
                world.Remove<ScrapBefore>(entity);
                return entity;
            },
            registry => registry.RegisterDurable<ScrapBefore, ScrapBeforeCodec>(Key(Retired)),
            out Entity entity, retire: true);

        Assert.Equal(5, loaded.Read<HolderMark>(entity).Number);
    }

    private static SerializationTypeKey Key(string id) => new(Guid.Parse(id), id, 1);

    private static Entity WithMark(World world, Action<Entity> add)
    {
        Entity entity = world.Create(new HolderMark { Number = 5 });
        add(entity);
        return entity;
    }

    // Saves a world whose retired type is registered, then loads it with a registry that only has HolderMark.
    private static World SaveThenLoad(Func<World, Entity> create, Func<SerializationRegistry, SerializationRegistry> registerRetired,
        out Entity entity, bool retire)
    {
        using var source = new World();
        entity = create(source);
        SerializationRegistry written = registerRetired(new SerializationRegistry()
            .RegisterDurable<HolderMark, HolderMarkCodec>(Key(Mark)));
        var read = new SerializationRegistry().RegisterDurable<HolderMark, HolderMarkCodec>(Key(Mark));
        if (retire) read.RetireType(Guid.Parse(Retired));
        using var stream = new MemoryStream();
        new WorldSerializer(written).Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        return WorldSerializer.ReadWorld(stream, read, new WorldLoadOptions(RequiredPurpose: SavePurpose.DurableSave));
    }
}

using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;
using CargoAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_CargoAfter;
using CargoBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_CargoBefore;
using GateBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_GateBefore;
using GateAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_GateAfter;
using ScrapBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ScrapBefore;
using ScrapAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ScrapAfter;
using HolderMarkCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_HolderMark;
using GaugeAfterCodec =global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_GaugeAfter;
using NoteAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_NoteAfter;
using NoteBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_NoteBefore;
using ShelfAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ShelfAfter;
using ShelfBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_ShelfBefore;
using StepAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_StepAfter;
using StepBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_StepBefore;
using SupplyAfterCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_SupplyAfter;
using SupplyBeforeCodec = global::__SomeEngine_ECSSerializationCodec_global__SomeEngine_ECS_Serialization_Tests_SupplyBefore;

namespace SomeEngine.ECS.Serialization.Tests;

// Each "Before" type stands for a component as an earlier game version saved it, and the matching
// "After" type for the same component in a later version. Both are registered under one key, so a
// save written with the first registry is read back with the second.
public sealed class SavedFieldChangeTests
{
    private const string Cargo = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0100";
    private const string Step = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0200";
    private const string Note = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0300";
    private const string Shelf = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0400";
    private const string Supply = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0500";
    private const string Plain = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0600";
    private const string Gate = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0a00";
    private const string Scrap = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0b00";

    [Fact]
    public void A_field_added_after_saving_takes_its_initial_value()
    {
        using World loaded = SaveCargoThenLoad(out Entity cargo, out _);

        Assert.Equal(100, loaded.Read<CargoAfter>(cargo).Durability);
    }

    [Fact]
    public void A_field_renamed_with_its_saved_identity_keeps_its_value()
    {
        using World loaded = SaveCargoThenLoad(out Entity cargo, out _);

        Assert.Equal(3, loaded.Read<CargoAfter>(cargo).Amount);
    }

    [Fact]
    public void A_field_removed_after_saving_leaves_the_fields_after_it_intact()
    {
        using World loaded = SaveCargoThenLoad(out Entity cargo, out Entity holder);

        Assert.Equal(holder, loaded.Read<CargoAfter>(cargo).Holder);
    }

    [Fact]
    public void A_field_whose_kind_changed_takes_its_initial_value()
    {
        using World loaded = SaveCargoThenLoad(out Entity cargo, out _);

        Assert.Equal(-1, loaded.Read<CargoAfter>(cargo).Level);
    }

    [Fact]
    public void A_nested_value_keeps_its_saved_fields_and_gives_new_fields_their_initial_value()
    {
        using World loaded = SaveCargoThenLoad(out Entity cargo, out _);

        LabelAfter label = loaded.Read<CargoAfter>(cargo).Label;
        Assert.Equal(4, label.Size);
        Assert.Equal(7, label.Color);
    }

    [Fact]
    public void An_entity_field_is_remapped_after_its_component_changed()
    {
        using var source = new World();
        Entity holder = source.Create(new HolderMark { Number = 42 });
        source.Create(new CargoBefore { Count = 3, Holder = holder });
        SerializationRegistry before = new SerializationRegistry()
            .RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Cargo, CargoBeforeCodec.Schema))
            .RegisterDurable<HolderMark, HolderMarkCodec>(Key(HolderMarkId, HolderMarkCodec.Schema));
        SerializationRegistry after = new SerializationRegistry()
            .RegisterDurable<CargoAfter, CargoAfterCodec>(Key(Cargo, CargoAfterCodec.Schema))
            .RegisterDurable<HolderMark, HolderMarkCodec>(Key(HolderMarkId, HolderMarkCodec.Schema));

        using World loaded = SaveThenLoad(source, before, after, EntityIdentityMode.Remap);

        Entity cargo = Single<CargoAfter>(loaded);
        Assert.Equal(42, loaded.Read<HolderMark>(loaded.Read<CargoAfter>(cargo).Holder).Number);
    }

    [Fact]
    public void An_entity_field_added_after_saving_is_null_after_remapping()
    {
        using var source = new World();
        source.Create(new CargoBefore { Count = 3 });

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Cargo, CargoBeforeCodec.Schema)),
            new SerializationRegistry().RegisterDurable<CargoAfter, CargoAfterCodec>(Key(Cargo, CargoAfterCodec.Schema)),
            EntityIdentityMode.Remap);

        Assert.Equal(Entity.Null, loaded.Read<CargoAfter>(Single<CargoAfter>(loaded)).Owner);
    }

    [Fact]
    public void A_disabled_component_stays_disabled_after_its_fields_changed()
    {
        using var source = new World();
        Entity entity = source.Create(new GateBefore { Width = 1, Height = 3 });
        source.Disable<GateBefore>(entity);

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterDurable<GateBefore, GateBeforeCodec>(Key(Gate, GateBeforeCodec.Schema)),
            new SerializationRegistry().RegisterDurable<GateAfter, GateAfterCodec>(Key(Gate, GateAfterCodec.Schema)));

        Assert.False(loaded.IsEnabled<GateAfter>(entity));
    }

    [Fact]
    public void A_removed_value_kept_for_a_changed_component_keeps_its_saved_fields()
    {
        using var source = new World();
        Entity entity = source.Create(new ScrapBefore { Mass = 2, Grade = 6 });
        source.Remove<ScrapBefore>(entity);

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterDurable<ScrapBefore, ScrapBeforeCodec>(Key(Scrap, ScrapBeforeCodec.Schema)),
            new SerializationRegistry().RegisterDurable<ScrapAfter, ScrapAfterCodec>(Key(Scrap, ScrapAfterCodec.Schema)));

        Assert.Equal(6, loaded.Read<Removed<ScrapAfter>>(entity).Value.Grade);
    }

    [Fact]
    public void A_packed_component_reads_a_save_written_before_a_field_was_added()
    {
        var after = new SerializationRegistry();
        GameSerializationModule.RegisterAll(after);
        using var source = new World();
        Entity entity = source.Create(new GaugeAfter { High = 8 });

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterDurable<GaugeAfter, GaugeAfterCodec>(Key(GaugeBeforeId, GaugeAfterCodec.Schema)),
            after);

        Assert.Equal((0, 8), (loaded.Read<GaugeBefore>(entity).Low, loaded.Read<GaugeBefore>(entity).High));
    }

    [Fact]
    public void An_entity_block_saved_before_a_component_changed_loads_its_saved_fields()
    {
        using var source = new World();
        Entity cargo = source.Create(new CargoBefore { Count = 3, Label = new LabelBefore { Size = 4 } });
        using var output = new MemoryStream();
        WorldSerializer.WriteEntities(output, source, [cargo],
            new SerializationRegistry().RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Cargo, CargoBeforeCodec.Schema)),
            new SerializeOptions(Purpose: SavePurpose.DurableSave));
        using var target = new World();

        Entity[] loaded = WorldSerializer.ReadEntities(output.ToArray().AsMemory(), target,
            new SerializationRegistry().RegisterDurable<CargoAfter, CargoAfterCodec>(Key(Cargo, CargoAfterCodec.Schema)));

        CargoAfter value = target.Read<CargoAfter>(Assert.Single(loaded));
        Assert.Equal((3, 100, 4, 7), (value.Amount, value.Durability, value.Label.Size, value.Label.Color));
    }

    [Fact]
    public void A_buffer_whose_element_gained_a_field_keeps_its_saved_elements()
    {
        using var source = new World();
        Entity entity = source.Create();
        source.AddBuffer<StepBefore>(entity);
        using (DynamicBuffer<StepBefore> steps = source.GetBuffer<StepBefore>(entity))
        {
            steps.Add(new StepBefore { Order = 1, Cost = 10 });
            steps.Add(new StepBefore { Order = 2, Cost = 20 });
        }

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterBuffer<StepBefore, StepBeforeCodec>(Key(Step, StepBeforeCodec.Schema)),
            new SerializationRegistry().RegisterBuffer<StepAfter, StepAfterCodec>(Key(Step, StepAfterCodec.Schema)));

        using ReadOnlyBuffer<StepAfter> loadedSteps = loaded.ReadBuffer<StepAfter>(entity);
        Assert.Equal([(1, 3), (2, 3)], loadedSteps.AsSpan().ToArray().Select(step => (step.Order, step.Time)));
    }

    [Fact]
    public void A_sparse_component_that_lost_a_field_keeps_its_other_fields()
    {
        using var source = new World();
        Entity entity = source.Create();
        source.Set(entity, new NoteBefore { Page = 9, Text = "kept" });

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().Register<NoteBefore, NoteBeforeCodec>(Key(Note, NoteBeforeCodec.Schema)),
            new SerializationRegistry().Register<NoteAfter, NoteAfterCodec>(Key(Note, NoteAfterCodec.Schema)));

        Assert.Equal("kept", loaded.Read<NoteAfter>(entity).Text);
    }

    [Fact]
    public void A_shared_component_that_lost_a_field_keeps_its_other_fields()
    {
        using var source = new World();
        Entity entity = source.Create();
        source.Set(entity, new ShelfBefore { Row = 2, Unused = 5 });

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().Register<ShelfBefore, ShelfBeforeCodec>(Key(Shelf, ShelfBeforeCodec.Schema)),
            new SerializationRegistry().Register<ShelfAfter, ShelfAfterCodec>(Key(Shelf, ShelfAfterCodec.Schema)));

        Assert.Equal(2, loaded.Read<ShelfAfter>(entity).Row);
    }

    [Fact]
    public void A_resource_that_gained_a_field_keeps_its_saved_fields()
    {
        using var source = new World();
        source.SetResource(new SupplyBefore { Stock = 9 });

        using World loaded = SaveThenLoad(source,
            new SerializationRegistry().RegisterResource<SupplyBefore, SupplyBeforeCodec>(Key(Supply, SupplyBeforeCodec.Schema)),
            new SerializationRegistry().RegisterResource<SupplyAfter, SupplyAfterCodec>(Key(Supply, SupplyAfterCodec.Schema)));

        Assert.Equal((9, 12), (loaded.ReadResource<SupplyAfter>().Stock, loaded.ReadResource<SupplyAfter>().Limit));
    }

    [Fact]
    public void A_component_saved_with_its_packed_layout_loads_after_a_field_was_removed()
    {
        var before = new SerializationRegistry();
        GameSerializationModule.RegisterAll(before);
        using var source = new World();
        Entity entity = source.Create(new GaugeBefore { Low = 1, High = 8 });

        using World loaded = SaveThenLoad(source, before,
            new SerializationRegistry().RegisterDurable<GaugeAfter, GaugeAfterCodec>(Key(GaugeBeforeId, GaugeAfterCodec.Schema)));

        Assert.Equal(8, loaded.Read<GaugeAfter>(entity).High);
    }

    [Fact]
    public void A_hand_written_codec_saved_under_a_different_key_is_rejected()
    {
        using var source = new World();
        source.Create(new PlainValue { Number = 1 });
        var written = new SerializationRegistry().Register<PlainValue, PlainValueCodec>(
            new SerializationTypeKey(Guid.Parse(Plain), "Plain", 1));
        var read = new SerializationRegistry().Register<PlainValue, PlainValueCodec>(
            new SerializationTypeKey(Guid.Parse(Plain), "Plain", 2));

        var error = Assert.Throws<InvalidDataException>(() => SaveThenLoad(source, written, read).Dispose());

        Assert.Equal(SerializationError.SchemaMismatch, SerializationErrors.GetError(error));
    }

    private const string HolderMarkId = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0700";
    private const string GaugeBeforeId = "3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0900";

    private static SerializationTypeKey Key(string id, ValueSchema schema) => new(Guid.Parse(id), id, schema.Hash);

    private static World SaveCargoThenLoad(out Entity cargo, out Entity holder)
    {
        using var source = new World();
        holder = source.Create();
        cargo = source.Create(new CargoBefore
        {
            Count = 3,
            Weight = 2.5f,
            Level = 5,
            Holder = holder,
            Label = new LabelBefore { Size = 4 },
        });
        return SaveThenLoad(source,
            new SerializationRegistry().RegisterDurable<CargoBefore, CargoBeforeCodec>(Key(Cargo, CargoBeforeCodec.Schema)),
            new SerializationRegistry().RegisterDurable<CargoAfter, CargoAfterCodec>(Key(Cargo, CargoAfterCodec.Schema)));
    }

    private static World SaveThenLoad(World source, SerializationRegistry written, SerializationRegistry read,
        EntityIdentityMode identity = EntityIdentityMode.Preserve)
    {
        using var stream = new MemoryStream();
        new WorldSerializer(written).Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        return WorldSerializer.ReadWorld(stream, read,
            new WorldLoadOptions(IdentityMode: identity, RequiredPurpose: SavePurpose.DurableSave));
    }

    private static Entity Single<T>(World world) where T : struct
    {
        Entity result = Entity.Null;
        using (QueryScope scope = world.Query<T>().OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++) result = chunk.Entities[row];
        return result;
    }

    private readonly struct PlainValueCodec : IDurableComponentCodec<PlainValue>
    {
        public void Write(ref DataWriter writer, in PlainValue value) => writer.WriteInt32(value.Number);
        public void Read(ref DataReader reader, out PlainValue value) => value = new PlainValue { Number = reader.ReadInt32() };
    }
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1100")]
public struct CargoBefore : IComponent
{
    public int Count;
    public float Weight;
    public long Level;
    public Entity Holder;
    public LabelBefore Label;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1200")]
public struct CargoAfter : IComponent
{
    [SerializedField("Count")] public int Amount;
    public int Level = -1;
    public Entity Holder;
    public LabelAfter Label;
    public int Durability = 100;
    public Entity Owner;

    public CargoAfter() { }
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1300")]
public struct LabelBefore : IComponent
{
    public int Size;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1400")]
public struct LabelAfter : IComponent
{
    public int Size;
    public int Color = 7;

    public LabelAfter() { }
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0700")]
public struct HolderMark : IComponent
{
    public int Number;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1500")]
public struct StepBefore : IBufferElement
{
    public int Order;
    public int Cost;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1600")]
public struct StepAfter : IBufferElement
{
    public int Order;
    public int Time = 3;

    public StepAfter() { }
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1700")]
public struct NoteBefore : ISparseComponent
{
    public int Page;
    public string Text;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1800")]
public struct NoteAfter : ISparseComponent
{
    public string Text;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1900")]
public struct ShelfBefore : ISharedComponent
{
    public int Row;
    public int Unused;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1a00")]
public struct ShelfAfter : ISharedComponent
{
    public int Row;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1b00")]
public struct SupplyBefore : IComponent
{
    public int Stock;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1c00")]
public struct SupplyAfter : IComponent
{
    public int Stock;
    public int Limit = 12;

    public SupplyAfter() { }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b0900")]
public partial struct GaugeBefore : IComponent
{
    public int Low;
    public int High;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b1d00")]
public struct GaugeAfter : IComponent
{
    public int High;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b2000")]
public struct GateBefore : IComponent, IEnableableComponent
{
    public int Width;
    public int Height;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b2100")]
public struct GateAfter : IComponent, IEnableableComponent
{
    public int Height;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b2200")]
public struct ScrapBefore : IComponent, IRemovedComponent
{
    public int Mass;
    public int Grade;
}

[SerializableComponent("3f1c0a52-7d1e-4c1b-9a51-0d6c2f9b2300")]
public struct ScrapAfter : IComponent, IRemovedComponent
{
    public int Grade;
}

public struct PlainValue : IComponent
{
    public int Number;
}

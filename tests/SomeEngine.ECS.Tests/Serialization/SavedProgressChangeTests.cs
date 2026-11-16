using SomeEngine.ECS.Entities;
using MeterAfterSerializer = global::__SomeEngine_ECSSerializer_global__SomeEngine_ECS_Serialization_Tests_MeterAfter;
using MeterBeforeSerializer = global::__SomeEngine_ECSSerializer_global__SomeEngine_ECS_Serialization_Tests_MeterBefore;

namespace SomeEngine.ECS.Serialization.Tests;

// Change tracking and system progress are not kept when a save is loaded from scratch, so changing them in a
// later game version does not stop older saves from loading.
public sealed class SavedProgressChangeTests
{
    private const string Meter = "5e2d9a14-0b7c-4f3e-9d28-6c1a4b8f0100";

    [Fact]
    public void A_component_that_started_tracking_changes_after_saving_keeps_its_value()
    {
        using var source = new World();
        Entity entity = source.Create(new MeterBefore { Level = 4 });
        using var stream = new MemoryStream();
        new WorldSerializer(new SaveRegistry().RegisterDurable<MeterBefore, MeterBeforeSerializer>(Key(MeterBeforeSerializer.Schema)))
            .Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;

        using World loaded = WorldSerializer.ReadWorld(stream,
            new SaveRegistry().RegisterDurable<MeterAfter, MeterAfterSerializer>(Key(MeterAfterSerializer.Schema)));

        Assert.Equal(4, loaded.Read<MeterAfter>(entity).Level);
    }

    [Fact]
    public void A_save_loads_into_a_game_with_a_different_number_of_systems()
    {
        using var source = new World();
        source.Create(new MeterBefore { Level = 4 });
        var registry = new SaveRegistry().RegisterDurable<MeterBefore, MeterBeforeSerializer>(Key(MeterBeforeSerializer.Schema));
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(source, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave),
            ProgressMode.Reset, new Progress(1));
        stream.Position = 0;
        var systems = new TwoSystems(new Progress(2), new Progress(3));

        using World loaded = WorldSerializer.ReadWorld(stream, registry, default, systems);

        Assert.True(systems.First.Restored && systems.Second.Restored);
    }

    private static SavedTypeKey Key(SavedFieldList schema) => new(Guid.Parse(Meter), "Meter", schema.Hash);

    private sealed class Progress(int count) : ISystemVersions
    {
        public bool Restored { get; private set; }
        public int VersionCount => count;
        public void CopyVersions(Span<ulong> versions) => versions.Fill(1);
        public void RestoreVersions(ReadOnlySpan<ulong> versions) => Restored = true;
    }

    private readonly struct TwoSystems(Progress first, Progress second) : IWorldSystemBinding
    {
        public Progress First => first;
        public Progress Second => second;
        public ISystemVersions[] Create(World world) => [first, second];
    }
}

[SaveId("5e2d9a14-0b7c-4f3e-9d28-6c1a4b8f1100")]
public struct MeterBefore : IComponent
{
    public int Level;
}

[SaveId("5e2d9a14-0b7c-4f3e-9d28-6c1a4b8f1200")]
public struct MeterAfter : ITrackedComponent
{
    public int Level;
}

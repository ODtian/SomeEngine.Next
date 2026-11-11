using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.Generators.Tests;

[Collection("Checkpoint diagnostics")]
public sealed class CheckpointTests
{
    [Fact]
    public void DiagnosticsAllowOnceViewsToRebuildAfterCheckpointLoad()
    {
        RebuildDiagnostics.Enabled = true;
        try
        {
            var registry = new SerializationRegistry();
            ViewSerialization.Register(registry);
            CheckpointContainerFunctions.RegisterSerialization(registry);
            registry.Register<CheckpointValue>();
            CheckpointContainer.Number.Value = 7;
            using var frames = new HeadlessFrameDriver();
            Entity root = Views.Spawn(frames.World, Entity.Null, new CheckpointContainer());
            frames.Frame();
            using var checkpoint = new MemoryStream();
            WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
            checkpoint.Position = 0;
            using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));
            CheckpointContainer.Number.Value = 13;

            restored.Frame();

            Assert.Equal(13, restored.World.ReadComponentData<CheckpointValue>(root).Value);
            CheckpointContainer.Number.Value = 19;
            restored.Frame();
            Assert.Equal(13, restored.World.ReadComponentData<CheckpointValue>(root).Value);
        }
        finally
        {
            RebuildDiagnostics.Enabled = false;
        }
    }

    [Fact]
    public void ClosedGenericViewsKeepTheirParametersAcrossCheckpointLoad()
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        CaptionFunctions.RegisterSerialization<int, CaptionIntCodec>(registry, new SerializationTypeKey(
            new Guid("d85d1fb4-f983-46a1-a245-78be88d850af"), "CaptionInt", 1));
        CaptionFunctions.RegisterSerialization<long, CaptionLongCodec>(registry, new SerializationTypeKey(
            new Guid("db550fe6-0ea9-49bb-8edf-e5c3c5e1bcab"), "CaptionLong", 1));
        using var frames = new HeadlessFrameDriver();
        Entity first = Views.Spawn(frames.World, Entity.Null, new Caption<int>(9, 2));
        Entity second = Views.Spawn(frames.World, Entity.Null, new Caption<long>(19, 3));
        frames.Frame();
        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
        checkpoint.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));

        restored.Frame();

        Assert.Equal(new Caption<int>(9, 2), restored.World.ReadComponentData<Caption<int>>(first));
        Assert.Equal(new Caption<long>(19, 3), restored.World.ReadComponentData<Caption<long>>(second));
    }

    [Fact]
    public void ApplicationCodecRegistrationPreservesTheDeclaredSaveSchema()
    {
        var writerRegistry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(writerRegistry, CardFunctions.SerializationType);
        var card = new Card("Inventory");
        using var stream = new MemoryStream();
        WorldSerializer.WriteComponent(stream, in card, writerRegistry, new(Purpose: SavePurpose.DurableSave));
        var readerRegistry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(readerRegistry,
            CardFunctions.SerializationType with { SchemaFingerprint = 2 });
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => WorldSerializer.ReadComponent<Card>(stream, readerRegistry, new(RequiredPurpose: SavePurpose.DurableSave)));
    }

    [Fact]
    public void GeneratedFieldCodecRestoresReadonlyParametersThroughTheComponentApi()
    {
        var registry = new SerializationRegistry();
        CheckpointOptionsFunctions.RegisterSerialization(registry);
        var options = new CheckpointOptions(CheckpointColor.Blue,
            new Guid("2fbf0bb2-f4b4-4af5-85a2-c025ebde9047"), "背包");
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, in options, registry, new(Purpose: SavePurpose.DurableSave));
        stream.Position = 0;
        CheckpointOptions restored = WorldSerializer.ReadComponent<CheckpointOptions>(stream, registry, new(RequiredPurpose: SavePurpose.DurableSave));

        Assert.Equal(options, restored);
    }

    [Fact]
    public void DurableWorldReadRemapsViewEntityParametersAndRebuildsTheirOutput()
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        CheckpointRootFunctions.RegisterSerialization(registry);
        CheckpointLabelFunctions.RegisterSerialization(registry);
        registry.Register<CheckpointValue, CheckpointValueCodec>(new SerializationTypeKey(
            new Guid("a31f6356-7460-4bc4-9b82-d0d1f839284c"), "CheckpointValue", 1));
        using var frames = new HeadlessFrameDriver();
        Entity removed = frames.World.Create();
        Entity source = frames.World.Create(new CheckpointValue(7));
        Views.Spawn(frames.World, Entity.Null, new CheckpointRoot(source));
        frames.Frame();
        frames.World.Destroy(removed);
        using var save = new MemoryStream();
        new WorldSerializer(registry).Save(frames.World, save, SavePurpose.DurableSave);
        save.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldSerializer.ReadWorld(save, registry, new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap) with { RequiredPurpose = SavePurpose.DurableSave }));
        Entity root = Entity.Null;
        Query query = restored.World.Query(new QueryDescription().WithStored<CheckpointRoot>());
        using (QueryScope rows = query.OpenRead())
            foreach (QueryChunk chunk in rows)
                foreach (Entity entity in chunk.Entities) root = entity;
        Entity restoredSource = restored.World.ReadComponentData<CheckpointRoot>(root).Source;
        Assert.NotEqual(source, restoredSource);
        restored.World.Set(restoredSource, new CheckpointValue(29));

        restored.Frame();

        Entity child = Assert.Single(ElementChildren.Read(restored.World, root));
        Assert.Equal(new CheckpointLabel(29), restored.World.ReadComponentData<CheckpointLabel>(child));
    }

    [Fact]
    public void GeneratedRegistrationAcceptsAnApplicationCodecForViewParameters()
    {
        var registry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(registry);
        var card = new Card("Inventory");
        using var stream = new MemoryStream();

        WorldSerializer.WriteComponent(stream, in card, registry, new(Purpose: SavePurpose.RawCheckpoint));
        stream.Position = 0;
        Card restored = WorldSerializer.ReadComponent<Card>(stream, registry, new(RequiredPurpose: SavePurpose.RawCheckpoint));

        Assert.Equal("Inventory", restored.Title);
    }

    [Fact]
    public void CheckpointRestoresViewStructureAndRebuildsDependenciesInTheNextFrame()
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        CheckpointRootFunctions.RegisterSerialization(registry);
        CheckpointLabelFunctions.RegisterSerialization(registry);
        registry.Register<CheckpointValue>();
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new CheckpointValue(7));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CheckpointRoot(source));
        frames.Frame();
        Entity child = Assert.Single(ElementChildren.Read(frames.World, root));
        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
        checkpoint.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));

        restored.Frame();

        Assert.Equal(child, Assert.Single(ElementChildren.Read(restored.World, root)));
        Assert.Equal(new CheckpointRoot(source), restored.World.ReadComponentData<CheckpointRoot>(root));
        Assert.Equal(new CheckpointLabel(7), restored.World.ReadComponentData<CheckpointLabel>(child));
        restored.World.Set(source, new CheckpointValue(13));
        restored.Frame();
        Assert.Equal(child, Assert.Single(ElementChildren.Read(restored.World, root)));
        Assert.Equal(new CheckpointLabel(13), restored.World.ReadComponentData<CheckpointLabel>(child));
    }

    [Fact]
    public void NextFrameRebuildsOnceContainersAndObjectReadsAfterCheckpointLoad()
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        CheckpointContentFunctions.RegisterSerialization(registry);
        CheckpointContainerFunctions.RegisterSerialization(registry);
        CheckpointObjectTextFunctions.RegisterSerialization(registry);
        CheckpointLabelFunctions.RegisterSerialization(registry);
        registry.Register<CheckpointValue>();
        CheckpointContainer.Number.Value = 7;
        CheckpointObjectText.Number.Value = 11;
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new CheckpointContent());
        frames.Frame();
        Entity container = Assert.Single(ElementChildren.Read(frames.World, root));
        Entity text = Assert.Single(ElementChildren.Read(frames.World, container));
        Entity label = Assert.Single(ElementChildren.Read(frames.World, text));
        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
        checkpoint.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));
        CheckpointContainer.Number.Value = 17;
        CheckpointObjectText.Number.Value = 19;

        restored.Frame();

        Assert.Equal(container, Assert.Single(ElementChildren.Read(restored.World, root)));
        Assert.Equal(text, Assert.Single(ElementChildren.Read(restored.World, container)));
        Assert.Equal(label, Assert.Single(ElementChildren.Read(restored.World, text)));
        Assert.Equal(17, restored.World.ReadComponentData<CheckpointValue>(container).Value);
        Assert.Equal(new CheckpointLabel(19), restored.World.ReadComponentData<CheckpointLabel>(label));
        CheckpointObjectText.Number.Value = 23;
        restored.Frame();
        Assert.Equal(new CheckpointLabel(23), restored.World.ReadComponentData<CheckpointLabel>(label));
    }
}

[CollectionDefinition("Checkpoint diagnostics", DisableParallelization = true)]
public sealed class CheckpointDiagnosticsCollection;

public struct CheckpointCardCodec : IComponentCodec<Card>
{
    public void Write(ref DataWriter writer, in Card value) => writer.WriteString(value.Title);
    public void Read(ref DataReader reader, out Card value) => value = new Card(reader.ReadString()!);
}

public struct CheckpointValueCodec : IDurableComponentCodec<CheckpointValue>
{
    public void Write(ref DataWriter writer, in CheckpointValue value) => writer.WriteInt32(value.Value);
    public void Read(ref DataReader reader, out CheckpointValue value) => value = new CheckpointValue(reader.ReadInt32());
}

public struct CaptionIntCodec : IDurableComponentCodec<Caption<int>>
{
    public void Write(ref DataWriter writer, in Caption<int> value)
    {
        writer.WriteInt32(value.Value);
        writer.WriteInt32(value.Repeat);
    }
    public void Read(ref DataReader reader, out Caption<int> value) =>
        value = new Caption<int>(reader.ReadInt32(), reader.ReadInt32());
}

public struct CaptionLongCodec : IDurableComponentCodec<Caption<long>>
{
    public void Write(ref DataWriter writer, in Caption<long> value)
    {
        writer.WriteInt64(value.Value);
        writer.WriteInt32(value.Repeat);
    }
    public void Read(ref DataReader reader, out Caption<long> value) =>
        value = new Caption<long>(reader.ReadInt64(), reader.ReadInt32());
}

public enum CheckpointColor : byte { Red, Blue }

public readonly record struct CheckpointOptions(CheckpointColor Color, Guid Id, string Title) : IView, ISparseComponent
{
    public BuildTask Build() => default;
}

public readonly record struct CheckpointValue(int Value) : IComponent, ITrackedComponent;

public readonly record struct CheckpointRoot(Entity Source) : IView, IComponent
{
    public BuildTask Build()
    {
        Scope.View(new CheckpointLabel(Scope.Read<CheckpointValue>(Source).Value), 1);
        return default;
    }
}

public readonly record struct CheckpointLabel(int Value) : IView, ISparseComponent
{
    public BuildTask Build() => default;
}

public readonly record struct CheckpointContent : IView, IComponent
{
    public BuildTask Build()
    {
        Entity container = Scope.View(new CheckpointContainer(), 1);
        using (container.Content())
            Scope.View(new CheckpointObjectText(), 2);
        return default;
    }
}

public readonly record struct CheckpointContainer : IView, IComponent
{
    public static readonly State<int> Number = new(0);
    public static BuildMode Mode => BuildMode.Once;
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        var value = new CheckpointValue(Number.Value);
        if (Scope.World.Has<CheckpointValue>(Scope.Entity))
            Scope.World.Set(Scope.Entity, in value);
        else
            Scope.World.Add(Scope.Entity, in value);
        return default;
    }
}

public readonly record struct CheckpointObjectText : IView, ISparseComponent
{
    public static readonly State<int> Number = new(0);

    public BuildTask Build()
    {
        Scope.View(new CheckpointLabel(Number.Value), 3);
        return default;
    }
}

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.Generators.Tests;

public sealed class CheckpointTests
{
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

        Assert.Equal(new Caption<int>(9, 2), restored.World.ReadSparse<Caption<int>>(first));
        Assert.Equal(new Caption<long>(19, 3), restored.World.ReadSparse<Caption<long>>(second));
    }

    [Fact]
    public void ApplicationCodecRegistrationPreservesTheDeclaredSaveSchema()
    {
        var writerRegistry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(writerRegistry, CardFunctions.SerializationType);
        var card = new Card("Inventory");
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableComponent(stream, in card, writerRegistry);
        var readerRegistry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(readerRegistry,
            CardFunctions.SerializationType with { SchemaFingerprint = 2 });
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => WorldSerializer.ReadDurableComponent<Card>(stream, readerRegistry));
    }

    [Fact]
    public void GeneratedFieldCodecRestoresReadonlyParametersThroughTheComponentApi()
    {
        var registry = new SerializationRegistry();
        CheckpointOptionsFunctions.RegisterSerialization(registry);
        var options = new CheckpointOptions(CheckpointColor.Blue,
            new Guid("2fbf0bb2-f4b4-4af5-85a2-c025ebde9047"), "背包");
        using var stream = new MemoryStream();

        WorldSerializer.WriteDurableComponent(stream, in options, registry);
        stream.Position = 0;
        CheckpointOptions restored = WorldSerializer.ReadDurableComponent<CheckpointOptions>(stream, registry);

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
        Entity removed = frames.World.CreateEntity();
        frames.World.DestroyEntity(removed);
        Entity source = frames.World.CreateEntity(new CheckpointValue(7));
        Views.Spawn(frames.World, Entity.Null, new CheckpointRoot(source));
        frames.Frame();
        using var save = new MemoryStream();
        WorldSerializer.WriteDurableWorld(save, frames.World, registry);
        save.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldSerializer.ReadDurableWorld(save, registry,
            new WorldLoadOptions(IdentityMode: EntityIdentityMode.Remap)));
        Entity root = Entity.Null;
        var query = restored.World.Query(restored.World.QueryDefinition().Read<CheckpointRoot>());
        restored.World.ExecuteQuery(query, ref root, static (rows, ref found) =>
        {
            foreach (var row in rows.Rows)
                found = row.Entity;
        });
        restored.World.ReleaseQuery(query);
        Entity restoredSource = restored.World.Read<CheckpointRoot>(root).Source;
        Assert.NotEqual(source, restoredSource);
        restored.World.Replace(restoredSource, new CheckpointValue(29));

        restored.Frame();

        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, root));
        Assert.Equal(new CheckpointLabel(29), restored.World.ReadSparse<CheckpointLabel>(child));
    }

    [Fact]
    public void GeneratedRegistrationAcceptsAnApplicationCodecForViewParameters()
    {
        var registry = new SerializationRegistry();
        CardFunctions.RegisterSerialization<CheckpointCardCodec>(registry);
        var card = new Card("Inventory");
        using var stream = new MemoryStream();

        WorldSerializer.WriteCheckpointComponent(stream, in card, registry);
        stream.Position = 0;
        Card restored = WorldSerializer.ReadCheckpointComponent<Card>(stream, registry);

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
        Entity source = frames.World.CreateEntity(new CheckpointValue(7));
        Entity root = Views.Spawn(frames.World, Entity.Null, new CheckpointRoot(source));
        frames.Frame();
        Entity child = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
        checkpoint.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));

        restored.Frame();

        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, root)));
        Assert.Equal(new CheckpointRoot(source), restored.World.Read<CheckpointRoot>(root));
        Assert.Equal(new CheckpointLabel(7), restored.World.ReadSparse<CheckpointLabel>(child));
        restored.World.Replace(source, new CheckpointValue(13));
        restored.Frame();
        Assert.Equal(child, Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, root)));
        Assert.Equal(new CheckpointLabel(13), restored.World.ReadSparse<CheckpointLabel>(child));
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
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity text = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity label = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, text));
        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, frames.World, registry);
        checkpoint.Position = 0;
        using var restored = new HeadlessFrameDriver(WorldCheckpointCodec.Read(checkpoint, registry));
        CheckpointContainer.Number.Value = 17;
        CheckpointObjectText.Number.Value = 19;

        restored.Frame();

        Assert.Equal(container, Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, root)));
        Assert.Equal(text, Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, container)));
        Assert.Equal(label, Assert.Single(Hierarchy<ElementTree>.GetChildren(restored.World, text)));
        Assert.Equal(17, restored.World.Read<CheckpointValue>(container).Value);
        Assert.Equal(new CheckpointLabel(19), restored.World.ReadSparse<CheckpointLabel>(label));
        CheckpointObjectText.Number.Value = 23;
        restored.Frame();
        Assert.Equal(new CheckpointLabel(23), restored.World.ReadSparse<CheckpointLabel>(label));
    }
}

public struct CheckpointCardCodec : IComponentCodec<Card>
{
    public void Write(ref DataWriter writer, in Card value) => writer.WriteString(value.Title);
    public void Read(ref DataReader reader, out Card value) => value = new Card(reader.ReadString()!);
}

public struct CheckpointValueCodec : ICanonicalComponentCodec<CheckpointValue>
{
    public void Write(ref DataWriter writer, in CheckpointValue value) => writer.WriteInt32(value.Value);
    public void Read(ref DataReader reader, out CheckpointValue value) => value = new CheckpointValue(reader.ReadInt32());
}

public struct CaptionIntCodec : ICanonicalComponentCodec<Caption<int>>
{
    public void Write(ref DataWriter writer, in Caption<int> value)
    {
        writer.WriteInt32(value.Value);
        writer.WriteInt32(value.Repeat);
    }
    public void Read(ref DataReader reader, out Caption<int> value) =>
        value = new Caption<int>(reader.ReadInt32(), reader.ReadInt32());
}

public struct CaptionLongCodec : ICanonicalComponentCodec<Caption<long>>
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

public readonly record struct CheckpointValue(int Value) : IComponent;

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
            Scope.World.Replace(Scope.Entity, in value);
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

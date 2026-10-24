using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Views.Tests;
using SomeEngine.Views.AotReference;

namespace SomeEngine.Views.AotSmoke;

internal static class CheckViewCheckpoint
{
    internal static void Write(string path)
    {
        SerializationRegistry registry = CreateRegistry(reading: false);
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new CheckpointNumber(7));
        frames.World.Add(source, new SavedCount(7));
        Entity root = Views.Spawn(frames.World, Entity.Null, new SavedPanel(source));
        frames.Frame();
        using var stream = File.Create(path);
        WorldCheckpointCodec.Write(stream, frames.World, registry);
        Console.WriteLine($"Checkpoint written; panel ViewType.Id={frames.World.Read<ViewType>(root).Id}.");
    }

    internal static void Read(string path)
    {
        RebuildDiagnostics.Enabled = true;
        SerializationRegistry registry = CreateRegistry(reading: true);
        using var stream = File.OpenRead(path);
        using var frames = new HeadlessFrameDriver(WorldCheckpointCodec.Read(stream, registry));
        Entity root = Entity.Null;
        var query = frames.World.Query(frames.World.QueryDefinition().Read<SavedPanel>());
        frames.World.ExecuteQuery(query, ref root, static (rows, ref panel) =>
        {
            foreach (var row in rows.Rows)
                panel = row.Entity;
        });
        frames.World.ReleaseQuery(query);
        Entity source = frames.World.Read<SavedPanel>(root).Source;
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(children.Length == 3, "The checkpoint restores the three ordered children.");
        Entity[] descendants = Hierarchy<ElementTree>.GetChildren(frames.World, children[1]).ToArray();
        Require(descendants.Length == 1, "The caller container keeps its declared child.");
        frames.World.Replace(source, new CheckpointNumber(13));
        frames.World.Replace(source, new SavedCount(13));

        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).SequenceEqual(children),
            "The first frame reuses the restored node identities and child order.");
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, children[1]).SequenceEqual(descendants),
            "The first frame reuses the caller container's descendants.");
        Require(frames.World.ReadSparse<SavedNumber>(children[0]).Value == 13,
            "The remapped panel rebuilds from its restored source parameters.");
        Require(frames.World.Read<CheckpointNumber>(children[1]).Value == 13,
            "The Once caller container rebuilds in the first frame.");
        Entity composeValue = Hierarchy<ElementTree>.GetChildren(frames.World, children[2]).Single();
        Require(frames.World.ReadSparse<SavedValue>(composeValue).Value == 13,
            "The referenced Compose view restores its generated codec and rebuilds its output.");
        frames.World.Replace(source, new CheckpointNumber(19));
        frames.World.Replace(source, new SavedCount(19));
        frames.Frame();
        Require(frames.World.ReadSparse<SavedNumber>(children[0]).Value == 19,
            "The first frame reestablishes ECS dependencies for subsequent updates.");
        Require(frames.World.Read<CheckpointNumber>(children[1]).Value == 13,
            "The Once container retains its first rebuilt output on subsequent frames.");
        Require(frames.World.ReadSparse<SavedValue>(composeValue).Value == 19,
            "The referenced Compose view reestablishes its source dependency.");
        Console.WriteLine($"Checkpoint read, type remapping, next-frame rebuilding, and dependencies passed; panel ViewType.Id={frames.World.Read<ViewType>(root).Id}.");
    }

    private static SerializationRegistry CreateRegistry(bool reading)
    {
        var registry = new SerializationRegistry();
        ViewSerialization.Register(registry);
        if (reading)
        {
            SavedComposeViews.Hud_ViewFunctions.RegisterSerialization(registry);
            SavedValueFunctions.RegisterSerialization(registry);
            SavedContainerFunctions.RegisterSerialization(registry);
            SavedNumberFunctions.RegisterSerialization(registry);
            SavedPanelFunctions.RegisterSerialization(registry);
        }
        else
        {
            SavedPanelFunctions.RegisterSerialization(registry);
            SavedNumberFunctions.RegisterSerialization(registry);
            SavedContainerFunctions.RegisterSerialization(registry);
            SavedValueFunctions.RegisterSerialization(registry);
            SavedComposeViews.Hud_ViewFunctions.RegisterSerialization(registry);
        }
        registry.Register<CheckpointNumber, CheckpointNumberCodec>(new SerializationTypeKey(
            new Guid("13e7b0ed-8321-44d2-97ec-6ea4f78db7f2"), "CheckpointNumber", 1));
        registry.Register<SavedCount, SavedCountCodec>(new SerializationTypeKey(
            new Guid("b008e42d-c732-48b0-a4d8-e957ec51dfe9"), "SavedCount", 1));
        return registry;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

public readonly record struct CheckpointNumber(int Value) : IComponent;

public struct CheckpointNumberCodec : ICanonicalComponentCodec<CheckpointNumber>
{
    public void Write(ref DataWriter writer, in CheckpointNumber value) => writer.WriteInt32(value.Value);
    public void Read(ref DataReader reader, out CheckpointNumber value) => value = new CheckpointNumber(reader.ReadInt32());
}

public readonly record struct SavedPanel(Entity Source) : IView, IComponent
{
    public BuildTask Build()
    {
        int value = Scope.Read<CheckpointNumber>(Source).Value;
        Scope.View(new SavedNumber(value), 1);
        Entity container = Scope.View(new SavedContainer(Source), 2);
        using (container.Content())
            Scope.View(new SavedNumber(5), 3);
        SavedComposeViews.Hud(Source);
        return default;
    }
}

public readonly record struct SavedNumber(int Value) : IView, ISparseComponent
{
    public BuildTask Build() => default;
}

public readonly record struct SavedContainer(Entity Source) : IView, IComponent
{
    public static BuildMode Mode => BuildMode.Once;
    public static ChildSource Children => ChildSource.Caller;

    public BuildTask Build()
    {
        var value = new CheckpointNumber(Scope.Read<CheckpointNumber>(Source).Value);
        if (Scope.World.Has<CheckpointNumber>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, in value);
        else
            Scope.World.Add(Scope.Entity, in value);
        return default;
    }
}

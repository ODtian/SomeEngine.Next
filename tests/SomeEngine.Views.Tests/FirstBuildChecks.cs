using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Public view behavior shared by the test runner and the ECS NativeAOT executable.</summary>
internal static class FirstBuildChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        Entity root = Views.Spawn(frames.World, Entity.Null, new ReferenceGroup(new string('v', 5), ReadNumber));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        var children = Hierarchy<ElementTree>.GetChildren(frames.World, root);
        Require(children.Count == 3, "The root has three declared children.");
        Require(frames.World.Read<TableText>(children[0]).Value == "first", "The first child retains its table parameters.");
        Require(frames.World.ReadSparse<ReferenceGroup>(root).Name == "vvvvv", "The root retains its sparse string parameter.");
        Require(frames.World.ReadSparse<ReferenceGroup>(root).NumberCallback() == 41, "The root retains its callback parameter.");
        Require(frames.World.Read<TableText>(children[2]).Value == "last", "The last child retains its declaration position.");
        var grandchildren = Hierarchy<ElementTree>.GetChildren(frames.World, children[1]);
        Require(grandchildren.Count == 1, "The branch creates its child during the first frame.");
        Require(frames.World.Read<TableText>(grandchildren[0]).Value == "vvvvv:41", "The branch builds from its sparse parameters.");
        Require(frames.World.Read<ViewType>(children[0]).Id == frames.World.Read<ViewType>(children[2]).Id,
            "Equal view types share a type number.");
        Require(frames.World.Read<ViewType>(children[0]).Id != frames.World.Read<ViewType>(children[1]).Id,
            "Different view types have different type numbers.");

        VerifyParameterChanges(frames, root, children.ToArray(), grandchildren[0]);
        VerifyOnceParameters(frames);
    }

    private static int ReadNumber() => 41;
    private static int ReadChangedNumber() => 53;

    private static void VerifyParameterChanges(HeadlessFrameDriver frames, Entity root, Entity[] children, Entity leaf)
    {
        uint branchVersion = frames.World.SparseWriteVersion<SparseBranch>(children[1]);
        uint leafVersion = frames.World.WriteVersion<TableText>(leaf);
        frames.World.ReplaceSparse(root, new ReferenceGroup("vvvvv", ReadNumber));

        frames.Frame();

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray().AsSpan().SequenceEqual(children),
            "Equal child parameters retain the declared entities.");
        Require(frames.World.SparseWriteVersion<SparseBranch>(children[1]) == branchVersion,
            "Equal sparse parameters retain their write version.");
        Require(frames.World.WriteVersion<TableText>(leaf) == leafVersion,
            "Equal nested parameters retain their write version.");
        frames.World.ReplaceSparse(root, new ReferenceGroup("updated", ReadChangedNumber));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray().AsSpan().SequenceEqual(children),
            "Changed child parameters retain the declared entities.");
        Require(frames.World.ReadSparse<SparseBranch>(children[1]).NumberCallback() == 53,
            "Changed sparse parameters retain the latest callback.");
        Require(frames.World.Read<TableText>(leaf).Value == "updated:53",
            "Changed sparse parameters rebuild the nested table text within the frame.");
        leafVersion = frames.World.WriteVersion<TableText>(leaf);
        frames.Frame();
        Require(frames.World.WriteVersion<TableText>(leaf) == leafVersion,
            "A later frame retains the completed parameter update.");
    }

    private static void VerifyOnceParameters(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceText("first"));
        frames.Frame();
        Entity child = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        frames.World.Replace(root, new OnceText("updated"));

        frames.Frame();

        Require(frames.World.Read<OnceText>(root).Value == "updated", "Once stores the latest parameters.");
        Require(frames.World.Read<TableText>(child).Value == "first", "Once retains its first content.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct ReferenceGroup(string Name, Func<int> NumberCallback) : IView, ISparseComponent
    {
        public async BuildTask Build()
        {
            await Task.CompletedTask;
            await default(BuildTask);
            Scope.View(new TableText("first"));
            Scope.View(new SparseBranch(Name, NumberCallback));
            Scope.View(new TableText("last"));
        }
    }

    private readonly record struct SparseBranch(string Name, Func<int> NumberCallback) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            Scope.View(new TableText($"{Name}:{NumberCallback()}"));
            return default;
        }
    }

    private readonly record struct TableText(string Value) : IView, IComponent
    {
        public BuildTask Build() => default;
    }

    private readonly record struct OnceText(string Value) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Scope.View(new TableText(Value));
            return default;
        }
    }
}

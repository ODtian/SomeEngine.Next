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
    }

    private static int ReadNumber() => 41;

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
}

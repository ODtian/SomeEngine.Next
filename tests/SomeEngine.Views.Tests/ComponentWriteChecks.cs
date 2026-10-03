using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Component write behavior shared by the test runner and the NativeAOT executable.</summary>
internal static class ComponentWriteChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.CreateEntity(new Number(3));
        frames.World.AddSparse(source, new SparseText(new string('a', 3)));
        Entity target = frames.World.CreateEntity();
        Entity clicks = frames.World.CreateEntity(new Clicks(4));
        uint numberVersion = frames.World.WriteVersion<Number>(source);
        uint textVersion = frames.World.SparseWriteVersion<SparseText>(source);
        Entity root = Views.Spawn(frames.World, Entity.Null, new CounterGroup(source, target, clicks));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(frames.World.WriteVersion<Number>(source) == numberVersion,
            "Equal table copies preserve their write version through structural changes.");
        Require(frames.World.SparseWriteVersion<SparseText>(source) == textVersion,
            "Equal sparse copies compare their managed values and preserve their write version.");
        Require(frames.World.Has<ExtraData>(source), "Ensure supplies required default data.");
        Require(frames.World.Read<Number>(target).Value == 17, "Write adds the changed component value.");
        Require(frames.World.ReadSparse<Counter>(source).Value == 4, "ReadWrite adds and updates sparse data.");
        Require(frames.World.Read<Text>(root).Value == "Count:4", "The first frame displays the converged count.");
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity container = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Require(frames.World.Read<Number>(container).Value == 19, "Caller builds commit their own component copies.");
        frames.World.Replace(clicks, new Clicks(7));

        frames.Frame();

        Require(frames.World.Read<Text>(root).Value == "Count:7", "Later input displays its count within the same frame.");
        uint displayVersion = frames.World.WriteVersion<Text>(root);
        frames.Frame();
        Require(frames.World.WriteVersion<Text>(root) == displayVersion,
            "The completed display retains its write version on the following frame.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct CounterGroup(Entity Source, Entity Target, Entity Input) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref Number number = ref Scope.Write<Number>(Source);
            Scope.Ensure<ExtraData>(Source);
            number = new Number(3);
            Scope.Write<SparseText>(Source) = new SparseText(new string('a', 3));
            ref Number target = ref Scope.Write<Number>(Target);
            using (Scope.View(new Group()).Content())
            {
                using (Scope.View(new NumberContainer()).Content()) { }
                Scope.Write<Number>(Target) = new Number(17);
                ref Counter counter = ref Scope.ReadWrite<Counter>(Source);
                Scope.Write<Text>(Scope.Entity) = new Text($"Count:{counter.Value}");
                int clicks = Scope.Read<Clicks>(Input).Value;
                if (counter.Value < clicks)
                    counter = new Counter(clicks);
            }
            Require(target.Value == 17, "Content shares the calling calculation's component copy.");
            return default;
        }
    }

    private readonly record struct NumberContainer : IView, IComponent
    {
        public static ChildSource Children => ChildSource.Caller;

        public BuildTask Build()
        {
            Scope.Write<Number>(Scope.Entity) = new Number(19);
            return default;
        }
    }

    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct SparseText(string Value) : ISparseComponent;
    private readonly record struct Counter(int Value) : ISparseComponent;
    private readonly record struct Clicks(int Value) : IComponent;
    private readonly record struct Text(string Value) : IComponent;
    private readonly record struct ExtraData : IComponent;
}

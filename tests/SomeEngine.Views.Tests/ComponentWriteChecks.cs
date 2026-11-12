using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Component write behavior shared by the test runner and the NativeAOT executable.</summary>
internal static partial class ComponentWriteChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        Entity source = frames.World.Create(new Number(3));
        frames.World.SetSparse(source, new SparseText(new string('a', 3)));
        Entity target = frames.World.Create();
        Entity clicks = frames.World.Create(new Clicks(4));
        ulong numberVersion = frames.World.ComponentDataWriteVersion<Number>(source);
        ulong textVersion = frames.World.ComponentDataWriteVersion<SparseText>(source);
        Entity root = Views.Spawn(frames.World, Entity.Null, new CounterGroup(source, target, clicks));

        frames.Frame();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        Require(frames.World.ComponentDataWriteVersion<Number>(source) == numberVersion,
            "Equal table copies preserve their write version through structural changes.");
        Require(frames.World.ComponentDataWriteVersion<SparseText>(source) == textVersion,
            "Equal sparse copies compare their managed values and preserve their write version.");
        Require(frames.World.Has<ExtraData>(source), "Ensure supplies required default data.");
        Require(frames.World.ReadComponentData<Number>(target).Value == 17, "Write adds the changed component value.");
        Require(frames.World.ReadComponentData<Counter>(source).Value == 4, "ReadWrite adds and updates sparse data.");
        Require(frames.World.ReadComponentData<Text>(root).Value == "Count:4", "The first frame displays the converged count.");
        Entity group = ElementChildren.Read(frames.World, root)[0];
        Entity container = ElementChildren.Read(frames.World, group)[0];
        Require(frames.World.ReadComponentData<Number>(container).Value == 19, "Caller builds commit their own component copies.");
        frames.World.Set(clicks, new Clicks(7));

        frames.Frame();

        Require(frames.World.ReadComponentData<Text>(root).Value == "Count:7", "Later input displays its count within the same frame.");
        ulong displayVersion = frames.World.ComponentDataWriteVersion<Text>(root);
        frames.Frame();
        Require(frames.World.ComponentDataWriteVersion<Text>(root) == displayVersion,
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

    private readonly record struct Number(int Value) : ITrackedComponent;
    private readonly record struct SparseText(string Value) : ISparseComponent, ITrackedComponent;
    private readonly record struct Counter(int Value) : ISparseComponent, ITrackedComponent;
    private readonly record struct Clicks(int Value) : ITrackedComponent;
    private readonly record struct Text(string Value) : ITrackedComponent;
    private readonly record struct ExtraData : IComponent;
}

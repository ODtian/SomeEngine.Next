using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

/// <summary>Public calculation behavior shared by the managed and NativeAOT consumers.</summary>
internal static partial class ComputationChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        var number = new State<int>(3);
        Entity target = frames.World.CreateEntity(new Number());
        Entity root = Views.Spawn(frames.World, Entity.Null, new NumberDisplay(number, target));
        frames.Frame();
        MemoResults memos = frames.World.ReadSparse<MemoResults>(root);
        uint signVersion = memos.Positive.Version;
        frames.World.Replace(root, new Text("edited"));
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        number.Value = 7;
        Require(memos.Doubled.Value == 14, "Reading the Memo presents a state written in the same frame.");
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 7, "The binding presents the changed state.");
        Require(frames.World.Read<Text>(root).Value == "edited", "An equal Memo result preserves the edited display.");
        Require(memos.Positive.Version == signVersion, "An equal Memo result preserves its public version.");
        Require(!frames.World.HasSparse<BuildError>(root), "The bindings preserve the completed view build.");

        number.Value = -1;
        frames.Frame();
        Require(frames.World.Read<Text>(root).Value == "negative", "A changed Memo result updates its display.");

        Hierarchy<ElementTree>.DestroySubtree(frames.World, root);
        frames.World.Replace(target, new Number(41));
        number.Value = 11;
        frames.Frame();
        Require(frames.World.Read<Number>(target).Value == 41, "The released view leaves its former target available.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct NumberDisplay(State<int> Number, Entity Target) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            State<int> number = Number;
            Entity target = Target;
            Memo<int> doubled = Scope.Memo(() => number.Value * 2);
            Memo<bool> positive = Scope.Memo(() => number.Value > 0);
            Scope.World.AddSparse(Scope.Entity, new MemoResults(doubled, positive));
            Scope.World.Add(Scope.Entity, new Text("built"));
            Scope.Compute(() => Scope.World.Replace(target, new Number(number.Value)));
            Scope.Compute(() => Scope.World.Replace(Scope.Entity,
                new Text(positive.Value ? "positive" : "negative")));
            return default;
        }
    }

    private readonly record struct MemoResults(Memo<int> Doubled, Memo<bool> Positive) : ISparseComponent;
    private readonly record struct Number(int Value) : IComponent;
    private readonly record struct Text(string Value) : IComponent;
}

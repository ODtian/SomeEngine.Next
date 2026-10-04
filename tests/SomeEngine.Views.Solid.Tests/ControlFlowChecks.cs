using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using static SomeEngine.Views.Solid.Solid;
using SolidFrames = SomeEngine.Views.Tests.HeadlessFrameDriver;

namespace SomeEngine.Views.Solid.Tests;

/// <summary>Public control-flow behavior shared by managed and NativeAOT consumers.</summary>
public static partial class ControlFlowChecks
{
    public static void Run()
    {
        ShowBranches();
        ValueBranches(false);
        ValueBranches(true);
        NullableValueBranches();
        SwitchBranches();
        DynamicComponents();
    }

    private static void ShowBranches()
    {
        using var frames = new SolidFrames();
        var count = new State<int>(1);
        var sample = new State<string>("first");
        Entity output = frames.World.CreateEntity(new Display("empty"));
        Entity label = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => count.Value > 0, () =>
        {
            label = Text(sample.Value);
            Effect(() => Scope.Write<Display>(output).Value = sample.Value);
        }, () => fallback = Text("fallback"))));
        frames.Frame();
        Entity first = label;

        sample.Value = "second";
        count.Value = 2;
        frames.Frame();
        Require(label == first && frames.World.Read<Elements.Text>(label).Value == "first",
            "Show preserves Once content when its reads or its true condition's source change.");
        Require(frames.World.Read<Display>(output).Value == "second", "Effects in active content follow their own reads.");

        count.Value = 0;
        sample.Value = "removed";
        frames.Frame();
        Require(!frames.World.IsAlive(first) && frames.World.Read<Elements.Text>(fallback).Value == "fallback",
            "Show releases its previous content and creates the fallback.");
        Require(frames.World.Read<Display>(output).Value == "second", "Released content stops writing external data.");
        Entity empty = fallback;

        count.Value = 1;
        frames.Frame();
        Require(!frames.World.IsAlive(empty) && label != first &&
            frames.World.Read<Elements.Text>(label).Value == "removed", "Returning to Show content creates new nodes.");
    }

    private static void ValueBranches(bool keyed)
    {
        using var frames = new SolidFrames();
        var name = new State<string?>("first");
        Entity sampled = Entity.Null;
        Entity bound = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => name.Value, value =>
        {
            sampled = Text(value());
            bound = Text(value);
        }, () => fallback = Text("empty"), keyed)));
        frames.Frame();
        Entity firstSample = sampled;
        Entity firstBinding = bound;

        name.Value = "second";
        frames.Frame();
        Require((sampled != firstSample) == keyed && frames.World.IsAlive(firstSample) == !keyed,
            "The keyed option determines whether changed present values create new content.");
        Require(frames.World.Read<Elements.Text>(sampled).Value == (keyed ? "second" : "first") &&
            frames.World.Read<Elements.Text>(bound).Value == "second", "Value getters update bindings on the selected branch.");
        if (!keyed)
            Require(bound == firstBinding, "Unkeyed bindings keep their node when their value changes.");
        sampled.SetText(frames.World, "edited");
        Entity secondSample = sampled;
        name.Value = "second";
        frames.Frame();
        Require(sampled == secondSample && frames.World.Read<Elements.Text>(sampled).Value == "edited",
            "Equal condition values preserve edited branch content.");

        Entity secondBinding = bound;
        name.Value = null;
        frames.Frame();
        Require(!frames.World.IsAlive(secondSample) && !frames.World.IsAlive(secondBinding) &&
            frames.World.Read<Elements.Text>(fallback).Value == "empty", "Null values select the fallback and release bindings.");
        Entity empty = fallback;

        name.Value = "third";
        frames.Frame();
        Require(!frames.World.IsAlive(empty) && sampled != firstSample &&
            frames.World.Read<Elements.Text>(sampled).Value == "third", "Present values create content after the fallback.");
    }

    private static void NullableValueBranches()
    {
        using var frames = new SolidFrames();
        var number = new State<int?>();
        Entity label = Entity.Null;
        Entity fallback = Entity.Null;
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Show(() => number.Value,
            value => label = Text(() => value().GetValueOrDefault()),
            () => fallback = Text("empty"), keyed: true)));
        frames.Frame();
        Entity empty = fallback;
        Require(frames.World.Read<Elements.Text>(empty).Value == "empty", "An absent nullable number selects the fallback.");

        number.Value = 0;
        frames.Frame();
        Require(!frames.World.IsAlive(empty) && frames.World.Read<Elements.Text>(label).Value == "0",
            "Zero is a present nullable number.");
        Entity zero = label;
        number.Value = 1;
        frames.Frame();
        Require(!frames.World.IsAlive(zero) && label != zero && frames.World.Read<Elements.Text>(label).Value == "1",
            "Changed keyed nullable numbers create new nodes.");
    }

    private static void SwitchBranches()
    {
        using var frames = new SolidFrames();
        var first = new State<int>(1);
        var second = new State<bool>(true);
        var sample = new State<string>("first");
        Entity label = Entity.Null;
        Action content = () => label = Text(sample.Value);
        Views.Spawn(frames.World, Entity.Null, new OnceView(() => Switch(
            Match(() => first.Value > 0, content), Match(() => second.Value, content))));
        frames.Frame();
        Entity firstLabel = label;
        label.SetText(frames.World, "edited");

        sample.Value = "second";
        first.Value = 2;
        second.Value = false;
        frames.Frame();
        Require(label == firstLabel && frames.World.Read<Elements.Text>(label).Value == "edited",
            "Switch preserves the first matching branch and its Once content.");

        first.Value = 0;
        second.Value = true;
        frames.Frame();
        Require(!frames.World.IsAlive(firstLabel) && label != firstLabel &&
            frames.World.Read<Elements.Text>(label).Value == "second",
            "Switch distinguishes selected cases that share a content function.");
        Entity secondLabel = label;
        second.Value = false;
        frames.Frame();
        Require(!frames.World.IsAlive(secondLabel), "Switch releases content when all conditions become false.");

        first.Value = 1;
        frames.Frame();
        Require(label != firstLabel && label != secondLabel && frames.World.Read<Elements.Text>(label).Value == "second",
            "Returning to a Switch case creates its content again.");
    }

    private static void DynamicComponents()
    {
        using var frames = new SolidFrames();
        var selection = new State<int>();
        Entity label = Entity.Null;
        Action first = () => label = Text("first");
        Action second = () => label = Text("second");
        Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
            Dynamic(() => selection.Value % 2 == 0 ? first : second)));
        frames.Frame();
        Entity firstLabel = label;
        label.SetText(frames.World, "edited");
        selection.Value = 2;
        frames.Frame();
        Require(label == firstLabel && frames.World.Read<Elements.Text>(label).Value == "edited",
            "Dynamic preserves the current component when its getter selects the same function.");

        selection.Value = 1;
        frames.Frame();
        Require(!frames.World.IsAlive(firstLabel) && label != firstLabel &&
            frames.World.Read<Elements.Text>(label).Value == "second", "Dynamic creates content for a different function.");
        Entity secondLabel = label;
        selection.Value = 0;
        frames.Frame();
        Require(!frames.World.IsAlive(secondLabel) && label != firstLabel &&
            frames.World.Read<Elements.Text>(label).Value == "first", "Returning to a component function creates new nodes.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly record struct OnceView(Action Content) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;

        public BuildTask Build()
        {
            Content();
            return default;
        }
    }

    private record struct Display(string Value) : IComponent;
}

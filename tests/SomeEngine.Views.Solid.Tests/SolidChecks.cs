using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements;
using SomeEngine.Views.Tests;
using static SomeEngine.Views.Solid.Solid;

namespace SomeEngine.Views.Solid.Tests;

/// <summary>Solid behavior shared by managed and NativeAOT consumers.</summary>
public static partial class SolidChecks
{
    public static void Run()
    {
        using var frames = new HeadlessFrameDriver();
        var count = new State<int>(3);
        var sample = new State<int>(7);
        Func<int> readSample = () => sample.Value;
        Func<bool> positive = null!;
        Entity number = default;
        Entity sign = default;
        var red = new Color(1, 0, 0);
        var green = new Color(0, 1, 0);
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceView(() =>
        {
            Scope.Write<Display>(Scope.Entity).Value = "created";
            positive = Memo(() => count.Value >= 0);
            number = Text(() => count.Value / 10000, "D4")
                .Corner(4).Background(() => count.Value % 2 == 0 ? red : green);
            sign = Text(() => positive() ? "positive" : "negative");
            Effect(() => Scope.Write<Total>(Scope.Entity).Value = count.Value + Untrack(readSample));
        }));
        frames.Frame();
        sign.SetText(frames.World, "edited");
        frames.World.Replace(root, new Display("edited component"));
        uint signVersion = frames.World.WriteVersion<Elements.Text>(sign);

        count.Value = 5;
        frames.Frame();
        Require(frames.World.Read<Elements.Text>(sign).Value == "edited" &&
            frames.World.WriteVersion<Elements.Text>(sign) == signVersion,
            "Equal Memo results preserve the caller's edited text and its version.");
        sample.Value = 20;
        frames.Frame();
        Require(frames.World.Read<Total>(root).Value == 12,
            "Untracked state is sampled when the Effect's tracked state changes.");

        for (int index = 0; index < 1000; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        uint numberVersion = frames.World.WriteVersion<Elements.Text>(number);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 4096; index++)
        {
            count.Value = index;
            frames.Frame();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
#if !DEBUG
        Require(allocated == 0, "Changing Solid states with equal numeric text allocate zero bytes after warmup.");
#endif
        Require(frames.World.Read<Elements.Text>(number).Value == "0000" &&
            frames.World.WriteVersion<Elements.Text>(number) == numberVersion,
            "Equal formatted numbers preserve their text version.");
        Require(frames.World.Read<Surface>(number).Fill == green && frames.World.Read<Surface>(number).CornerRadius == 4,
            "Reactive background updates preserve other surface fields.");
        Require(frames.World.Read<Display>(root).Value == "edited component" &&
            frames.World.Read<Elements.Text>(sign).Value == "edited",
            "State changes preserve component content and equal Memo displays.");
        Require(frames.World.Read<Total>(root).Value == 4115, "Effects write the latest tracked and sampled values.");

        count.Value = -1;
        Require(!positive(), "Memo getters observe a state written before the next frame.");
        frames.Frame();
        Require(frames.World.Read<Elements.Text>(sign).Value == "negative", "Changed Memo results update downstream text.");
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
    private record struct Total(int Value) : IComponent;
}

using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Layout;
using SomeEngine.Typography;
using SomeEngine.Views;
using SomeEngine.Views.Compose;
using SomeEngine.Views.Solid;
using SomeEngine.Views.SwiftUI;
using static SomeEngine.Elements.ButtonFunctions;
using static SomeEngine.Elements.FocusFunctions;
using static SomeEngine.Elements.TextFunctions;
using static SomeEngine.Layout.FlexFunctions;
using static SomeEngine.Views.Compose.Compose;
using static SomeEngine.Views.Views;
using Size = SomeEngine.Layout.Size;

namespace SomeEngine.Elements.Consumer.Tests;

/// <summary>Counter interfaces exercised by managed tests and the NativeAOT executable.</summary>
internal static partial class InterfaceChecks
{
    internal static void CheckStruct(bool jobs) => CheckCounter(new StructCounter(), "struct", jobs);
    internal static void CheckCompose(bool jobs) => CheckCounter(new ComposeCounter_View(), "Compose", jobs);
    internal static void CheckSwiftUI(bool jobs) => CheckCounter(new SwiftUICounter(), "SwiftUI", jobs);
    internal static void CheckSolid(bool jobs) => CheckCounter(new SolidCounter_View(), "Solid", jobs);

    private static void CheckCounter<T>(in T view, string frontend, bool jobs)
        where T : struct, IView, IEquatable<T>
    {
        using var frames = new CounterFrames(jobs);
        World world = frames.World;
        Entity root = Spawn(world, frames.Canvas, view);
        frames.Frame();
        CheckDisplay(world, root, "Clicks: 0");
        Entity button = ButtonChecks.DescendantsWith<Pressable>(world, root).Single();
        Entity label = FindText(world, root, "Add one");
        var labelRect = world.Read<LayoutRect>(label).Value;
        var position = new Vector2(labelRect.X + labelRect.Width / 2, labelRect.Y + labelRect.Height / 2);

        Pointer.Move(world, frames.Canvas, position);
        Pointer.Press(world, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(world.Read<Hover>(button).Pressed, "The pointer holds the button pressed.");
        CheckDisplay(world, root, "Clicks: 0");

        Pointer.Release(world, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Require(world.Read<Clicks>(button).Count == 1, "The pointer release completes one click.");
        CheckDisplay(world, root, "Clicks: 1");

        Keyboard.Key(world, Key.Down, true);
        Keyboard.Key(world, Key.Down, false);
        Keyboard.Key(world, Key.Enter, true);
        Keyboard.Key(world, Key.Enter, false);
        frames.Frame();
        Require(world.HasSparse<Focused>(button), "Keyboard navigation focuses the button.");
        CheckDisplay(world, root, "Clicks: 2");

        Gamepad.Confirm(world);
        frames.Frame();
        Require(world.Read<Clicks>(button).Count == 3, "Pointer, keyboard and gamepad each complete a click.");
        CheckDisplay(world, root, "Clicks: 3");

        frames.Frame();
        world.Replace(frames.Canvas, new Canvas { Size = new Size(480, 320) });
        frames.Frame();
        CheckDisplay(world, root, "Clicks: 3");
        Console.WriteLine($"Interface counter passed: {frontend}, {(jobs ? "Job World" : "World")}.");
    }

    private static Entity FindText(World world, Entity root, string value) =>
        ButtonChecks.DescendantsWith<TextContent>(world, root)
            .Single(node => world.Read<TextContent>(node).Value == value);

    private static void CheckDisplay(World world, Entity root, string value)
    {
        Entity label = FindText(world, root, value);
        Require(world.Read<Text>(label).Value == value, "The text parameter matches its displayed content.");
        var rect = world.Read<LayoutRect>(label).Value;
        Require(rect.Width > 0 && rect.Height > 0, "The displayed count has a measured layout rectangle.");
    }

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct StructCounter : IView, IComponent
    {
        public BuildTask Build()
        {
            ref CounterValue count = ref Scope.ReadWrite<CounterValue>(Scope.Entity);
            using (Column().Content())
            {
                Entity button = Focusable(Button("Add one"));
                if (button.Clicked())
                    count.Value++;
                Text($"Clicks: {count.Value}");
            }
            return default;
        }
    }

    private record struct CounterValue(int Value) : IComponent;

    [Composable]
    private static void ComposeCounter()
    {
        State<int> count = Remember(static () => MutableStateOf(0));
        using (Column().Content())
        {
            Entity button = Focusable(Button("Add one"));
            if (button.Clicked())
                count.Value++;
            Text($"Clicks: {count.Value}");
        }
    }

    private readonly partial record struct SwiftUICounter : IView
    {
        [State] private partial int Count { get; set; }

        public BuildTask Build()
        {
            using (Column().Content())
            {
                Scope.View(new CounterButton(CountBinding));
                Text($"Clicks: {Count}");
            }
            return default;
        }
    }

    private readonly partial record struct CounterButton(Binding<int> Count) : IView
    {
        public BuildTask Build()
        {
            Entity button = Focusable(Button("Add one"));
            Binding<int> count = Count;
            if (button.Clicked())
                count.Value++;
            return default;
        }
    }

    [Component]
    private static void SolidCounter()
    {
        var count = new State<int>(0);
        using (Column().Content())
        {
            Entity button = Focusable(Button("Add one"));
            Solid.Effect(() =>
            {
                if (button.Clicked())
                    count.Value++;
            });
            Solid.Text(() => $"Clicks: {count.Value}");
        }
    }

    private sealed class CounterFrames : IDisposable
    {
        private readonly Font _font;
        private readonly SystemGroup<ImmediateSystemContext> _systems;
        internal World World { get; }
        internal Entity Canvas { get; }

        internal CounterFrames(bool jobs)
        {
            World = new World(execution: jobs ? new WorldJobs(JobSystem.Default) : null);
            _font = Font.Load(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fonts", "Latin.ttf")));
            Canvas = World.CreateEntity(new Canvas { Size = new Size(320, 240) });
            World.Add(Canvas, new TextTheme(new TextStyle { Font = _font, Size = 20 }));
            _systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(World));
            var shaping = new TextShapingSystem();
            _systems.Add(new HitTestSystem());
            _systems.Add(new FocusNavigationSystem());
            _systems.Add(new ViewRebuildSystem());
            _systems.Add(shaping);
            _systems.Add(new LayoutSystem(shaping));
        }

        internal void Frame() => _systems.Update();

        public void Dispose()
        {
            _systems.Dispose();
            World.Dispose();
            _font.Dispose();
        }
    }
}

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views.SwiftUI;
using SomeEngine.Views.Tests;

namespace SomeEngine.Views.AotSmoke;

internal static partial class SwiftUIChecks
{
    internal static void Run(HeadlessFrameDriver frames)
    {
        Entity root = Views.Spawn(frames.World, Entity.Null, new Panel("first", 3, false));
        frames.Frame();
        Entity counter = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Entity editor = Hierarchy<ElementTree>.GetChildren(frames.World, counter).Single();
        Require(frames.World.Read<DisplayText>(counter).Value == "first:3:1", "SwiftUI properties use their initial values.");
        Require(frames.World.Read<DisplayText>(editor).Value == "3", "The child Binding reads the property's initial value.");

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        frames.World.ReplaceSparse(editor, new Edit<int>(true, 7));
        frames.Frame();
        Require(frames.World.Read<DisplayText>(counter).Value == "first:7:1", "The child Binding refreshes its parent's display.");
        frames.World.Replace(root, new Panel("second", 99, false));
        frames.Frame();
        Require(frames.World.Read<DisplayText>(counter).Value == "second:7:1", "New parent parameters preserve both state properties.");

        frames.World.Replace(root, new Panel("other", 99, true));
        frames.Frame();
        Entity other = Hierarchy<ElementTree>.GetChildren(frames.World, root).Single();
        Require(frames.World.Read<DisplayText>(other).Value == "other:99:1", "The other branch creates its own state.");

        var state = new State<int>(11);
        var binding = new Binding<int>(state);
        binding.Value = 13;
        Require(state.Value == 13, "Bindings can share an independent State.");
    }

    private readonly record struct Panel(string Label, int Initial, bool Alternate) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (Alternate)
                Scope.View(new Counter<int>(Label, Initial));
            else
                Scope.View(new Counter<int>(Label, Initial));
            return default;
        }
    }

    private readonly partial record struct Counter<T>(string Label, T Initial) : IView where T : notnull
    {
        [State] private partial T Value { get; set; } = Initial;
        [State] private partial int Count { get; set; } = 1;

        public BuildTask Build()
        {
            Scope.View(new Editor<T>(ValueBinding));
            Scope.Write<DisplayText>(Scope.Entity) = new DisplayText($"{Label}:{Value}:{Count}");
            return default;
        }
    }

    private readonly record struct Editor<T>(Binding<T> Value) : IView, ISparseComponent where T : notnull
    {
        public BuildTask Build()
        {
            Binding<T> value = Value;
            ref Edit<T> edit = ref Scope.ReadWrite<Edit<T>>(Scope.Entity);
            if (edit.Set)
            {
                value.Value = edit.Value;
                edit = default;
            }
            Scope.Write<DisplayText>(Scope.Entity) = new DisplayText(value.Value.ToString()!);
            return default;
        }
    }

    private readonly record struct Edit<T>(bool Set, T Value) : ISparseComponent;
    private readonly record struct DisplayText(string Value) : IComponent;

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }
}

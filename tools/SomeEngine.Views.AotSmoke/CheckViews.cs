global using static SomeEngine.Views.AotSmoke.CardFunctions;

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;
using SomeEngine.Views.AotSmoke;
using SomeEngine.Views.Tests;

if (args is ["write-checkpoint", string writePath])
{
    CheckViewCheckpoint.Write(writePath);
    return;
}
if (args is ["read-checkpoint", string readPath])
{
    CheckViewCheckpoint.Read(readPath);
    return;
}

using var frames = new HeadlessFrameDriver();
Entity root = Views.Spawn(frames.World, Entity.Null, new Cards(false));
frames.Frame();
Entity[] first = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
Require(first.Length == 3, "The first frame declares three cards.");
Require(frames.World.ReadSparse<Card>(first[0]).Title == "same", "The named function stores the card parameters.");
Require(frames.World.Read<ElementKey>(first[1]).Key != frames.World.Read<ElementKey>(first[2]).Key,
    "Columns distinguish the two calls on one line.");
frames.World.Replace(first[0], new Selection(9));
frames.World.Replace(first[2], new Selection(7));
frames.World.ReplaceSparse(root, new Cards(true));
frames.Frame();
Entity[] second = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
Require(first[0] != second[0], "Switching branches gives each call its own entity.");
Require(frames.World.Read<Selection>(second[0]).Value == 0, "The second branch starts with its own state.");
Require(first[2] == second[2] && frames.World.Read<Selection>(second[2]).Value == 7,
    "The call on the same column preserves its local state.");
LayoutChecks.Run();
FlexChecks.Run();
StackGridChecks.Run();
ComposeChecks.Run(frames);
SwiftUIChecks.Run(frames);
RememberChecks.Run(frames);
SomeEngine.Elements.Consumer.Tests.SurfaceChecks.Run(false);
SomeEngine.Elements.Consumer.Tests.SurfaceChecks.Run(true);
SomeEngine.Elements.Consumer.Tests.TextChecks.Run();
SomeEngine.Elements.Consumer.Tests.LocalizationChecks.Run();
Console.WriteLine("Localized keys, arguments, language changes, English and Chinese plurals, number and date cultures, and ordinary text versions passed under NativeAOT.");
SomeEngine.Views.Solid.Tests.SolidChecks.Run();
Console.WriteLine("Solid text bindings, Memo getters, Effect, Untrack, reactive surfaces, and zero-allocation state changes passed under NativeAOT.");
Console.WriteLine("Text shaping, Column measurements, style modifiers, formatted numbers, write versions, and zero-allocation integer writes passed under NativeAOT.");
Console.WriteLine("Column surface values, layout sizes, write versions, and public user modifiers passed under NativeAOT.");
SomeEngine.Elements.Consumer.Tests.PointerChecks.Run(false);
SomeEngine.Elements.Consumer.Tests.PointerChecks.Run(true);
SomeEngine.Elements.Consumer.Tests.PointerChecks.RunRouting(false);
SomeEngine.Elements.Consumer.Tests.PointerChecks.RunRouting(true);
Console.WriteLine("Canvas pointer input, click bubbling, StopsPointer, subtree hover, held presses, capture, and public user behaviors passed under NativeAOT.");
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunGrid(false);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunGrid(true);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunRelease(false, false);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunRelease(false, true);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunRelease(true, false);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunRelease(true, true);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunCustomNavigation();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunCompositions();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunFocusabilityRemoval();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunInputOrder();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunMovingLayout();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunCanvasNavigation();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunBranchRelease(false);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunBranchRelease(true);
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunReparentedRelease();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunWorlds();
SomeEngine.Elements.Consumer.Tests.FocusChecks.RunMixedInputs();
Console.WriteLine("Keyboard and gamepad focus, confirmation, capture, ancestor transfer, layout geometry, public user behaviors, and scheduled navigation passed under NativeAOT.");

SomeEngine.Elements.Consumer.Tests.BehaviorChecks.Run(false);
SomeEngine.Elements.Consumer.Tests.BehaviorChecks.Run(true);
Console.WriteLine("Drag, scroll, selection and external behavior declarations passed.");
SomeEngine.Elements.Consumer.Tests.ButtonChecks.Run(SomeEngine.Elements.Consumer.Tests.ButtonKind.BuiltIn);
SomeEngine.Elements.Consumer.Tests.ButtonChecks.Run(SomeEngine.Elements.Consumer.Tests.ButtonKind.Rewritten);
SomeEngine.Elements.Consumer.Tests.ButtonChecks.Run(SomeEngine.Elements.Consumer.Tests.ButtonKind.Icon);
Console.WriteLine("Button themes, held presses, click consumption, label updates, and public user Button and IconButton passed under NativeAOT.");
ComposeGroupChecks.Run(frames);
Console.WriteLine("View functions, Compose content and inline groups, Remember, SwiftUI state and bindings, parameter comparisons, identities, sparse storage, scaled canvases, Flex, Stack, Grid, and user layouts passed under NativeAOT.");

static void Require(bool value, string message)
{
    if (!value)
        throw new InvalidOperationException(message);
}

namespace SomeEngine.Views.AotSmoke
{
    public readonly partial record struct Card(string Title) : IView
    {
        public async BuildTask Build()
        {
            if (!Scope.World.Has<Selection>(Scope.Entity))
            {
                Scope.World.Add(Scope.Entity, new Selection(0));
                Scope.World.AddTag<LocalState>(Scope.Entity);
            }
        }
    }

    public readonly record struct Cards(bool Alternate) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            if (Alternate)
                Scope.View(new Card("same"));
            else
                Card("same");
            Card("left"); Card("right");
            return default;
        }
    }

    public readonly record struct Selection(int Value) : IComponent;
}

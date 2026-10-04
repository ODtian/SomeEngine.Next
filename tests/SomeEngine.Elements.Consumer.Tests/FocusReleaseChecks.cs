using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;
using SomeEngine.Layout;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class FocusChecks
{
    internal static void RunRelease(bool direct, bool focusableRoot)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusFamily(true, true, focusableRoot));
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, parent)[0];
        Entity leaf = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(leaf), "The top-left child receives the first focus.");
        Require(frames.World.Read<FamilyDisplay>(root).Index == 2, "The caller displays the child's focus.");

        if (direct)
            frames.World.DestroyEntity(leaf);
        frames.World.Replace(root, new FocusFamily(true, false, focusableRoot));
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(parent), "The nearest focusable ancestor receives focus.");
        Require(frames.World.Read<FamilyDisplay>(root).Index == 1, "The caller displays the ancestor's focus.");

        if (direct)
            Hierarchy<ElementTree>.DestroySubtree(frames.World, parent);
        frames.World.Replace(root, new FocusFamily(false, false, focusableRoot));
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(root) == focusableRoot,
            "Focus returns to the root when it accepts focus, and otherwise clears.");
        Require(frames.World.Read<FamilyDisplay>(root).Index == (focusableRoot ? 0 : -1),
            "The caller displays the final focus result.");
    }

    internal static void RunFocusabilityRemoval()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusFamily(true, true, false));
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, parent)[0];
        Entity leaf = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        frames.World.Remove<Focusable>(leaf);
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(parent) && frames.World.IsAlive(leaf),
            "A live node that loses focusability returns focus to its focusable ancestor.");
        Require(frames.World.Read<FamilyDisplay>(root).Index == 1, "The caller displays the ancestor's focus.");
    }

    internal static void RunBranchRelease(bool focusableRoot)
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusFamily(true, true, focusableRoot));
        frames.Frame();
        Entity parent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Hierarchy<ElementTree>.DestroySubtree(frames.World, parent);
        frames.World.Replace(root, new FocusFamily(false, false, focusableRoot));
        frames.Frame();
        Require(frames.World.HasSparse<Focused>(root) == focusableRoot,
            "A whole branch releases focus through its ancestors to the surviving root or clears it.");
        Require(frames.World.Read<FamilyDisplay>(root).Index == (focusableRoot ? 0 : -1),
            "The caller displays the surviving focus after the whole branch is released.");
    }

    internal static void RunReparentedRelease()
    {
        using var frames = new FocusFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusFamily(true, true, false));
        frames.Frame();
        Entity previousParent = Hierarchy<ElementTree>.GetChildren(frames.World, root)[0];
        Entity group = Hierarchy<ElementTree>.GetChildren(frames.World, previousParent)[0];
        Entity leaf = Hierarchy<ElementTree>.GetChildren(frames.World, group)[0];
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        Entity parent = frames.World.CreateEntity(new Focusable());
        frames.World.Add(parent, new Clicks());
        Hierarchy<ElementTree>.SetParent(frames.World, parent, frames.Canvas);
        Hierarchy<ElementTree>.SetParent(frames.World, leaf, parent);
        frames.World.DestroyEntity(leaf);
        Require(frames.World.HasSparse<Focused>(parent) && !frames.World.HasSparse<Focused>(previousParent),
            "Release uses the parent chain changed since the previous navigation frame.");
    }

    private readonly record struct FocusFamily(bool ShowParent, bool ShowLeaf, bool FocusableRoot) : IView, IComponent
    {
        public BuildTask Build()
        {
            if (FocusableRoot)
                FocusFunctions.Focusable(Scope.Entity);
            int focused = Scope.Entity.Focused() ? 0 : -1;
            if (ShowParent)
            {
                Entity parent = FocusFunctions.Focusable(Absolute().Size(200, 200));
                if (parent.Focused())
                    focused = 1;
                using (parent.Content())
                using (Scope.View(new Group()).Content())
                {
                    if (ShowLeaf)
                    {
                        Entity leaf = FocusFunctions.Focusable(Absolute().Size(20, 20));
                        if (leaf.Focused())
                            focused = 2;
                    }
                }
            }
            Scope.Write<FamilyDisplay>(Scope.Entity) = new FamilyDisplay(focused);
            return default;
        }
    }

    private readonly record struct FamilyDisplay(int Index) : IComponent;
}

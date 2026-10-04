using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Layout.AbsoluteFunctions;
using UserPressable = SomeEngine.Elements.Consumer.Tests.Rewrites.PressableFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class PointerChecks
{
    internal static void RunRouting(bool rewritten)
    {
        using var world = new World();
        Entity canvas = world.CreateEntity(new Canvas { Size = new Size(800, 600) });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new HitTestSystem());
        systems.Add(new ViewRebuildSystem());
        systems.Add(new LayoutSystem());
        Entity root = Views.Views.Spawn(world, canvas, new CardButtons(rewritten));
        systems.Update();
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root)[0];
        Entity card = Hierarchy<ElementTree>.GetChildren(world, container)[0];
        var children = Hierarchy<ElementTree>.GetChildren(world, card);
        Entity branch = children[0];
        Entity button = Hierarchy<ElementTree>.GetChildren(world, branch)[0];
        Entity sibling = children[1];
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(false, false),
            "A new card displays the initial subtree state.");

        Pointer.Move(world, canvas, new Vector2(10, 10));
        Pointer.Press(world, canvas, PointerButton.Primary);
        systems.Update();
        RequireHover(world, button, hovered: true, pressed: true, within: true);
        RequireHover(world, branch, hovered: false, pressed: false, within: true);
        RequireHover(world, card, hovered: false, pressed: false, within: true);
        RequireHover(world, sibling, hovered: false, pressed: false, within: false);
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(true, true),
            "A caller reading Hover observes entry into the button's subtree.");

        Pointer.Move(world, canvas, new Vector2(160, 10));
        systems.Update();
        RequireHover(world, button, hovered: false, pressed: true, within: false);
        RequireHover(world, branch, hovered: false, pressed: false, within: false);
        RequireHover(world, card, hovered: false, pressed: false, within: true);
        RequireHover(world, sibling, hovered: true, pressed: false, within: true);
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(true, false),
            "Moving to a sibling leaves the branch and stays within the card.");

        Pointer.Move(world, canvas, new Vector2(400, 200));
        systems.Update();
        RequireHover(world, button, hovered: false, pressed: true, within: false);
        RequireHover(world, branch, hovered: false, pressed: false, within: false);
        RequireHover(world, card, hovered: false, pressed: false, within: false);
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(false, false),
            "The caller observes exit from the whole card while the button holds its press.");
        Pointer.Release(world, canvas, PointerButton.Primary);
        systems.Update();
        RequireHover(world, button, hovered: false, pressed: false, within: false);
        Require(world.Read<DisplayedClicks>(root) == new DisplayedClicks(0, 0),
            "Releasing outside preserves both displayed click counts.");

        Click(world, canvas);
        systems.Update();
        Require(world.Read<Clicks>(card).Count == 1 && world.Read<Clicks>(button).Count == 1,
            "A completed child click reaches the card through the branch.");
        Require(world.Read<DisplayedClicks>(root) == new DisplayedClicks(1, 1),
            "The caller consumes the card's and child's clicks in the same frame.");
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(true, true),
            "The pointer can enter the subtree again.");

        world.Add(branch, new StopsPointer());
        Click(world, canvas);
        systems.Update();
        Require(world.Read<Clicks>(card).Count == 1 && world.Read<Clicks>(button).Count == 2,
            "A stopping branch retains the card count and advances the child count.");
        Require(world.Read<DisplayedClicks>(root) == new DisplayedClicks(1, 2),
            "The caller observes the click boundary through Clicked.");
        Require(world.Read<DisplayedSubtree>(root) == new DisplayedSubtree(true, true),
            "The card's subtree state follows the hovered descendant across the click boundary.");

        world.Remove<StopsPointer>(branch);
        Click(world, canvas);
        systems.Update();
        Require(world.Read<DisplayedClicks>(root) == new DisplayedClicks(2, 3),
            "Removing the click boundary lets the caller consume the next bubbled click.");
    }

    private static void Click(World world, Entity canvas)
    {
        Pointer.Move(world, canvas, new Vector2(10, 10));
        Pointer.Press(world, canvas, PointerButton.Primary);
        Pointer.Release(world, canvas, PointerButton.Primary);
    }

    private static void RequireHover(World world, Entity node, bool hovered, bool pressed, bool within)
    {
        Hover value = world.Read<Hover>(node);
        Require(value.Hovered == hovered && value.Pressed == pressed && value.Within == within,
            $"Node {node}: expected hovered={hovered}, pressed={pressed}, within={within}; " +
            $"received hovered={value.Hovered}, pressed={value.Pressed}, within={value.Within}.");
    }

    private readonly record struct CardButtons(bool Rewritten) : IView, IComponent
    {
        public BuildTask Build()
        {
            ref CardButtonCounts count = ref Scope.ReadWrite<CardButtonCounts>(Scope.Entity);
            Entity card;
            Entity branch;
            using (Absolute().Content())
            {
                card = MarkPressable(Absolute().Size(300, 100));
                using (card.Content())
                {
                    branch = Absolute().Size(100, 50);
                    Scope.Ensure<Hover>(branch);
                    using (branch.Content())
                    {
                        Entity button = MarkPressable(Absolute().Size(100, 50));
                        if (ReadClick(button))
                            count.Button++;
                    }
                    MarkPressable(Absolute().Size(100, 50).Position(150, 0));
                }
            }
            if (ReadClick(card))
                count.Card++;
            Scope.Write<DisplayedClicks>(Scope.Entity) = new DisplayedClicks(count.Card, count.Button);
            Scope.Write<DisplayedSubtree>(Scope.Entity) = new DisplayedSubtree(
                Scope.Read<Hover>(card).Within, Scope.Read<Hover>(branch).Within);
            return default;
        }

        private Entity MarkPressable(Entity node) => Rewritten
            ? UserPressable.Pressable(node)
            : PressableFunctions.Pressable(node);

        private bool ReadClick(Entity node) => Rewritten ? UserPressable.Clicked(node) : node.Clicked();
    }

    private record struct CardButtonCounts(int Card, int Button) : IComponent;
    private readonly record struct DisplayedClicks(int Card, int Button) : IComponent;
    private readonly record struct DisplayedSubtree(bool Card, bool Branch) : IComponent;
}

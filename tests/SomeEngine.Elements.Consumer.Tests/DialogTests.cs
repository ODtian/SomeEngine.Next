using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.DialogFunctions;
using static SomeEngine.Elements.DialogActionsFunctions;
using static SomeEngine.Elements.DialogTitleFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class DialogTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void UserDialogTitleAndFooterMixWithBuiltInPartsAndShareThemeUpdates(
        bool userDialog, bool userTitle, bool userFooter) => DialogChecks.Run(userDialog, userTitle, userFooter);

    [Fact]
    public void DialogDisplaysItsDefaultBackgroundCornersAndShadow()
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ActionsDialog());
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity dialog = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Surface surface = frames.World.Read<Surface>(dialog);
        Assert.Equal(new Color(0.15f, 0.15f, 0.15f), surface.Fill);
        Assert.Equal(8, surface.CornerRadius);
        Assert.Equal(new DropShadow(new Color(0, 0, 0, 0.35f), new Vector2(0, 4), 12), surface.Shadow);
        Assert.Equal(new Thickness(24, 24, 24, 24), frames.World.Read<Box>(dialog).Padding);
    }

    [Fact]
    public void TitledDialogMatchesManualCompositionAndRestoresParent()
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new DialogPair("Settings"));
        frames.Frame();
        AssertDialogPair(frames.World, root, "Settings");

        frames.World.Replace(root, new DialogPair("Options"));
        frames.Frame();
        AssertDialogPair(frames.World, root, "Options");
    }

    [Fact]
    public void ActionsPlaceCallerChildrenHorizontallyAtTheRightEdge()
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ActionsDialog());
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity dialog = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity actions = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, dialog));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(frames.World, actions).ToArray();
        Assert.Equal(new[] { "first", "second" }, children.Select(node => frames.World.Read<DialogContent>(node).Label));
        Assert.Equal(new Rect(0, 0, 200, 78), frames.World.Read<LayoutRect>(dialog).Value);
        Assert.Equal(new Rect(24, 24, 152, 30), frames.World.Read<LayoutRect>(actions).Value);
        Assert.Equal(new Rect(88, 29, 40, 20), frames.World.Read<LayoutRect>(children[0]).Value);
        Assert.Equal(new Rect(136, 24, 40, 30), frames.World.Read<LayoutRect>(children[1]).Value);
    }

    [Fact]
    public void CallerCanMoveAndRemoveTitleWhileKeepingInsertedView()
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new DialogWithTitle(TitlePosition.First));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity dialog = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity[] first = Hierarchy<ElementTree>.GetChildren(frames.World, dialog).ToArray();
        Entity title = first[0];
        Entity body = first[1];
        Assert.Equal("Settings", frames.World.ReadSparse<DialogTitle>(title).Text);
        Entity text = Assert.Single(ButtonChecks.DescendantsWith<Text>(frames.World, title));
        Assert.Equal("Settings", frames.World.Read<Text>(text).Value);
        Assert.Equal(24, frames.World.Read<ShapedText>(text).Style.Size);
        Assert.Equal(new Color(1, 1, 1), frames.World.Read<ShapedText>(text).Style.Color);
        Assert.True(frames.World.Read<LayoutRect>(text).Value.Y < frames.World.Read<LayoutRect>(body).Value.Y);

        frames.World.Replace(root, new DialogWithTitle(TitlePosition.Last));
        frames.Frame();
        Assert.Equal(new[] { body, title }, Hierarchy<ElementTree>.GetChildren(frames.World, dialog).ToArray());
        Assert.True(frames.World.Read<LayoutRect>(body).Value.Y < frames.World.Read<LayoutRect>(text).Value.Y);

        frames.World.Replace(root, new DialogWithTitle(TitlePosition.Omitted));
        frames.Frame();
        Assert.Equal(body, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, dialog)));
        Assert.Empty(ButtonChecks.DescendantsWith<Text>(frames.World, dialog));
        Assert.Equal(new Rect(24, 24, 152, 20), frames.World.Read<LayoutRect>(body).Value);
    }

    [Fact]
    public void CallerCanReorderViewsAndInsertContent()
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new CallerDialog(false));
        frames.Frame();
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity dialog = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, container));
        Entity[] before = Hierarchy<ElementTree>.GetChildren(frames.World, dialog).ToArray();
        Assert.Equal(new[] { "first", "second" }, before.Select(node => frames.World.Read<DialogContent>(node).Label));
        Assert.Equal(new Rect(0, 0, 200, 114), frames.World.Read<LayoutRect>(dialog).Value);
        Assert.Equal(new Rect(24, 24, 152, 20), frames.World.Read<LayoutRect>(before[0]).Value);
        Assert.Equal(new Rect(24, 60, 152, 30), frames.World.Read<LayoutRect>(before[1]).Value);

        frames.World.Replace(root, new CallerDialog(true));
        frames.Frame();
        Entity[] after = Hierarchy<ElementTree>.GetChildren(frames.World, dialog).ToArray();
        Assert.Equal(new[] { "second", "middle", "first" }, after.Select(node => frames.World.Read<DialogContent>(node).Label));
        Assert.Equal(before[1], after[0]);
        Assert.Equal(before[0], after[2]);
        Assert.Equal(new Rect(0, 0, 200, 140), frames.World.Read<LayoutRect>(dialog).Value);
        Assert.Equal(new Rect(24, 24, 152, 30), frames.World.Read<LayoutRect>(after[0]).Value);
        Assert.Equal(new Rect(24, 70, 152, 10), frames.World.Read<LayoutRect>(after[1]).Value);
        Assert.Equal(new Rect(24, 96, 152, 20), frames.World.Read<LayoutRect>(after[2]).Value);
    }

    private readonly record struct CallerDialog(bool Reverse) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Dialog().Size(200, float.NaN).Padding(24).Content())
            {
                if (Reverse)
                {
                    Scope.View(new DialogContent("second", 30), 2);
                    Scope.View(new DialogContent("middle", 10), 3);
                    Scope.View(new DialogContent("first", 20), 1);
                }
                else
                {
                    Scope.View(new DialogContent("first", 20), 1);
                    Scope.View(new DialogContent("second", 30), 2);
                }
            }
            return default;
        }
    }

    private readonly record struct DialogContent(string Label, float Height) : IView, IComponent, ILayout
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) =>
            constraints.Constrain(new Size(40, Height));
    }

    private enum TitlePosition : byte { First, Last, Omitted }

    private readonly record struct DialogWithTitle(TitlePosition Position) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Dialog().Size(200, float.NaN).Padding(24).Content())
            {
                if (Position == TitlePosition.First)
                    Scope.View(new DialogTitle("Settings"), 11);
                Scope.View(new DialogContent("body", 20), 12);
                if (Position == TitlePosition.Last)
                    Scope.View(new DialogTitle("Settings"), 11);
            }
            return default;
        }
    }

    private readonly record struct ActionsDialog() : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            using (Dialog().Size(200, float.NaN).Padding(24).Content())
            using (DialogActions().Content())
            {
                Scope.View(new DialogContent("first", 20));
                Scope.View(new DialogContent("second", 30));
            }
            return default;
        }
    }

    private static void AssertDialogPair(World world, Entity root, string title)
    {
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        Entity[] children = Hierarchy<ElementTree>.GetChildren(world, container).ToArray();
        Assert.Equal(3, children.Length);
        Entity manual = children[0];
        Entity helper = children[1];
        Assert.Equal("after", world.Read<DialogContent>(children[2]).Label);
        Assert.Equal(world.Read<Surface>(manual), world.Read<Surface>(helper));
        Assert.Equal(world.Read<LayoutRect>(manual).Value, world.Read<LayoutRect>(helper).Value);
        Assert.Equal(new[] { title }, ButtonChecks.DescendantsWith<Text>(world, manual).Select(node => world.Read<Text>(node).Value));
        Assert.Equal(new[] { title }, ButtonChecks.DescendantsWith<Text>(world, helper).Select(node => world.Read<Text>(node).Value));
        Assert.Equal(
            ButtonChecks.DescendantsWith<LayoutRect>(world, manual).Select(node => world.Read<LayoutRect>(node).Value),
            ButtonChecks.DescendantsWith<LayoutRect>(world, helper).Select(node => world.Read<LayoutRect>(node).Value));
        Assert.Equal(
            ButtonChecks.DescendantsWith<ShapedText>(world, manual).Select(node => (world.Read<ShapedText>(node).Style.Size, world.Read<ShapedText>(node).Style.Color)),
            ButtonChecks.DescendantsWith<ShapedText>(world, helper).Select(node => (world.Read<ShapedText>(node).Style.Size, world.Read<ShapedText>(node).Style.Color)));
    }

    private readonly record struct DialogPair(string Title) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
            {
                using (Dialog().Size(200, float.NaN).Padding(24).Content())
                {
                    DialogTitle(Title);
                    DeclareDialogContent();
                }
                using (var dialog = TitledDialog(Title))
                {
                    dialog.Entity.Size(200, float.NaN).Padding(24);
                    DeclareDialogContent();
                }
                Scope.View(new DialogContent("after", 10));
            }
            return default;
        }

        private static void DeclareDialogContent()
        {
            Scope.View(new DialogContent("body", 20));
            using (DialogActions().Content())
            {
                Scope.View(new DialogContent("first", 20));
                Scope.View(new DialogContent("second", 30));
            }
        }
    }
}

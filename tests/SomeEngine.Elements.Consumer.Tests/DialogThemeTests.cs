using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.DialogActionsFunctions;
using static SomeEngine.Elements.DialogFunctions;
using static SomeEngine.Elements.DialogTitleFunctions;
using static SomeEngine.Layout.AbsoluteFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class DialogTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NearestThemeSuppliesItsDefaultsAndRetainsTransparentAndZeroValues(bool zeroInner, bool userFooter)
    {
        using var frames = new ButtonFrames();
        var theme = new DialogTheme
        {
            Fill = new Color(0.4f, 0.5f, 0.6f), CornerRadius = 12,
            Shadow = new DropShadow(new Color(0, 0, 0, 0.5f), new Vector2(2, 4), 6),
            Gap = 6, TitleColor = new Color(0.8f, 0.7f, 0.6f), TitleSize = 20, ActionsGap = 4,
        };
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedDialogs(theme, zeroInner, userFooter));
        frames.Frame();
        AssertNearestTheme(frames.World, root, zeroInner);

        theme.Fill = new Color(0.7f, 0.3f, 0.1f);
        theme.TitleSize = 30;
        theme.Gap = 10;
        theme.ActionsGap = 12;
        frames.World.Replace(root, new NestedDialogs(theme, zeroInner, userFooter));
        frames.Frame();
        AssertNearestTheme(frames.World, root, zeroInner);
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Entity outer = Hierarchy<ElementTree>.GetChildren(frames.World, container)[0];
        Assert.Equal(new Color(0.7f, 0.3f, 0.1f), frames.World.Read<Surface>(outer).Fill);
        Entity title = Assert.Single(ButtonChecks.DescendantsWith<Text>(frames.World, outer));
        Assert.Equal(30, frames.World.Read<ShapedText>(title).Style.Size);
    }

    private static void AssertNearestTheme(World world, Entity root, bool zeroInner)
    {
        Entity container = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, root));
        Entity group = Hierarchy<ElementTree>.GetChildren(world, container)[1];
        Entity inner = Assert.Single(Hierarchy<ElementTree>.GetChildren(world, group));
        Surface surface = world.Read<Surface>(inner);
        Assert.Equal(zeroInner ? default : new Color(0.7f, 0.1f, 0.3f), surface.Fill);
        Assert.Equal(zeroInner ? 0 : 8, surface.CornerRadius);
        Assert.Equal(zeroInner ? default : new DropShadow(new Color(0, 0, 0, 0.35f), new Vector2(0, 4), 12), surface.Shadow);
        Entity[] children = Hierarchy<ElementTree>.GetChildren(world, inner).ToArray();
        Entity title = Assert.Single(ButtonChecks.DescendantsWith<Text>(world, children[0]));
        TextStyle style = world.Read<ShapedText>(title).Style;
        Assert.Equal(24, style.Size);
        Assert.Equal(zeroInner ? default : new Color(1, 1, 1), style.Color);
        Assert.Equal(zeroInner ? 34.688f : 50.688f, world.Read<LayoutRect>(children[1]).Value.Y, 3);
        Entity[] actions = Hierarchy<ElementTree>.GetChildren(world, children[2]).ToArray();
        Rect first = world.Read<LayoutRect>(actions[0]).Value;
        Rect second = world.Read<LayoutRect>(actions[1]).Value;
        Assert.Equal(zeroInner ? 0 : 8, second.X - first.X - first.Width, 3);
    }

    private readonly record struct NestedDialogs(DialogTheme Theme, bool ZeroInner, bool UserFooter) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Entity.Provide(Theme);
            DialogTheme innerTheme = ZeroInner ? new DialogTheme
            {
                Fill = default, CornerRadius = 0, Shadow = default, Gap = 0, TitleColor = default, ActionsGap = 0,
            } : new DialogTheme { Fill = new Color(0.7f, 0.1f, 0.3f) };
            using (Absolute().Content())
            {
                DeclareDialog("Outer", UserFooter);
                using (Scope.View(new Group(), 91).Provide(innerTheme).Content())
                    DeclareDialog("Inner", UserFooter);
            }
            return default;
        }

        private static void DeclareDialog(string title, bool userFooter)
        {
            using (Dialog().Size(200, float.NaN).Padding(2).Content())
            {
                DialogTitle(title);
                Scope.View(new DialogContent("body", 20));
                Entity footer = userFooter ? Rewrites.DialogFooterFunctions.DialogFooter() : DialogActions();
                using (footer.Content())
                {
                    Scope.View(new DialogContent("first", 20));
                    Scope.View(new DialogContent("second", 30));
                }
            }
        }
    }

#if DEBUG
    [Theory]
    [InlineData(DialogPart.Dialog)]
    [InlineData(DialogPart.Title)]
    [InlineData(DialogPart.Actions)]
    [InlineData(DialogPart.TitledDialog)]
    [InlineData(DialogPart.UserDialog)]
    [InlineData(DialogPart.UserTitle)]
    [InlineData(DialogPart.UserFooter)]
    public void DialogPartsRequireAnActiveBuildScope(DialogPart part)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (part)
            {
                case DialogPart.Dialog: new Dialog().Build(); break;
                case DialogPart.Title: new DialogTitle("Settings").Build(); break;
                case DialogPart.Actions: new DialogActions().Build(); break;
                case DialogPart.TitledDialog: using (TitledDialog("Settings")) { } break;
                case DialogPart.UserDialog: new Rewrites.Dialog().Build(); break;
                case DialogPart.UserTitle: new Rewrites.DialogTitle("Settings").Build(); break;
                case DialogPart.UserFooter: new Rewrites.DialogFooter().Build(); break;
            }
        });
        Assert.Contains("active view build", error.Message);
    }
#endif
}

#if DEBUG
public enum DialogPart : byte { Dialog, Title, Actions, TitledDialog, UserDialog, UserTitle, UserFooter }
#endif

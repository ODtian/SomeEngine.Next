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

/// <summary>Dialog composition and theme updates shared with the NativeAOT executable.</summary>
internal static partial class DialogChecks
{
    internal static void Run(bool userDialog, bool userTitle, bool userFooter)
    {
        using var frames = new ButtonFrames();
        var theme = new DialogTheme
        {
            Fill = new Color(0.4f, 0.5f, 0.6f),
            CornerRadius = 12,
            Shadow = new DropShadow(new Color(0, 0, 0, 0.5f), new Vector2(2, 4), 6),
            Gap = 6,
            TitleColor = new Color(0.8f, 0.7f, 0.6f),
            TitleSize = 20,
            ActionsGap = 4,
        };
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas,
            new DialogExample(userDialog, userTitle, userFooter, theme));
        frames.Frame();
        var parts = ReadParts(frames.World, root);
        Surface surface = frames.World.Read<Surface>(parts.Dialog);
        Require(surface.Fill == new Color(0.4f, 0.5f, 0.6f) && surface.CornerRadius == 12 &&
            surface.Shadow == new DropShadow(new Color(0, 0, 0, 0.5f), new Vector2(2, 4), 6),
            "The dialog resolves its surface from the provided theme.");
        Require(frames.World.Read<Text>(parts.Title).Value == "Settings", "The title displays its text.");
        TextStyle style = frames.World.Read<ShapedText>(parts.Title).Style;
        Require(style.Size == 20 && style.Color == new Color(0.8f, 0.7f, 0.6f) &&
            ReferenceEquals(style.Font, frames.World.Read<TextTheme>(frames.Canvas).Style.Font),
            "The title resolves dialog size and color while inheriting the text font.");
        Require(Close(frames.World.Read<LayoutRect>(parts.Dialog).Value.Height, 99.24f) &&
            Close(frames.World.Read<LayoutRect>(parts.Body).Value.Y, 38.24f) &&
            Close(frames.World.Read<LayoutRect>(parts.FirstAction).Value.X, 111) &&
            Close(frames.World.Read<LayoutRect>(parts.SecondAction).Value.X, 155),
            "Built-in and user parts share the specified vertical spacing and right-aligned actions.");

        theme.Fill = new Color(0.7f, 0.3f, 0.1f);
        theme.CornerRadius = 3;
        theme.Shadow = new DropShadow(new Color(0.2f, 0.3f, 0.4f, 0.5f), new Vector2(-3, 6), 2);
        theme.Gap = 10;
        theme.TitleColor = new Color(0.9f, 0.8f, 0.7f);
        theme.TitleSize = 30;
        theme.ActionsGap = 12;
        frames.World.Replace(root, new DialogExample(userDialog, userTitle, userFooter, theme));
        frames.Frame();
        parts = ReadParts(frames.World, root);
        surface = frames.World.Read<Surface>(parts.Dialog);
        style = frames.World.Read<ShapedText>(parts.Title).Style;
        Require(surface.Fill == new Color(0.7f, 0.3f, 0.1f) && surface.CornerRadius == 3 &&
            surface.Shadow == new DropShadow(new Color(0.2f, 0.3f, 0.4f, 0.5f), new Vector2(-3, 6), 2),
            "A theme update refreshes fill, corners, and shadow within the frame.");
        Require(style.Size == 30 && style.Color == new Color(0.9f, 0.8f, 0.7f),
            "A theme update refreshes the title's public shaped style.");
        Require(Close(frames.World.Read<LayoutRect>(parts.Dialog).Value.Height, 120.86f) &&
            Close(frames.World.Read<LayoutRect>(parts.Body).Value.Y, 55.86f) &&
            Close(frames.World.Read<LayoutRect>(parts.FirstAction).Value.X, 103) &&
            Close(frames.World.Read<LayoutRect>(parts.SecondAction).Value.X, 155),
            "Both implementations reflow title, body, and actions from the updated theme.");
        Require(frames.World.Read<Box>(parts.Dialog).Fixed == new Size(200, float.NaN) &&
            frames.World.Read<Box>(parts.Dialog).Padding == new Thickness(5, 5, 5, 5),
            "The caller retains its dialog dimensions and padding across theme updates.");
    }

    private static DialogParts ReadParts(World world, Entity root)
    {
        Entity container = Hierarchy<ElementTree>.GetChildren(world, root).Single();
        Entity dialog = Hierarchy<ElementTree>.GetChildren(world, container).Single();
        Entity[] children = Hierarchy<ElementTree>.GetChildren(world, dialog).ToArray();
        Require(children.Length == 3, "The caller declares title, arbitrary body, and actions in order.");
        Entity title = ButtonChecks.DescendantsWith<Text>(world, children[0]).Single();
        Entity[] actions = Hierarchy<ElementTree>.GetChildren(world, children[2]).ToArray();
        Require(actions.Length == 2, "The footer accepts both caller-declared actions.");
        return new(dialog, title, children[1], actions[0], actions[1]);
    }

    private static bool Close(float value, float expected) => MathF.Abs(value - expected) < 0.001f;

    private static void Require(bool value, string message)
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    private readonly record struct DialogParts(Entity Dialog, Entity Title, Entity Body,
        Entity FirstAction, Entity SecondAction);

    private readonly record struct DialogExample(bool UserDialog, bool UserTitle, bool UserFooter, DialogTheme Theme)
        : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Entity.Provide(Theme);
            using (Absolute().Content())
            {
                Entity dialog = UserDialog ? Rewrites.DialogFunctions.Dialog() : Dialog();
                using (dialog.Size(200, float.NaN).Padding(5).Content())
                {
                    if (UserTitle)
                        Rewrites.DialogTitleFunctions.DialogTitle("Settings");
                    else
                        DialogTitle("Settings");
                    Scope.View(new DialogBody(20));
                    Entity footer = UserFooter ? Rewrites.DialogFooterFunctions.DialogFooter() : DialogActions();
                    using (footer.Content())
                    {
                        Scope.View(new DialogBody(20));
                        Scope.View(new DialogBody(30));
                    }
                }
            }
            return default;
        }
    }

    private readonly record struct DialogBody(float Height) : IView, IComponent, ILayout
    {
        public BuildTask Build() => default;
        public Size Measure(ref LayoutPass pass, in Constraints constraints) =>
            constraints.Constrain(new Size(40, Height));
    }
}

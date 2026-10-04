using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;
using SomeEngine.Views;
using static SomeEngine.Elements.Consumer.Tests.ButtonChecks;
using static SomeEngine.Layout.AbsoluteFunctions;
using static SomeEngine.Layout.FlexFunctions;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed partial class ButtonTests
{
#if DEBUG
    [Theory]
    [InlineData(ButtonKind.BuiltIn)]
    [InlineData(ButtonKind.Rewritten)]
    [InlineData(ButtonKind.Icon)]
    public void ButtonsRequireAnActiveBuildScope(ButtonKind kind)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            switch (kind)
            {
                case ButtonKind.BuiltIn: new Button("AV").Build(); break;
                case ButtonKind.Rewritten: new Rewrites.Button("AV").Build(); break;
                case ButtonKind.Icon: new Rewrites.IconButton(">", "AV").Build(); break;
            }
        });
        Assert.Contains("active view build", error.Message);
    }
#endif

    [Theory]
    [InlineData(ButtonKind.BuiltIn)]
    [InlineData(ButtonKind.Rewritten)]
    [InlineData(ButtonKind.Icon)]
    public void DefaultButtonDisplaysItsLabelAndTheme(ButtonKind kind)
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ButtonExample(kind, "AV"));

        frames.Frame();

        Entity surface = Assert.Single(DescendantsWith<Surface>(frames.World, root));
        Entity label = Assert.Single(DescendantsWith<Text>(frames.World, root),
            node => frames.World.Read<Text>(node).Value == "AV");
        Assert.Equal(new Color(0.2f, 0.2f, 0.2f), frames.World.Read<Surface>(surface).Fill);
        Assert.Equal(4, frames.World.Read<Surface>(surface).CornerRadius);
        Assert.Equal(new Thickness(8, 8, 8, 8), frames.World.Read<Box>(surface).Padding);
        Assert.Equal("AV", frames.World.Read<Text>(label).Value);
        Assert.Equal(new Color(1, 1, 1), frames.World.Read<ShapedText>(label).Style.Color);
        if (kind == ButtonKind.Icon)
        {
            Entity glyph = Assert.Single(DescendantsWith<Text>(frames.World, root),
                node => frames.World.Read<Text>(node).Value == ">");
            Rect iconRect = frames.World.Read<LayoutRect>(glyph).Value;
            Assert.Equal(iconRect.X + iconRect.Width + 4, frames.World.Read<LayoutRect>(label).Value.X, 3);
            Assert.Equal(new Color(1, 1, 1), frames.World.Read<ShapedText>(glyph).Style.Color);
        }
        else
            Assert.Equal(39.98f, frames.World.Read<LayoutRect>(surface).Value.Width, 3);
        Assert.Equal(43.24f, frames.World.Read<LayoutRect>(surface).Value.Height, 3);
    }

    [Theory]
    [InlineData(ButtonKind.BuiltIn)]
    [InlineData(ButtonKind.Rewritten)]
    [InlineData(ButtonKind.Icon)]
    public void ProvidedThemeChangesEveryButtonInSubtree(ButtonKind kind)
    {
        using var frames = new ButtonFrames();
        var theme = new ButtonTheme
        {
            Fill = new Color(0.4f, 0.5f, 0.6f),
            HoverFill = new Color(0.5f, 0.6f, 0.7f),
            PressedFill = new Color(0.2f, 0.3f, 0.4f),
            TextColor = new Color(0.8f, 0.7f, 0.6f, 0.5f),
            Padding = new Thickness(3, 5, 7, 11),
            CornerRadius = 12,
        };
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ThemedButtons(kind, theme));
        frames.Frame();

        AssertAppearance(frames.World, root, new Color(0.4f, 0.5f, 0.6f),
            new Color(0.8f, 0.7f, 0.6f, 0.5f), new Thickness(3, 5, 7, 11), 12);

        theme.Fill = new Color(0.7f, 0.3f, 0.1f);
        theme.TextColor = new Color(0.9f, 0.8f, 0.7f);
        theme.Padding = new Thickness(5, 7, 11, 13);
        theme.CornerRadius = 2;
        frames.World.Replace(root, new ThemedButtons(kind, theme));
        frames.Frame();

        AssertAppearance(frames.World, root, new Color(0.7f, 0.3f, 0.1f),
            new Color(0.9f, 0.8f, 0.7f), new Thickness(5, 7, 11, 13), 2);
    }

    [Theory]
    [InlineData(ButtonKind.BuiltIn)]
    [InlineData(ButtonKind.Rewritten)]
    [InlineData(ButtonKind.Icon)]
    public void PointerStatesChooseButtonFill(ButtonKind kind)
    {
        using var frames = new ButtonFrames();
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new ButtonExample(kind, "AV"));
        frames.Frame();
        Entity button = Assert.Single(DescendantsWith<Pressable>(frames.World, root));
        Entity surface = Assert.Single(DescendantsWith<Surface>(frames.World, root));

        Pointer.Move(frames.World, frames.Canvas, new Vector2(4, 4));
        frames.Frame();
        Assert.True(frames.World.Read<Hover>(button).Hovered);
        Assert.Equal(new Color(0.3f, 0.3f, 0.3f), frames.World.Read<Surface>(surface).Fill);

        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.True(frames.World.Read<Hover>(button).Pressed);
        Assert.Equal(new Color(0.1f, 0.1f, 0.1f), frames.World.Read<Surface>(surface).Fill);

        Pointer.Move(frames.World, frames.Canvas, new Vector2(300, 200));
        frames.Frame();
        Assert.False(frames.World.Read<Hover>(button).Hovered);
        Assert.True(frames.World.Read<Hover>(button).Pressed);
        Assert.Equal(new Color(0.1f, 0.1f, 0.1f), frames.World.Read<Surface>(surface).Fill);

        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.False(frames.World.Read<Hover>(button).Pressed);
        Assert.Equal(new Color(0.2f, 0.2f, 0.2f), frames.World.Read<Surface>(surface).Fill);
        Assert.Equal(0u, frames.World.Read<Clicks>(button).Count);
    }

    [Theory]
    [InlineData(ButtonKind.BuiltIn)]
    [InlineData(ButtonKind.Rewritten)]
    [InlineData(ButtonKind.Icon)]
    public void CallerConsumesClicksAndRetainsThemAfterLabelChanges(ButtonKind kind) => ButtonChecks.Run(kind);

    [Theory]
    [InlineData(ButtonKind.BuiltIn, false)]
    [InlineData(ButtonKind.Rewritten, false)]
    [InlineData(ButtonKind.Icon, false)]
    [InlineData(ButtonKind.BuiltIn, true)]
    [InlineData(ButtonKind.Rewritten, true)]
    [InlineData(ButtonKind.Icon, true)]
    public void NearestThemeUsesItsDefaultsAndPreservesExplicitZeroValues(ButtonKind kind, bool zeroInner)
    {
        using var frames = new ButtonFrames();
        var theme = new ButtonTheme
        {
            Fill = new Color(0.4f, 0.5f, 0.6f),
            HoverFill = new Color(0.5f, 0.6f, 0.7f),
            PressedFill = new Color(0.2f, 0.3f, 0.4f),
            TextColor = new Color(0.8f, 0.7f, 0.6f),
            Padding = new Thickness(3, 5, 7, 11),
            CornerRadius = 12,
        };
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new NestedButtons(kind, theme, zeroInner));
        frames.Frame();
        ButtonPair buttons = frames.World.Read<ButtonPair>(root);
        Entity outerSurface = Assert.Single(DescendantsWith<Surface>(frames.World, buttons.Outer));
        Entity innerSurface = Assert.Single(DescendantsWith<Surface>(frames.World, buttons.Inner));
        Assert.Equal(new Color(0.4f, 0.5f, 0.6f), frames.World.Read<Surface>(outerSurface).Fill);
        Assert.Equal(zeroInner ? default : new Color(0.7f, 0.1f, 0.3f), frames.World.Read<Surface>(innerSurface).Fill);
        Assert.Equal(zeroInner ? 0 : 4, frames.World.Read<Surface>(innerSurface).CornerRadius);
        Assert.Equal(zeroInner ? default : new Thickness(8, 8, 8, 8), frames.World.Read<Box>(innerSurface).Padding);
        foreach (Entity text in DescendantsWith<Text>(frames.World, buttons.Inner))
            Assert.Equal(zeroInner ? default : new Color(1, 1, 1), frames.World.Read<ShapedText>(text).Style.Color);

        Rect outerRect = frames.World.Read<LayoutRect>(outerSurface).Value;
        Pointer.Move(frames.World, frames.Canvas, new Vector2(outerRect.X + 1, outerRect.Y + 1));
        frames.Frame();
        Assert.Equal(new Color(0.5f, 0.6f, 0.7f), frames.World.Read<Surface>(outerSurface).Fill);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(new Color(0.2f, 0.3f, 0.4f), frames.World.Read<Surface>(outerSurface).Fill);

        theme.PressedFill = new Color(0.9f, 0.6f, 0.2f);
        frames.World.Replace(root, new NestedButtons(kind, theme, zeroInner));
        frames.Frame();
        Assert.Equal(new Color(0.9f, 0.6f, 0.2f), frames.World.Read<Surface>(outerSurface).Fill);
        Pointer.Release(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();

        Rect innerRect = frames.World.Read<LayoutRect>(innerSurface).Value;
        Pointer.Move(frames.World, frames.Canvas, new Vector2(innerRect.X + 1, innerRect.Y + 1));
        frames.Frame();
        Assert.Equal(zeroInner ? default : new Color(0.3f, 0.3f, 0.3f), frames.World.Read<Surface>(innerSurface).Fill);
        Pointer.Press(frames.World, frames.Canvas, PointerButton.Primary);
        frames.Frame();
        Assert.Equal(zeroInner ? default : new Color(0.1f, 0.1f, 0.1f), frames.World.Read<Surface>(innerSurface).Fill);
    }

    private readonly record struct NestedButtons(ButtonKind Kind, ButtonTheme Theme, bool ZeroInner) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Entity.Provide(Theme);
            ButtonTheme innerTheme = ZeroInner ? new ButtonTheme
            {
                Fill = default, HoverFill = default, PressedFill = default, TextColor = default,
                Padding = default, CornerRadius = 0,
            } : new ButtonTheme { Fill = new Color(0.7f, 0.1f, 0.3f) };
            Entity outer;
            Entity inner;
            using (Column(5).Align(Align.Start, Align.Start).Content())
            {
                outer = DeclareButton(Kind, "outer");
                using (Scope.View(new Group(), 9).Provide(innerTheme).Content())
                    inner = DeclareButton(Kind, "inner");
            }
            Scope.Write<ButtonPair>(Scope.Entity) = new ButtonPair(outer, inner);
            return default;
        }
    }

    private readonly record struct ButtonPair(Entity Outer, Entity Inner) : IComponent;

    private static void AssertAppearance(World world, Entity root, Color fill, Color textColor,
        Thickness padding, float cornerRadius)
    {
        List<Entity> surfaces = DescendantsWith<Surface>(world, root);
        Assert.Equal(2, surfaces.Count);
        foreach (Entity surface in surfaces)
        {
            Assert.Equal(fill, world.Read<Surface>(surface).Fill);
            Assert.Equal(cornerRadius, world.Read<Surface>(surface).CornerRadius);
            Assert.Equal(padding, world.Read<Box>(surface).Padding);
        }
        foreach (Entity text in DescendantsWith<Text>(world, root))
            Assert.Equal(textColor, world.Read<ShapedText>(text).Style.Color);
    }

    private readonly record struct ThemedButtons(ButtonKind Kind, ButtonTheme Theme) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Entity.Provide(Theme);
            using (Column(5).Align(Align.Start, Align.Start).Content())
            {
                DeclareButton(Kind, "AV");
                using (Scope.View(new Group(), 9).Content())
                    DeclareButton(Kind, "OK");
            }
            return default;
        }
    }

    private readonly record struct ButtonExample(ButtonKind Kind, string Label) : IView, IComponent
    {
        public BuildTask Build()
        {
            using (Absolute().Content())
                DeclareButton(Kind, Label);
            return default;
        }
    }
}

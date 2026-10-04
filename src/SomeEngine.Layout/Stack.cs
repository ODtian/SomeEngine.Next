using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Overlays children and aligns their outer boxes within the largest content size.</summary>
public record struct Stack(Align Horizontal = Align.Start, Align Vertical = Align.Start) : IView, IComponent, ILayout
{
    public Stack() : this(Align.Start, Align.Start) { }
    public static ChildSource Children => ChildSource.Caller;
    public BuildTask Build() => default;

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        DevelopmentCheck.Require(Horizontal is Align.Start or Align.Center or Align.End or Align.Stretch,
            "Stack.Horizontal requires Start, Center, End, or Stretch.");
        DevelopmentCheck.Require(Vertical is Align.Start or Align.Center or Align.End or Align.Stretch,
            "Stack.Vertical requires Start, Center, End, or Stretch.");
        var sizes = new Size[pass.Children.Count];
        var available = new Constraints(default, constraints.Max);
        Size extent = default;
        for (int index = 0; index < sizes.Length; index++)
        {
            Size size = pass.Measure(pass.Children[index], in available);
            sizes[index] = size;
            extent = new Size(MathF.Max(extent.Width, size.Width), MathF.Max(extent.Height, size.Height));
        }
        extent = constraints.Constrain(extent);
        for (int index = 0; index < sizes.Length; index++)
        {
            Size size = sizes[index];
            var child = pass.Children[index];
            Box box = pass.Has<Box>(child) ? pass.Read<Box>(child) : default;
            var allocated = new Size(
                Horizontal == Align.Stretch && float.IsNaN(box.Fixed.Width)
                    ? MathF.Min(extent.Width, box.Max.Width) : size.Width,
                Vertical == Align.Stretch && float.IsNaN(box.Fixed.Height)
                    ? MathF.Min(extent.Height, box.Max.Height) : size.Height);
            if (allocated != size)
            {
                Constraints stretched = Constraints.Fixed(allocated);
                size = pass.Measure(child, in stretched);
            }
            pass.Place(child, new Vector2(
                GetAlignmentOffset(Horizontal, extent.Width - size.Width), GetAlignmentOffset(Vertical, extent.Height - size.Height)));
        }
        return extent;
    }

    private static float GetAlignmentOffset(Align alignment, float remaining) => alignment switch
    {
        Align.Center => remaining / 2,
        Align.End => remaining,
        _ => 0,
    };
}

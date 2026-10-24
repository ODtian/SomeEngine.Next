using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Measures independently positioned children and bounds their extents.</summary>
public record struct Absolute() : IView, IComponent, ILayout
{
    public static ChildSource Children => ChildSource.Caller;
    public BuildTask Build() => default;

    public Size Measure(ref LayoutPass pass, in Constraints constraints)
    {
        Size extent = default;
        foreach (var child in pass.Children)
        {
            Vector2 position = pass.Has<AbsolutePosition>(child) ? pass.Read<AbsolutePosition>(child).Value : default;
            var available = new Constraints(default, new Size(
                MathF.Max(0, constraints.Max.Width - position.X),
                MathF.Max(0, constraints.Max.Height - position.Y)));
            Size size = pass.Measure(child, in available);
            pass.Place(child, position);
            extent = new Size(MathF.Max(extent.Width, position.X + size.Width),
                MathF.Max(extent.Height, position.Y + size.Height));
        }
        return constraints.Constrain(extent);
    }
}

using System.Diagnostics;
using System.Numerics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Layout;

internal static class LayoutValidation
{
    [Conditional("DEBUG")]
    internal static void CheckCanvas(in Canvas canvas)
    {
        CheckSize(canvas.Size);
        DevelopmentCheck.Require(float.IsFinite(canvas.Scale) && canvas.Scale > 0,
            "Canvas.Scale requires a finite positive value.");
    }

    [Conditional("DEBUG")]
    internal static void CheckSize(Size size) =>
        DevelopmentCheck.Require(float.IsFinite(size.Width) && size.Width >= 0 &&
            float.IsFinite(size.Height) && size.Height >= 0, "Layout sizes require finite nonnegative dimensions.");

    [Conditional("DEBUG")]
    internal static void CheckConstraints(in Constraints constraints)
    {
        CheckSize(constraints.Min);
        DevelopmentCheck.Require(constraints.Max.Width >= constraints.Min.Width &&
            constraints.Max.Height >= constraints.Min.Height, "Constraint minimum dimensions require values at or below maximum dimensions.");
    }

    [Conditional("DEBUG")]
    internal static void CheckBox(in Box box)
    {
        DevelopmentCheck.Require(float.IsFinite(box.Min.Width) && box.Min.Width >= 0 &&
            float.IsFinite(box.Min.Height) && box.Min.Height >= 0 &&
            box.Max.Width >= box.Min.Width && box.Max.Height >= box.Min.Height,
            "Box minimum dimensions require nonnegative values at or below maximum dimensions.");
        DevelopmentCheck.Require((float.IsNaN(box.Fixed.Width) || float.IsFinite(box.Fixed.Width) && box.Fixed.Width >= 0) &&
            (float.IsNaN(box.Fixed.Height) || float.IsFinite(box.Fixed.Height) && box.Fixed.Height >= 0),
            "Box.Fixed dimensions require finite nonnegative values or Size.Auto.");
        DevelopmentCheck.Require(float.IsFinite(box.Padding.Left) && box.Padding.Left >= 0 &&
            float.IsFinite(box.Padding.Top) && box.Padding.Top >= 0 &&
            float.IsFinite(box.Padding.Right) && box.Padding.Right >= 0 &&
            float.IsFinite(box.Padding.Bottom) && box.Padding.Bottom >= 0,
            "Box.Padding requires finite nonnegative edge sizes.");
    }

    [Conditional("DEBUG")]
    internal static void CheckPosition(Vector2 position) =>
        DevelopmentCheck.Require(float.IsFinite(position.X) && float.IsFinite(position.Y),
            "Layout positions require finite coordinates.");
}

using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements.Render;

internal static class ElementPresentation
{
    internal static (Matrix3x2 Matrix, float Alpha) Read(World world, Entity node, Rect rectangle)
    {
        ElementTransform transform = world.Has<ElementTransform>(node)
            ? world.Read<ElementTransform>(node) : ElementTransform.Identity;
        PresentationOffset offset = world.Has<PresentationOffset>(node)
            ? world.Read<PresentationOffset>(node) : PresentationOffset.Identity;
        Matrix3x2 matrix = Matrix3x2.CreateTranslation(-rectangle.X, -rectangle.Y)
            * transform.Matrix * Matrix3x2.CreateTranslation(rectangle.X, rectangle.Y) * offset.Matrix;
        return (matrix, transform.Alpha * offset.Alpha);
    }
}

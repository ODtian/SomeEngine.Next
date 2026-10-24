using System.Numerics;
using SomeEngine.Transparency.Research;

namespace SomeEngine.Render.Tests;

public sealed class TransparencyBehaviorTests
{
    [Theory]
    [InlineData(8, .1f)]
    [InlineData(32, .5f)]
    [InlineData(128, .1f)]
    public void Transparency_color_stays_within_the_requested_error(int panels, float alpha)
    {
        TriangleHierarchy geometry = ResearchGeometry.CreatePanels(panels, alpha);
        Vector2 point = new(.13f, .23f);
        const float colorError = 1f / 255;
        TransparencyResult result = TransparencyEvaluator.Evaluate(geometry, point, 1, new(.125f), colorError);
        Vector3 reference = TransparencyEvaluator.CompositeReference(geometry, point, 1, new(.125f));
        Vector3 difference = Vector3.Abs(result.Color - reference);
        Assert.InRange(difference.X, 0, colorError);
        Assert.InRange(difference.Y, 0, colorError);
        Assert.InRange(difference.Z, 0, colorError);
    }

    [Fact]
    public void Changing_transparent_submission_order_preserves_the_color()
    {
        TriangleHierarchy forward = ResearchGeometry.CreatePanels(32, .5f);
        TriangleHierarchy reverse = new(forward.Triangles.Reverse().ToArray());
        Vector2 point = new(.13f, .23f);
        TransparencyResult a = TransparencyEvaluator.Evaluate(forward, point, 1, new(.125f), 1f / 255);
        TransparencyResult b = TransparencyEvaluator.Evaluate(reverse, point, 1, new(.125f), 1f / 255);
        Assert.InRange(Vector3.Distance(a.Color, b.Color), 0, 1e-6f);
    }
}

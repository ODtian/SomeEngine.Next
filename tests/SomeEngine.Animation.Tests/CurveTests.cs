using SomeEngine.ECS;

namespace SomeEngine.Animation.Tests;

public class CurveTests
{
#if DEBUG
    [Fact]
    public void InvalidCurveInputsReportDevelopmentChecks()
    {
        Assert.Throws<InvalidOperationException>(() => Spec.Tween(-1));
        Assert.Throws<InvalidOperationException>(() => Spec.Spring(stiffness: 0));
        Assert.Throws<InvalidOperationException>(() => Spec.Spring(damping: -1));
        Assert.Throws<InvalidOperationException>(() => Spec.Spring(tolerance: 0));
        Assert.Throws<InvalidOperationException>(() => Spec.Keyframes(1, start: -1));
        Assert.Throws<InvalidOperationException>(() => Spec.Spring().Curve.Sample(0.5f));
    }
#endif

    [Fact]
    public unsafe void FunctionCurveSamplesAUserFunctionPointer()
    {
        var spec = new Spec(1, Curve.FromFunction(&Square));

        Assert.Equal(0.0625f, spec.Curve.Sample(0.25f));
        Assert.Equal(0, spec.Curve.Sample(-1));
        Assert.Equal(1, spec.Curve.Sample(2));
    }

    private static float Square(float time) => time * time;

    [Fact]
    public void KeyframeCurveSamplesItsInstanceBufferAndSegmentEasing()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<Keyframe>(entity);
        world.ExecuteBufferWrite<Keyframe>(entity, static keys =>
        {
            keys.Add(new Keyframe(0, 999));
            keys.Add(new Keyframe(0, 0));
            keys.Add(new Keyframe(0.5f, 0.8f, EasingType.QuadIn));
            keys.Add(new Keyframe(1, 1));
        });
        Spec spec = Spec.Keyframes(2, start: 1, count: 3);

        world.ExecuteBufferRead<Keyframe>(entity, keys =>
        {
            Assert.Equal(0, spec.Curve.Sample(-1, keys.AsSpan()));
            Assert.Equal(0.4f, spec.Curve.Sample(0.25f, keys.AsSpan()), 5);
            Assert.Equal(0.8f, spec.Curve.Sample(0.5f, keys.AsSpan()), 5);
            Assert.Equal(0.85f, spec.Curve.Sample(0.75f, keys.AsSpan()), 5);
            Assert.Equal(1, spec.Curve.Sample(2, keys.AsSpan()));
        });
    }

    [Fact]
    public void SpringMatchesReferenceStepsAndSettlesAtItsTarget()
    {
        Curve spring = Spec.Spring(180, 20).Curve;
        float current = 0;
        float velocity = 0;

        bool settled = spring.Step<float, FloatOps>(1f / 60, 1, ref current, ref velocity);

        // Reference Step.cs, two semi-implicit steps of 1/120 second, mass = 1.
        Assert.False(settled);
        Assert.Equal(0.035260417f, current, 6);
        Assert.Equal(2.73125f, velocity, 5);

        for (int frame = 0; frame < 1200 && !settled; frame++)
            settled = spring.Step<float, FloatOps>(1f / 60, 1, ref current, ref velocity);

        Assert.True(settled);
        Assert.Equal(1, current);
        Assert.Equal(0, velocity);
    }

    [Theory]
    [InlineData(EasingType.Linear, 0.25f)]
    [InlineData(EasingType.SineIn, 0.07612047f)]
    [InlineData(EasingType.SineOut, 0.38268343f)]
    [InlineData(EasingType.SineInOut, 0.14644661f)]
    [InlineData(EasingType.QuadIn, 0.0625f)]
    [InlineData(EasingType.QuadOut, 0.4375f)]
    [InlineData(EasingType.QuadInOut, 0.125f)]
    [InlineData(EasingType.CubicIn, 0.015625f)]
    [InlineData(EasingType.CubicOut, 0.578125f)]
    [InlineData(EasingType.CubicInOut, 0.0625f)]
    [InlineData(EasingType.ExpoOut, 0.8232233f)]
    [InlineData(EasingType.BackIn, -0.06413656f)]
    [InlineData(EasingType.ElasticOut, 0.9116117f)]
    [InlineData(EasingType.BounceOut, 0.47265625f)]
    public void TweenSamplesReferenceEasingValues(EasingType easing, float expected)
    {
        Spec spec = Spec.Tween(2, easing);

        Assert.Equal(2, spec.Duration);
        Assert.Equal(expected, spec.Curve.Sample(0.25f), 5);
        Assert.Equal(0, spec.Curve.Sample(-1));
        Assert.Equal(1, spec.Curve.Sample(2));
    }
}

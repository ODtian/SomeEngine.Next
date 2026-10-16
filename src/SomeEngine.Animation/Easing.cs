namespace SomeEngine.Animation;

public enum EasingType : byte
{
    Linear,
    SineIn, SineOut, SineInOut,
    QuadIn, QuadOut, QuadInOut,
    CubicIn, CubicOut, CubicInOut,
    QuartIn, QuartOut, QuartInOut,
    QuintIn, QuintOut, QuintInOut,
    ExpoIn, ExpoOut, ExpoInOut,
    CircIn, CircOut, CircInOut,
    BackIn, BackOut, BackInOut,
    ElasticIn, ElasticOut, ElasticInOut,
    BounceIn, BounceOut, BounceInOut,
}

public static class Easing
{
    /// <summary>Resolves an enum input once when the curve is bound.</summary>
    public static unsafe delegate* managed<float, float> Resolve(EasingType easing) => easing switch
    {
        EasingType.Linear => &Linear.Sample,
        EasingType.SineIn => &SineIn.Sample,
        EasingType.SineOut => &SineOut.Sample,
        EasingType.SineInOut => &SineInOut.Sample,
        EasingType.QuadIn => &QuadIn.Sample,
        EasingType.QuadOut => &QuadOut.Sample,
        EasingType.QuadInOut => &QuadInOut.Sample,
        EasingType.CubicIn => &CubicIn.Sample,
        EasingType.CubicOut => &CubicOut.Sample,
        EasingType.CubicInOut => &CubicInOut.Sample,
        EasingType.QuartIn => &QuartIn.Sample,
        EasingType.QuartOut => &QuartOut.Sample,
        EasingType.QuartInOut => &QuartInOut.Sample,
        EasingType.QuintIn => &QuintIn.Sample,
        EasingType.QuintOut => &QuintOut.Sample,
        EasingType.QuintInOut => &QuintInOut.Sample,
        EasingType.ExpoIn => &ExpoIn.Sample,
        EasingType.ExpoOut => &ExpoOut.Sample,
        EasingType.ExpoInOut => &ExpoInOut.Sample,
        EasingType.CircIn => &CircIn.Sample,
        EasingType.CircOut => &CircOut.Sample,
        EasingType.CircInOut => &CircInOut.Sample,
        EasingType.BackIn => &BackIn.Sample,
        EasingType.BackOut => &BackOut.Sample,
        EasingType.BackInOut => &BackInOut.Sample,
        EasingType.ElasticIn => &ElasticIn.Sample,
        EasingType.ElasticOut => &ElasticOut.Sample,
        EasingType.ElasticInOut => &ElasticInOut.Sample,
        EasingType.BounceIn => &BounceIn.Sample,
        EasingType.BounceOut => &BounceOut.Sample,
        EasingType.BounceInOut => &BounceInOut.Sample,
        _ => &Linear.Sample,
    };

    public static float Evaluate(EasingType easing, float t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return easing switch
        {
            EasingType.Linear => t,
            EasingType.SineIn => 1 - MathF.Cos(t * MathF.PI / 2),
            EasingType.SineOut => MathF.Sin(t * MathF.PI / 2),
            EasingType.SineInOut => (1 - MathF.Cos(MathF.PI * t)) / 2,
            EasingType.QuadIn => t * t,
            EasingType.QuadOut => 1 - (1 - t) * (1 - t),
            EasingType.QuadInOut => t < 0.5f ? 2 * t * t : 1 - MathF.Pow(2 - 2 * t, 2) / 2,
            EasingType.CubicIn => t * t * t,
            EasingType.CubicOut => 1 - MathF.Pow(1 - t, 3),
            EasingType.CubicInOut => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(2 - 2 * t, 3) / 2,
            EasingType.QuartIn => t * t * t * t,
            EasingType.QuartOut => 1 - MathF.Pow(1 - t, 4),
            EasingType.QuartInOut => t < 0.5f ? 8 * t * t * t * t : 1 - MathF.Pow(2 - 2 * t, 4) / 2,
            EasingType.QuintIn => MathF.Pow(t, 5),
            EasingType.QuintOut => 1 - MathF.Pow(1 - t, 5),
            EasingType.QuintInOut => t < 0.5f ? 16 * MathF.Pow(t, 5) : 1 - MathF.Pow(2 - 2 * t, 5) / 2,
            EasingType.ExpoIn => MathF.Pow(2, 10 * t - 10),
            EasingType.ExpoOut => 1 - MathF.Pow(2, -10 * t),
            EasingType.ExpoInOut => t < 0.5f ? MathF.Pow(2, 20 * t - 10) / 2 : (2 - MathF.Pow(2, 10 - 20 * t)) / 2,
            EasingType.CircIn => 1 - MathF.Sqrt(1 - t * t),
            EasingType.CircOut => MathF.Sqrt(1 - (t - 1) * (t - 1)),
            EasingType.CircInOut => t < 0.5f ? (1 - MathF.Sqrt(1 - 4 * t * t)) / 2 : (MathF.Sqrt(1 - MathF.Pow(2 - 2 * t, 2)) + 1) / 2,
            EasingType.BackIn => 2.70158f * t * t * t - 1.70158f * t * t,
            EasingType.BackOut => 1 + 2.70158f * MathF.Pow(t - 1, 3) + 1.70158f * MathF.Pow(t - 1, 2),
            EasingType.BackInOut => t < 0.5f
                ? MathF.Pow(2 * t, 2) * (3.5949095f * 2 * t - 2.5949095f) / 2
                : (MathF.Pow(2 * t - 2, 2) * (3.5949095f * (2 * t - 2) + 2.5949095f) + 2) / 2,
            EasingType.ElasticIn => -MathF.Pow(2, 10 * t - 10) * MathF.Sin((10 * t - 10.75f) * (2 * MathF.PI / 3)),
            EasingType.ElasticOut => MathF.Pow(2, -10 * t) * MathF.Sin((10 * t - 0.75f) * (2 * MathF.PI / 3)) + 1,
            EasingType.ElasticInOut => t < 0.5f
                ? -MathF.Pow(2, 20 * t - 10) * MathF.Sin((20 * t - 11.125f) * (2 * MathF.PI / 4.5f)) / 2
                : MathF.Pow(2, 10 - 20 * t) * MathF.Sin((20 * t - 11.125f) * (2 * MathF.PI / 4.5f)) / 2 + 1,
            EasingType.BounceIn => 1 - BounceOut(1 - t),
            EasingType.BounceOut => BounceOut(t),
            EasingType.BounceInOut => t < 0.5f ? (1 - BounceOut(1 - 2 * t)) / 2 : (1 + BounceOut(2 * t - 1)) / 2,
            _ => t,
        };
    }

    private static float BounceOut(float t)
    {
        const float factor = 7.5625f;
        const float width = 2.75f;
        if (t < 1 / width) return factor * t * t;
        if (t < 2 / width) return factor * (t -= 1.5f / width) * t + 0.75f;
        if (t < 2.5f / width) return factor * (t -= 2.25f / width) * t + 0.9375f;
        return factor * (t -= 2.625f / width) * t + 0.984375f;
    }
}

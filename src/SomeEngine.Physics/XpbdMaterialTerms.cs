namespace SomeEngine.Physics;

/// <summary>Scaled spring, damping and compliance terms for one XPBD row.</summary>
internal readonly struct XpbdMaterialTerms
{
    private readonly float _spring, _damping, _compliance;

    internal XpbdMaterialTerms(float stiffness, float damping, float duration)
    {
        if (stiffness > 0 && stiffness * duration >= damping)
        {
            float inverseStiffness = 1 / stiffness;
            _spring = 1;
            _damping = damping * inverseStiffness / duration;
            _compliance = inverseStiffness / (duration * duration);
        }
        else if (damping > 0)
        {
            _spring = stiffness * duration / damping;
            _damping = 1;
            _compliance = 1 / (damping * duration);
        }
        else
        {
            _spring = _damping = 0;
            _compliance = 1;
        }
    }

    internal float Project(float error, float startError, float weight, float multiplier)
    {
        if (weight == 0 && _compliance == 0) return 0;
        return multiplier - (_spring * error + _damping * (error - startError) + _compliance * multiplier)
            / (_compliance + (_spring + _damping) * weight);
    }
}

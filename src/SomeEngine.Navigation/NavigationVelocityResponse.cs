using System.Numerics;

namespace SomeEngine.Navigation;

public readonly record struct NavigationVelocityState(Vector3 Position, Vector3 Velocity);

/// <summary>TimeConstant zero selects instantaneous velocity; a positive value selects exponential response in seconds.</summary>
public readonly record struct NavigationVelocityResponse(float TimeConstant)
{
    public float VelocityFraction(float time) => TimeConstant == 0 ? 1 : (float)(1 - Math.Exp(-time / TimeConstant));

    /// <summary>Position change caused by one unit of target-velocity change over the interval.</summary>
    public float DisplacementFactor(float time) => TimeConstant == 0
        ? time : (float)(time - TimeConstant * (1 - Math.Exp(-time / TimeConstant)));

    public float TargetChangeRadius(float maximumAcceleration) => TimeConstant * maximumAcceleration;

    /// <summary>Integrates the selected target with the same closed form used by acceleration-aware avoidance.</summary>
    public NavigationVelocityState Integrate(Vector3 position, Vector3 velocity, Vector3 targetVelocity, float time)
    {
        Vector3 change = targetVelocity - velocity;
        return new(position + velocity * time + DisplacementFactor(time) * change,
            velocity + VelocityFraction(time) * change);
    }
}

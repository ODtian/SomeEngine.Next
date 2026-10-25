using System.Numerics;

namespace SomeEngine.Liquid;

public enum LiquidMotion { Airborne, Flowing, Resting }

/// <summary>A material volume. Lengths are metres, time is seconds and volume is cubic metres.</summary>
public struct LiquidPoint
{
    public int Number, Previous;
    public Vector3 Position, Velocity, Normal, Axis;
    public double Volume;
    public float Age, Radius, Width, RadialVelocity, WidthVelocity;
    public float Strain, Stress, Disturbance;
    public Vector4 Impact;
    public LiquidContactSurface Surface;
    public readonly float Height => (float)(Volume / (Math.PI * Radius * Width));
    public readonly float Neck => Previous >= 0 ? 1 - Disturbance : 1;
    public readonly LiquidMotion Motion => Surface == LiquidContactSurface.None ? LiquidMotion.Airborne :
        Velocity == Vector3.Zero && RadialVelocity == 0 && WidthVelocity == 0 ? LiquidMotion.Resting : LiquidMotion.Flowing;
}

/// <summary>Inlet flux in m³/s, velocity in m/s, and an independent sampling interval.</summary>
public readonly record struct LiquidSource(Vector3 Position, Vector3 Velocity, double VolumeRate,
    float Duration = 3.6f, float SampleSeconds = .05f, float PulsePeriod = .72f, float PulseSeconds = .44f,
    float RadiusVariation = .001f, float SurfaceRadius = 0);

/// <summary>Liquid/solid contact angles in degrees and porous absorption speed in m/s.</summary>
public readonly record struct LiquidWetting(float RecedingDegrees, float AdvancingDegrees, float AbsorptionSpeed = 0,
    float RetentionFactor = .7853981634f)
{
    public float CosineDifference => MathF.Cos(RecedingDegrees * MathF.PI / 180) - MathF.Cos(AdvancingDegrees * MathF.PI / 180);
    public float EquilibriumCosine => .5f * (MathF.Cos(RecedingDegrees * MathF.PI / 180) + MathF.Cos(AdvancingDegrees * MathF.PI / 180));
}

/// <summary>Diffusion coefficient (m²/s) and vapour density difference (kg/m³) for thin sessile drops.</summary>
public readonly record struct LiquidEvaporation(float Diffusivity, float VaporDensityDifference);

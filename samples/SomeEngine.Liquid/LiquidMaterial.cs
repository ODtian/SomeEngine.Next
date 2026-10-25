using System.Numerics;

namespace SomeEngine.Liquid;

public enum LiquidFluid { Water, Urine, Semen }
public enum LiquidEmission { Stream, Pulses, Drops }

/// <summary>Dynamic viscosity (Pa s), surface tension (N/m), density (kg/m³), modulus (Pa) and optical coefficients.</summary>
public readonly record struct LiquidMaterial(
    float Viscosity, float SurfaceTension, float Density,
    float Elasticity, float RelaxationSeconds,
    Vector3 Extinction, Vector3 Scattering, float ScatteringStrength, float Roughness)
{
    public static LiquidMaterial For(LiquidFluid fluid) => fluid switch
    {
        LiquidFluid.Urine => new(
            Viscosity: .0011f, SurfaceTension: .055f, Density: 1010,
            Elasticity: 0, RelaxationSeconds: .015f,
            Extinction: new(.8f, 3, 34), Scattering: new(.65f, .48f, .035f), ScatteringStrength: .12f, Roughness: .025f),
        LiquidFluid.Semen => new(
            Viscosity: .05f, SurfaceTension: .055f, Density: 1000,
            Elasticity: 3, RelaxationSeconds: .42f,
            Extinction: new(70, 85, 110), Scattering: new(.91f, .89f, .80f), ScatteringStrength: 1, Roughness: .045f),
        _ => new(
            Viscosity: .001f, SurfaceTension: .072f, Density: 1000,
            Elasticity: 0, RelaxationSeconds: .015f,
            Extinction: new(24, 7, 2.5f), Scattering: new(.025f, .32f, .38f), ScatteringStrength: .65f, Roughness: .025f),
    };
}

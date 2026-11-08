#ifndef PHYSICS_XPBD_MATERIAL
#define PHYSICS_XPBD_MATERIAL
float deviceXpbdProjectMaterial(float stiffness, float damping, float duration,
    float error, float startError, float weight, float multiplier)
{
    float spring, viscosity, compliance;
    if (stiffness > 0 && stiffness * duration >= damping)
    {
        float inverseStiffness = 1 / stiffness;
        spring = 1;
        viscosity = damping * inverseStiffness / duration;
        compliance = inverseStiffness / (duration * duration);
    }
    else if (damping > 0)
    {
        spring = stiffness * duration / damping;
        viscosity = 1;
        compliance = 1 / (damping * duration);
    }
    else { spring = viscosity = 0; compliance = 1; }
    if (weight == 0 && compliance == 0) return 0;
    return multiplier - (spring * error + viscosity * (error - startError) + compliance * multiplier)
        / (compliance + (spring + viscosity) * weight);
}
#endif

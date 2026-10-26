#ifndef PHYSICS_XPBD_PROJECTION_MATH
#define PHYSICS_XPBD_PROJECTION_MATH
// XPBD equation 18, with its force response to the current constraint residual.
float deviceXpbdMultiplierChange(float residual,float multiplier,float mobility,float compliance,float duration,
    out float forceStiffness)
{
    float squaredStep=duration*duration,alpha=compliance/squaredStep;
    float response=1/(mobility+alpha);
    forceStiffness=response/squaredStep;
    return (-residual-alpha*multiplier)*response;
}
#endif

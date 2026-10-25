#include "BridgeState.h"
#include "ResearchStep.h"

// BDF1 in inertial-correction form keeps rounded world positions from feeding
// their subtraction error back into the next step's free-flight velocity.
void researchVelocity(Rigid* body)
{
    if (!researchBodyAwake(body)) return;
    const auto& islands = researchStep->experiment->islands;
    int index = researchStep->experiment->index(body);
    if (islands.directContacts[index])
    {
#ifdef DEMO_2D
        body->velocity = islands.contactVelocities[index];
#else
        body->velocityLin = islands.contactVelocities[index];
        body->velocityAng = {0, 0, 0};
#endif
        return;
    }
    const auto& solver = researchStep->experiment->solver;
    float dt = solver.dt;
#ifdef DEMO_2D
    body->velocity = body->prevVelocity + float3{0, solver.gravity, 0} * dt
        + (body->position - body->inertial) / dt;
    body->velocity.z = (!researchStep->experiment->newton.compliant ? body->position.z - body->initial.z
        : newtonPlanarDifference(body->position.z, body->initial.z)) / dt;
#else
    body->velocityLin = body->prevVelocityLin + float3{0, 0, solver.gravity} * dt
        + (body->positionLin - body->inertialLin) / dt;
    body->velocityAng = ((researchStep->experiment->researchExtensions || (!researchStep->experiment->newton.compliant && researchStructuralBody(body)))
        ? researchRotationDifference(body->positionAng, body->initialAng)
        : newtonRotationDifference(body->positionAng, body->initialAng)) / dt;
#endif
}

// Time integration owns the single velocity reconstruction between the main
// AVBD iterations and optional position-only stabilization.
void researchCommitVelocities()
{
    RESEARCH_TIME(phaseTime, Velocity);
    for (Rigid* body : researchStep->experiment->bodies)
    {
#ifdef DEMO_2D
        body->prevVelocity = body->velocity;
#else
        body->prevVelocityLin = body->velocityLin;
#endif
        if (body->mass > 0) researchVelocity(body);
    }
}

#include "BridgeState.h"
#include "ResearchStep.h"

// AVBD owns iteration scheduling; selected extensions contribute at named stages.
void researchBeforeIteration(int iteration)
{
    auto* step = researchStep;
    step->currentIteration = iteration;
    step->maximumBodyChangeSquared = 0;
    if (iteration > step->experiment->solver.iterations)
    {
        ++step->experiment->lastStabilizationIterations;
        return;
    }
    if (iteration == 0)
    {
        if (!researchKeepSupportGraph())
        {
            clearSupportRanges(step->experiment);
            step->experiment->contactReferences.clear();
        }
        researchRestoreContactForces(step->experiment);
        researchBeginCollisionMotion();
#ifndef DEMO_2D
        researchAddMovedContacts();
#endif
        if (step->experiment->researchExtensions) researchSolveContactBlocks();
    }
#ifndef DEMO_2D
    // Current box geometry decides when a contact row needs new witnesses.
    // Retained rows keep their material points and dual history.
    if (!step->experiment->newton.compliant) researchRefreshContacts();
#endif
    if (step->correct && iteration == step->iteration) step->correct(step->context);
    if (iteration == 0 && step->experiment->researchExtensions) researchSolveRestingSupports();
    if (iteration > 0 && step->experiment->longRange.enabled) researchUpdateSupports();
    if (iteration == 0) researchInitializeJointBounds(step->experiment);
    researchSelectIterations();
    if (iteration == step->experiment->solver.iterations)
        ++step->experiment->lastStabilizationIterations;

}

void researchAfterIteration()
{
    Experiment* experiment = researchStep->experiment;
    researchUpdateLongRangeForces();
    if (experiment->researchExtensions)
    {
        bool splitTree = !experiment->jointTree.contactBodies.empty() && experiment->solver.iterations > 1;
        if (splitTree && researchStep->currentIteration == experiment->solver.iterations / 2 - 1) researchSolveConstraints(1);
        if (researchStep->currentIteration == experiment->solver.iterations - 1) researchSolveConstraints(splitTree ? 1 : 2);
    }
    bool addedContacts = researchAddMovedContacts();
    if (addedContacts)
    {
#ifdef DEMO_2D
        if (experiment->longRange.enabled && !experiment->newton.compliant && !experiment->researchExtensions)
            // Candidate growth requires a contact export before cached support reuse.
            experiment->supportGraph.manifoldCount = -1;
#endif
        researchUpdateSupports();
        if (experiment->researchExtensions && researchStep->currentIteration == experiment->solver.iterations - 1)
            ++experiment->solver.iterations;
    }
    researchExtendContactSolve();
    auto* step = researchStep;
    if (!experiment->newton.compliant && !experiment->researchExtensions
        && step->currentIteration + 1 == experiment->solver.iterations
        && experiment->solver.iterations < step->configuredIterations * contactIterationMultiplier)
    {
        bool unresolved = addedContacts;
        for (Force* force = experiment->solver.forces; force && !unresolved; force = force->next)
            if (auto* contact = dynamic_cast<Manifold*>(force); contact)
            {
                float3 normal; float gap;
                unresolved = researchContactPenetrates(contact, constraintAccuracy, normal, gap);
#ifdef DEMO_2D
                if (unresolved) researchRefreshPlanarContact(contact, normal, gap);
#endif
            }
        if (unresolved) ++experiment->solver.iterations;
    }
    // A second primal update observes the first dual/support force transfer.
    constexpr int firstConvergenceIteration = 1;
#ifdef DEMO_2D
    constexpr int convergenceBodyCount = 128;
#else
    constexpr int convergenceBodyCount = 0;
#endif
    if (!addedContacts && !experiment->researchExtensions && step->bodyCount >= convergenceBodyCount && experiment->joints.empty()
        && experiment->longRange.jointBounds.empty() && step->currentIteration >= firstConvergenceIteration
        && step->currentIteration + 1 < experiment->solver.iterations
        && step->maximumBodyChangeSquared <= contactGeometryAccuracy * contactGeometryAccuracy)
    {
        bool converged = true;
#ifdef DEMO_2D
        for (Force* force = experiment->solver.forces; force; force = force->next)
            if (auto* contact = dynamic_cast<ContactManifold*>(force); contact && contact->penetration > contactGeometryAccuracy)
            { converged = false; break; }
#else
        for (const auto& term : experiment->rigidForces)
            if (term.contact && static_cast<ContactManifold*>(term.contact)->penetration > contactGeometryAccuracy)
            { converged = false; break; }
#endif
#ifndef DEMO_2D
        for (Force* force = experiment->solver.forces; force && converged; force = force->next)
            if (auto* contact = dynamic_cast<Manifold*>(force))
            {
                converged = !researchContactPenetrates(contact, (float)contactGeometryAccuracy);
            }
#endif
        if (converged)
        {
            researchUpdateSupports();
            transferLongRangeForces(experiment);
            experiment->solver.iterations = step->currentIteration + 1;
        }
    }
}


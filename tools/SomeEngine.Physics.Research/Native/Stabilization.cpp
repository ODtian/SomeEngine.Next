#include "BridgeState.h"
#include "ResearchStep.h"

#ifdef DEMO_2D
static constexpr int maximumResearchRecovery = 1;
#else
static constexpr int maximumResearchRecovery = 3;
#endif

bool researchPostStabilize()
{
#ifdef DEMO_2D
    return researchStep->experiment->solver.postStabilize;
#else
    return researchStep->experiment->postStabilize;
#endif
}

int researchRecoveryLimit()
{
    if (!researchPostStabilize()) return 0;
    return researchStep->experiment->researchExtensions ? maximumResearchRecovery : 1;
}

static int readContactFeature(const Manifold::Contact& contact)
{
#ifdef DEMO_2D
    return contact.feature.value;
#else
    return contact.feature.key;
#endif
}

static float3x3 readContactBasis(Manifold* manifold, int point)
{
#ifdef DEMO_2D
    float2 normal = manifold->contacts[point].normal;
    return {normal.x, normal.y, 0, normal.y, -normal.x, 0, 0, 0, 1};
#else
    return manifold->basis;
#endif
}

void researchSaveStabilizationForces(Experiment* experiment)
{
    if (researchStep->stabilizationForcesSaved) return;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            auto* changes = findContactReactions(manifold);
            for (int i = 0; i < manifold->numContacts; ++i)
            {
                const auto& contact = manifold->contacts[i];
                float3x3 toWorld = readContactBasis(manifold, i);
#ifndef DEMO_2D
                toWorld = transpose(toWorld);
#endif
                float3 extra = changes == nullptr ? float3{0, 0, 0} : (*changes)[i];
#ifdef DEMO_2D
                float3 lambda{manifold->lambda[i * 2], manifold->lambda[i * 2 + 1], 0};
                float3 penalty{manifold->penalty[i * 2], manifold->penalty[i * 2 + 1], 0};
#else
                float3 lambda = contact.lambda, penalty = contact.penalty;
#endif
                experiment->stabilizationContacts.push_back({manifold, readContactFeature(contact),
                    toWorld * lambda, toWorld * extra, penalty, contact.stick});
            }
        }
        else if (auto* joint = dynamic_cast<Joint*>(force))
        {
#ifdef DEMO_2D
            StabilizationJoint state{joint};
            std::copy_n(joint->lambda, state.lambda.size(), state.lambda.begin());
            std::copy_n(joint->penalty, state.penalty.size(), state.penalty.begin());
            experiment->stabilizationJoints.push_back(state);
#else
            experiment->stabilizationJoints.push_back({joint, joint->lambdaLin, joint->lambdaAng,
                joint->penaltyLin, joint->penaltyAng, joint->broken});
#endif
        }
    }
    researchStep->stabilizationForcesSaved = true;
}

void researchSaveNewStabilizationContacts(Manifold* manifold)
{
    if (!researchStep->stabilizationForcesSaved) return;
    auto& states = researchStep->experiment->stabilizationContacts;
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        const auto& contact = manifold->contacts[point];
        int feature = readContactFeature(contact);
        auto saved = std::find_if(states.begin(), states.end(), [&](const StabilizationContact& state)
        { return state.manifold == manifold && state.feature == feature; });
        if (saved != states.end()) continue;
#ifdef DEMO_2D
        float3 penalty{manifold->penalty[point * 2], manifold->penalty[point * 2 + 1], 0};
#else
        float3 penalty = contact.penalty;
#endif
        states.push_back({manifold, feature, float3{0, 0, 0}, float3{0, 0, 0}, penalty, false});
    }
}

void restoreStabilizationForces(Experiment* experiment)
{
    auto& states = experiment->stabilizationContacts;
    if (!states.empty())
        for (auto* contact : experiment->reactionContacts) contact->reactions.fill(float3{0, 0, 0});
    for (const auto& state : states)
    {
        Manifold* manifold = state.manifold;
        for (int i = 0; i < manifold->numContacts; ++i)
        {
            auto& contact = manifold->contacts[i];
            if (readContactFeature(contact) != state.feature) continue;
            float3x3 basis = readContactBasis(manifold, i);
            float3 lambda = basis * state.worldForce;
#ifdef DEMO_2D
            manifold->lambda[i * 2] = lambda.x;
            manifold->lambda[i * 2 + 1] = lambda.y;
            manifold->penalty[i * 2] = state.penalty.x;
            manifold->penalty[i * 2 + 1] = state.penalty.y;
#else
            contact.lambda = lambda;
            contact.penalty = state.penalty;
#endif
            contact.stick = state.stick;
            if (lengthSq(state.worldExtra) > 0)
                editContactReactions(experiment, manifold)[i] = basis * state.worldExtra;
            break;
        }
    }
    states.clear();
    for (const auto& state : experiment->stabilizationJoints)
    {
#ifdef DEMO_2D
        std::copy(state.lambda.begin(), state.lambda.end(), state.joint->lambda);
        std::copy(state.penalty.begin(), state.penalty.end(), state.joint->penalty);
#else
        state.joint->lambdaLin = state.lambdaLin;
        state.joint->lambdaAng = state.lambdaAng;
        state.joint->penaltyLin = state.penaltyLin;
        state.joint->penaltyAng = state.penaltyAng;
        state.joint->broken = state.broken;
#endif
    }
    experiment->stabilizationJoints.clear();
}

static float readContactCorrectionForce(Manifold* manifold, int point)
{
    const auto& contact = manifold->contacts[point];
    Rigid* a = manifold->bodyA;
    Rigid* b = manifold->bodyB;
#ifdef DEMO_2D
    float2 normal = contact.normal;
    float2 ra = researchRotateContactPoint(a->position.z, contact.rA);
    float2 rb = researchRotateContactPoint(b->position.z, contact.rB);
    float gap = dot(normal, a->position.xy() - b->position.xy() + ra - rb);
    float ta = cross(ra, normal), tb = cross(rb, normal);
    float response = (a->mass > 0 ? 1 / a->mass + ta * ta / a->moment : 0)
        + (b->mass > 0 ? 1 / b->mass + tb * tb / b->moment : 0);
#else
    float3 normal = manifold->basis[0];
    float3 ra = rotate(a->positionAng, contact.rA);
    float3 rb = rotate(b->positionAng, contact.rB);
    float gap = dot(normal, a->positionLin - b->positionLin + ra - rb);
    float3 ta = cross(ra, normal), tb = cross(rb, normal);
    float response = a->mass > 0 ? 1 / a->mass : 0;
    response += b->mass > 0 ? 1 / b->mass : 0;
    for (int axis = 0; axis < 3; ++axis)
    {
        if (a->mass > 0) response += ta[axis] * ta[axis] / a->moment[axis];
        if (b->mass > 0) response += tb[axis] * tb[axis] / b->moment[axis];
    }
#endif
    float dt = researchStep->experiment->solver.dt;
    return std::max(0.0f, -gap) / (dt * dt * response);
}

static void preconditionStabilizationForce(Force* force)
{
#ifdef DEMO_2D
    auto* manifold = dynamic_cast<Manifold*>(force);
    auto* joint = dynamic_cast<Joint*>(force);
    for (int row = 0; row < force->rows(); ++row)
    {
        if (!isinf(force->stiffness[row])) continue;
        if (manifold && row % 2) continue;
        float accuracy = constraintAccuracy;
        if (joint && row == 2)
        {
            float radius = std::max(joint->bodyA ? joint->bodyA->radius : 0.0f, joint->bodyB->radius);
            accuracy *= joint->torqueArm / radius;
        }
        float reaction = force->lambda[row];
        if (manifold)
            reaction = std::max(fabsf(reaction + researchContactChange(manifold, row / 2).x),
                readContactCorrectionForce(manifold, row / 2));
        float stiffness = fabsf(reaction) / accuracy;
        force->penalty[row] = std::max(force->penalty[row], clamp(stiffness, PENALTY_MIN, PENALTY_MAX));
    }
#else
    if (auto* manifold = dynamic_cast<Manifold*>(force))
    {
        for (int i = 0; i < manifold->numContacts; ++i)
        {
            auto& contact = manifold->contacts[i];
            float reaction = contact.lambda.x + researchContactChange(manifold, i).x;
            float stiffness = std::max(fabsf(reaction), readContactCorrectionForce(manifold, i)) / constraintAccuracy;
            contact.penalty.x = std::max(contact.penalty.x, clamp(stiffness, PENALTY_MIN, PENALTY_MAX));
        }
    }
    else if (auto* joint = dynamic_cast<Joint*>(force))
    {
        if (joint->broken) return;
        if (isinf(joint->stiffnessLin))
            for (int axis = 0; axis < 3; ++axis)
            {
                float stiffness = fabsf(joint->lambdaLin[axis]) / constraintAccuracy;
                joint->penaltyLin[axis] = std::max(joint->penaltyLin[axis], clamp(stiffness, PENALTY_MIN, PENALTY_MAX));
            }
        if (isinf(joint->stiffnessAng))
        {
            float radius = std::max(joint->bodyA ? joint->bodyA->radius : 0.0f, joint->bodyB->radius);
            float angularTolerance = constraintAccuracy * joint->torqueArm / radius;
            for (int axis = 0; axis < 3; ++axis)
            {
                float stiffness = fabsf(joint->lambdaAng[axis]) / angularTolerance;
                joint->penaltyAng[axis] = std::max(joint->penaltyAng[axis], clamp(stiffness, PENALTY_MIN, PENALTY_MAX));
            }
        }
    }
#endif
}

static bool requiresJointStabilization(Experiment* experiment)
{
    if (experiment->joints.empty()) return false;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceSolves(force)) continue;
        auto* joint = dynamic_cast<Joint*>(force);
        if (!joint) continue;
#ifdef DEMO_2D
        if (!isinf(joint->stiffness[0])) continue;
        float2 pointA = joint->bodyA ? joint->bodyA->position.xy()
            + researchRotateContactPoint(joint->bodyA->position.z, joint->rA) : joint->rA;
        float2 pointB = joint->bodyB->position.xy() + researchRotateContactPoint(joint->bodyB->position.z, joint->rB);
#else
        if (joint->broken || !isinf(joint->stiffnessLin)) continue;
        float3 pointA = joint->bodyA ? transform(joint->bodyA->positionLin, joint->bodyA->positionAng, joint->rA) : joint->rA;
        float3 pointB = transform(joint->bodyB->positionLin, joint->bodyB->positionAng, joint->rB);
#endif
        if (length(pointA - pointB) > constraintAccuracy) return true;
    }
    return false;
}

static bool requiresStabilization(Experiment* experiment)
{
    if (requiresJointStabilization(experiment)) return true;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceSolves(force)) continue;
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        for (int i = 0; i < manifold->numContacts; ++i)
        {
            const auto& contact = manifold->contacts[i];
#ifdef DEMO_2D
            float2 pointA = manifold->bodyA->position.xy() + researchRotateContactPoint(manifold->bodyA->position.z, contact.rA);
            float2 pointB = manifold->bodyB->position.xy() + researchRotateContactPoint(manifold->bodyB->position.z, contact.rB);
            if (dot(contact.normal, pointA - pointB) < -constraintAccuracy) return true;
#else
            float3 pointA = transform(manifold->bodyA->positionLin, manifold->bodyA->positionAng, contact.rA);
            float3 pointB = transform(manifold->bodyB->positionLin, manifold->bodyB->positionAng, contact.rB);
            if (dot(manifold->basis[0], pointA - pointB) < -constraintAccuracy) return true;
#endif
        }
    }
    return false;
}

static void updateStabilizationForces(Experiment* experiment)
{
    researchSaveStabilizationForces(experiment);
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceSolves(force)) continue;
#ifdef DEMO_2D
        if (!dynamic_cast<Manifold*>(force) && !dynamic_cast<Joint*>(force)) continue;
        force->computeConstraint(0);
        for (int row = 0; row < force->rows(); ++row)
        {
            float lambda = isinf(force->stiffness[row]) ? force->lambda[row] : 0.0f;
            force->lambda[row] = clamp(force->penalty[row] * force->C[row] + lambda, force->fmin[row], force->fmax[row]);
        }
#else
        force->updateDual(0);
#endif
        preconditionStabilizationForce(force);
    }
}

bool researchInitializeStabilization()
{
    auto* experiment = researchStep->experiment;
    if (!experiment->researchExtensions) return true;
    if (!requiresStabilization(experiment))
    {
        experiment->lastStabilizationIterations = 0;
        return false;
    }
    researchSaveStabilizationForces(experiment);
    for (Rigid* body : experiment->bodies)
    {
        if (!researchBodySolves(body)) continue;
#ifdef DEMO_2D
        body->inertial = body->position;
#else
        body->inertialLin = body->positionLin;
        body->inertialAng = body->positionAng;
#endif
    }
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceSolves(force)) continue;
        preconditionStabilizationForce(force);
#ifdef DEMO_2D
        std::fill_n(force->lambda, force->rows(), 0.0f);
#else
        if (auto* manifold = dynamic_cast<Manifold*>(force))
            for (int point = 0; point < manifold->numContacts; ++point) manifold->contacts[point].lambda = {0, 0, 0};
        else if (auto* joint = dynamic_cast<Joint*>(force))
            joint->lambdaLin = joint->lambdaAng = {0, 0, 0};
#endif
    }
    for (auto* contact : experiment->reactionContacts) contact->reactions.fill(float3{0, 0, 0});
    return true;
}

void researchExtendContactSolve()
{
    // Reuse recovery passes to resolve contact impulses before rebuilding
    // velocities, while retaining one pass for the final position recovery.
    auto* step = researchStep;
    auto* experiment = step->experiment;
    if (experiment->researchExtensions && researchPostStabilize() && step->currentIteration == experiment->solver.iterations - 1
        && experiment->jointTree.contactBodies.empty()
        && step->additionalContactIterations < maximumResearchRecovery - 1 && requiresStabilization(experiment))
    {
        ++experiment->solver.iterations;
        ++step->additionalContactIterations;
    }
}

bool researchMoreStabilization()
{
    auto* experiment = researchStep->experiment;
    if (!experiment->researchExtensions) return false;
    if (researchAddMovedContacts()) researchClassifyJoints();
    if (experiment->longRange.enabled && (requiresJointStabilization(experiment)
        || (!experiment->jointTree.contactBodies.empty() && requiresStabilization(experiment))))
    {
        researchSaveStabilizationForces(experiment);
        researchSolveConstraints();
    }
    researchRefreshContacts();
    int recoveryIterations = maximumResearchRecovery - researchStep->additionalContactIterations;
    if (!experiment->longRange.enabled || experiment->lastStabilizationIterations == recoveryIterations || !requiresStabilization(experiment)) return false;
    updateStabilizationForces(experiment);
    return true;
}

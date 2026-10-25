/*
 * Joint block assembly adapted from Chris Giles, Copyright (c) 2026.
 * Permission to use, copy, modify, distribute and sell this software and its
 * documentation for any purpose is hereby granted without fee, provided that
 * the above copyright notice appear in all copies. Chris Giles makes no
 * representations about suitability for any purpose. It is provided "as is"
 * without express or implied warranty.
 */
#include "BridgeState.h"
#include "ResearchStep.h"
#ifndef DEMO_2D
#include <ppl.h>
#endif

#ifndef DEMO_2D
bool researchStructuralBody(Rigid* body)
{
    auto* experiment = researchStep->experiment;
    if (experiment->joints.empty()) return false;
    if (experiment->jointBodyVersion != experiment->jointVersion)
    {
        experiment->jointBodies.assign(experiment->bodies.size(), 0);
        for (Joint* joint : experiment->joints)
        {
            if (joint->bodyA) experiment->jointBodies[experiment->index(joint->bodyA)] = 1;
            experiment->jointBodies[experiment->index(joint->bodyB)] = 1;
        }
        experiment->jointBodiesOnly = true;
        for (int index = 0; index < (int)experiment->bodies.size(); ++index)
            if (experiment->bodies[index]->mass > 0 && !experiment->jointBodies[index])
            { experiment->jointBodiesOnly = false; break; }
        experiment->jointBodyVersion = experiment->jointVersion;
    }
    if (experiment->jointBodiesOnly && body && body->mass > 0) return true;
    return body && experiment->jointBodies[experiment->index(body)];
}

bool researchNewtonContact(Manifold* manifold)
{
    auto* experiment = researchStep->experiment;
    if (experiment->newton.compliant) return true;
    if (experiment->researchExtensions) return false;
    if (experiment->joints.empty()) return true;
    if (experiment->jointBodyVersion != experiment->jointVersion) researchStructuralBody(manifold->bodyA);
    return !experiment->jointBodiesOnly && !researchStructuralBody(manifold->bodyA) && !researchStructuralBody(manifold->bodyB);
}

void researchInitializeJoint(Joint* joint)
{
    auto* experiment = researchStep->experiment;
    if (!experiment->newton.compliant && !experiment->researchExtensions)
    {
        float stepSquared = joint->solver->dt * joint->solver->dt;
        float3 linearResponse{}, angularResponse{};
        for (Rigid* body : {joint->bodyA, joint->bodyB})
        {
            if (!body || body->mass <= 0) continue;
            float3 offset = rotate(body->positionAng, body == joint->bodyA ? joint->rA : joint->rB);
            for (int axis = 0; axis < 3; ++axis)
            {
                float3 direction{0, 0, 0}; direction[axis] = 1;
                float3 turn = cross(offset, direction);
                linearResponse[axis] += 1 / body->mass + turn.x * turn.x / body->moment.x
                    + turn.y * turn.y / body->moment.y + turn.z * turn.z / body->moment.z;
                angularResponse[axis] += joint->torqueArm * joint->torqueArm / body->moment[axis];
            }
        }
        for (int axis = 0; axis < 3; ++axis)
        {
            if (isinf(joint->stiffnessLin)) joint->penaltyLin[axis] = max(joint->penaltyLin[axis], 1 / (stepSquared * linearResponse[axis]));
            if (isinf(joint->stiffnessAng)) joint->penaltyAng[axis] = max(joint->penaltyAng[axis], 1 / (stepSquared * angularResponse[axis]));
        }
    }
    if (joint->stiffnessAng == 0) return;
    auto [entry, inserted] = researchStep->experiment->newton.joints.try_emplace(joint);
    if (inserted)
    {
        quat parent = joint->bodyA ? joint->bodyA->positionAng : quat{0, 0, 0, 1};
        entry->second.restRelative = inverse(parent) * joint->bodyB->positionAng;
    }
}

float3 researchJointRotationError(Joint* joint)
{
    quat parent = joint->bodyA ? joint->bodyA->positionAng : quat{0, 0, 0, 1};
    if (joint->stiffnessAng > 0) parent = parent * researchStep->experiment->newton.joints.at(joint).restRelative;
    return parent - joint->bodyB->positionAng;
}

AngularInertia researchAngularInertia(Rigid* body)
{
    if (!researchStep->experiment->researchExtensions && !researchStructuralBody(body)) return newtonAngularInertia(body);
    float3 difference = researchRotationDifference(body->positionAng, body->inertialAng);
    double v[3] = {difference.x * 0.5, difference.y * 0.5, difference.z * 0.5};
    const auto& a = body->positionAng;
    const auto& b = body->inertialAng;
    double norm = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
    double w = ((double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w) / norm;
    double jacobian[9] = {w, v[2], -v[1], -v[2], w, v[0], v[1], -v[0], w};
    AngularInertia result;
    for (int row = 0; row < 3; ++row)
        for (int axis = 0; axis < 3; ++axis)
        {
            result.gradient[row] += jacobian[axis * 3 + row] * body->moment[axis] * 2 * v[axis];
            for (int column = 0; column < 3; ++column)
                result.hessian[row * 3 + column] += jacobian[axis * 3 + row]
                    * body->moment[axis] * jacobian[axis * 3 + column];
        }
    return result;
}
#endif

#ifndef DEMO_2D
// Newton's GREEDY colors and assemble/commit stages, adapted to include the
// contact and LRC conflicts of Giles's hard-row blocks. Free components
// propagate contact response from the gravity-facing side of each island.

static void solveBodyBlock(int index, float alpha, float3& changeLin, float3& changeAng)
{
    Rigid* body = researchStep->bodies[index];
    float3 start = body->positionLin;
    quat rotation = body->positionAng;
    bool refined = false;
    for (int inner = 0; inner < bodyRefinementLimit; ++inner)
    {
        float3x3 linear{}, angular{}, cross{};
        float3 gradientLin{}, gradientAng{};
        researchBodyEquation(body, alpha, linear, angular, cross, gradientLin, gradientAng);
        BodySolve factor(linear, angular, cross);
        factor.solve(-gradientLin, -gradientAng, changeLin, changeAng);
        if (researchSupports()) researchSupport(index, factor, changeLin, changeAng);
        float motion = length(changeLin) + body->radius * length(changeAng);
        if (!refined && (motion <= constraintAccuracy || researchStructuralBody(body))) return;
        refined = true;
        body->positionLin += changeLin;
        body->positionAng = researchAdvanceRotation(body->positionAng, changeAng);
        researchBodyChanged(index);
        if (motion <= constraintAccuracy) break;
    }
    changeLin = body->positionLin - start;
    const quat& current = body->positionAng;
    if (current.x == rotation.x && current.y == rotation.y && current.z == rotation.z && current.w == rotation.w)
        changeAng = {0, 0, 0};
    else
    {
        quat relative = current * inverse(rotation);
        changeAng = relative.vec() * (2 / relative.w);
    }
    body->positionLin = start;
    body->positionAng = rotation;
    researchBodyChanged(index);
}

static bool solveRigidIslands(float alpha)
{
    auto* step = researchStep;
    auto* experiment = step->experiment;
    auto& islands = experiment->islands;
    if (!experiment->joints.empty() || !experiment->longRange.jointBounds.empty()) return false;
    // Islands share fixed bodies. Their dynamic states and support reactions
    // are disjoint; their support graph selects the load propagation order.
    auto solveIsland = [&](int group)
    {
        ResearchStep* previous = researchStep;
        researchStep = step;
        int root = islands.groupRoots[group], updates = 0;
        float maximumChangeSquared = 0;
        auto& bodiesInIsland = islands.bodyGroups[root];
        bool completeSupport = experiment->longRange.enabled && experiment->solver.gravity != 0;
        if (completeSupport)
            for (int body : bodiesInIsland) completeSupport &= experiment->supportGraph.bodySupported[body] != 0;
        if (completeSupport) std::sort(bodiesInIsland.begin(), bodiesInIsland.end(), std::greater<int>());
        else if (experiment->solver.gravity != 0)
            std::stable_sort(bodiesInIsland.begin(), bodiesInIsland.end(), [&](int a, int b)
            {
                float heightA = experiment->bodies[a]->positionLin.z;
                float heightB = experiment->bodies[b]->positionLin.z;
                return experiment->solver.gravity < 0 ? heightA < heightB : heightA > heightB;
            });
        for (int index : bodiesInIsland)
        {
            Rigid* body = step->bodies[index];
            if (!researchBodySolves(body)) continue;
            ++updates;
            float3 changeLin{}, changeAng{};
            solveBodyBlock(index, alpha, changeLin, changeAng);
            body->positionLin = body->positionLin + changeLin;
            body->positionAng = researchAdvanceRotation(body->positionAng, changeAng);
            maximumChangeSquared = max(maximumChangeSquared,
                2 * (lengthSq(changeLin) + body->radius * body->radius * lengthSq(changeAng)));
        }
        islands.bodyChangeSquared[root] = maximumChangeSquared;
        islands.updatedBodies[root] = updates;
        researchStep = previous;
    };
    if (step->bodyCount >= 128 && islands.groupRoots.size() > 1)
        concurrency::parallel_for(0, (int)islands.groupRoots.size(), solveIsland, concurrency::static_partitioner());
    else for (int group = 0; group < (int)islands.groupRoots.size(); ++group) solveIsland(group);
    for (int root : islands.groupRoots)
    {
        islands.bodyUpdates += islands.updatedBodies[root];
        step->maximumBodyChangeSquared = max(step->maximumBodyChangeSquared, islands.bodyChangeSquared[root]);
    }
    return true;
}

void researchUpdateRigidForces(float alpha)
{
    RESEARCH_TIME(phaseTime, Dual);
    auto* step = researchStep;
    auto& forces = step->experiment->rigidForces;
    auto update = [&](int first, int last)
    {
        ResearchStep* previous = researchStep;
        researchStep = step;
        for (int index = first; index < last; ++index)
        {
            const auto& term = forces[index];
            if (!researchForceSolves(term.force)) continue;
            if (term.contact && researchNewtonContact(term.contact)) researchUpdateContactPressure(term.contact, alpha);
            else term.force->updateDual(alpha);
        }
        researchStep = previous;
    };
    if (step->bodyCount < 128) { update(0, (int)forces.size()); return; }
    int batches = std::min(8, ((int)forces.size() + 63) / 64);
    concurrency::parallel_for(0, batches, [&](int batch)
    {
        update((int)forces.size() * batch / batches, (int)forces.size() * (batch + 1) / batches);
    }, concurrency::static_partitioner());
}

void researchSolveRigidColors(float alpha)
{
    RESEARCH_TIME(phaseTime, Primal);
    auto* experiment = researchStep->experiment;
    const auto& bodies = experiment->bodies;
    int count = (int)bodies.size();
    // Newton's typed rigid force adjacency, retaining Giles's force order.
    if (experiment->bodyForceContactVersion != experiment->contactVersion
        || experiment->bodyForceJointVersion != experiment->jointVersion)
    {
        experiment->bodyForces.resize(count);
        for (int index = 0; index < count; ++index)
        {
            auto& terms = experiment->bodyForces[index];
            terms.clear();
            Rigid* body = bodies[index];
            for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
            {
                if (auto* contact = dynamic_cast<Manifold*>(force))
                {
                    if (contact->numContacts > 0) terms.push_back({force, contact, nullptr});
                }
                else terms.push_back({force, nullptr, dynamic_cast<Joint*>(force)});
            }
        }
        experiment->rigidForces.clear();
        for (Force* force = experiment->solver.forces; force; force = force->next)
        {
            auto* contact = dynamic_cast<Manifold*>(force);
            if (!contact || contact->numContacts > 0) experiment->rigidForces.push_back({force, contact, nullptr});
        }
        experiment->bodyForceContactVersion = experiment->contactVersion;
        experiment->bodyForceJointVersion = experiment->jointVersion;
    }
#ifdef AVBD_PROFILE
    int contactPoints = 0;
    for (const auto& term : experiment->rigidForces) if (term.contact) contactPoints += term.contact->numContacts;
    RESEARCH_MAX(ContactPoints, contactPoints);
#endif
    if (experiment->solver.gravity == 0 && experiment->rigidForces.empty() && experiment->longRange.jointBounds.empty()) return;
    if (!experiment->joints.empty() && experiment->jointBodyVersion != experiment->jointVersion) researchStructuralBody(bodies[0]);
    if (solveRigidIslands(alpha)) return;
    if (experiment->bodyColorGroups.empty() || experiment->jointColorVersion != experiment->jointVersion
        || (experiment->colorContactVersion != experiment->contactVersion && (!experiment->joints.empty() || !experiment->longRange.jointBounds.empty())))
    {
        std::vector<std::vector<int>> adjacent(count);
        for (Force* force = experiment->solver.forces; force; force = force->next)
        {
            if (!force->bodyA) continue;
            if (auto* manifold = dynamic_cast<Manifold*>(force); manifold && manifold->numContacts == 0) continue;
            int a = experiment->index(force->bodyA), b = experiment->index(force->bodyB);
            adjacent[a].push_back(b); adjacent[b].push_back(a);
        }
        const auto& data = experiment->longRange;
        for (const auto& bound : data.jointBounds)
        {
            int a = data.jointAnchors[bound.anchorA].body, b = data.jointAnchors[bound.anchorB].body;
            if (a < 0 || b < 0 || a == b) continue;
            adjacent[a].push_back(b); adjacent[b].push_back(a);
        }
        std::vector<unsigned char> coupled(count);
        std::vector<int> connected;
        auto mark = [&](int body)
        {
            if (body >= 0 && bodies[body]->mass > 0 && !coupled[body])
            { coupled[body] = 1; connected.push_back(body); }
        };
        for (Joint* joint : experiment->joints)
        {
            mark(experiment->index(joint->bodyA)); mark(experiment->index(joint->bodyB));
        }
        for (const auto& bound : data.jointBounds)
        {
            mark(data.jointAnchors[bound.anchorA].body); mark(data.jointAnchors[bound.anchorB].body);
        }
        for (int next = 0; next < (int)connected.size(); ++next)
            for (int neighbor : adjacent[connected[next]]) mark(neighbor);
        std::vector<int> order(count), colors(count, -1);
        for (int body = 0; body < count; ++body) order[body] = body;
        std::stable_sort(order.begin(), order.end(), [&](int a, int b) { return adjacent[a].size() > adjacent[b].size(); });
        experiment->bodyColorGroups.clear();
        std::vector<unsigned char> used(count);
        for (int body : order)
        {
            std::fill(used.begin(), used.end(), 0);
            for (int neighbor : adjacent[body]) if (colors[neighbor] >= 0) used[colors[neighbor]] = 1;
            int color = 0;
            while (used[color]) ++color;
            colors[body] = color;
            if (color == (int)experiment->bodyColorGroups.size()) experiment->bodyColorGroups.emplace_back();
            if (bodies[body]->mass > 0 && coupled[body]) experiment->bodyColorGroups[color].push_back(body);
        }
        // Giles's sequential GS path for free contact components. Separate
        // one-body groups preserve its established high-mass-ratio schedule.
        for (int body = count - 1; body >= 0; --body)
            if (bodies[body]->mass > 0 && !coupled[body]) experiment->bodyColorGroups.push_back({body});
        experiment->bodyLinearChanges.resize(count);
        experiment->bodyAngularChanges.resize(count);
        experiment->bodyChanges.resize(count);
        auto& islands = experiment->islands;
        islands.colorGroups.resize(count);
        for (auto& groups : islands.colorGroups) groups.clear();
        std::vector<int> lastColor(count, -1);
        for (int color = 0; color < (int)experiment->bodyColorGroups.size(); ++color)
            for (int body : experiment->bodyColorGroups[color])
            {
                int island = islands.roots[body];
                auto& groups = islands.colorGroups[island];
                if (lastColor[island] != color) { groups.emplace_back(); lastColor[island] = color; }
                groups.back().push_back(body);
            }
        experiment->jointColorVersion = experiment->jointVersion;
        experiment->colorContactVersion = experiment->contactVersion;
    }
    RESEARCH_MAX(ColorGroups, experiment->bodyColorGroups.size());
    auto* step = researchStep;
    auto& islands = experiment->islands;
    auto solveGroups = [&](const std::vector<std::vector<int>>& groups, int& updates, float& maximumChangeSquared)
    {
        ResearchStep* previous = researchStep;
        researchStep = step;
        for (const auto& group : groups)
        {
            for (int index : group)
            {
                Rigid* body = bodies[index];
                bool active = researchBodySolves(body);
                experiment->bodyChanges[index] = active;
                if (!active) continue;
                ++updates;
                float3 changeLin{}, changeAng{};
                solveBodyBlock(index, alpha, changeLin, changeAng);
                maximumChangeSquared = max(maximumChangeSquared,
                    2 * (lengthSq(changeLin) + body->radius * body->radius * lengthSq(changeAng)));
                experiment->bodyLinearChanges[index] = changeLin;
                experiment->bodyAngularChanges[index] = changeAng;
                if (group.size() == 1)
                {
                    body->positionLin = body->positionLin + changeLin;
                    body->positionAng = researchAdvanceRotation(body->positionAng, changeAng);
                    researchBodyChanged(index);
                }
            }
            if (group.size() > 1) for (int index : group)
            {
                if (!experiment->bodyChanges[index]) continue;
                Rigid* body = bodies[index];
                body->positionLin = body->positionLin + experiment->bodyLinearChanges[index];
                body->positionAng = researchAdvanceRotation(body->positionAng, experiment->bodyAngularChanges[index]);
                researchBodyChanged(index);
            }
        }
        researchStep = previous;
    };
    if (count >= 128 && islands.groupRoots.size() > 1)
    {
        concurrency::parallel_for(0, (int)islands.groupRoots.size(), [&](int group)
        {
            int root = islands.groupRoots[group];
            int updates = 0; float maximumChangeSquared = 0;
            solveGroups(islands.colorGroups[root], updates, maximumChangeSquared);
            islands.updatedBodies[root] = updates;
            islands.bodyChangeSquared[root] = maximumChangeSquared;
        }, concurrency::static_partitioner());
        for (int root : islands.groupRoots)
        {
            islands.bodyUpdates += islands.updatedBodies[root];
            step->maximumBodyChangeSquared = max(step->maximumBodyChangeSquared, islands.bodyChangeSquared[root]);
        }
    }
    else solveGroups(experiment->bodyColorGroups, islands.bodyUpdates, step->maximumBodyChangeSquared);
}
#endif

#ifdef DEMO_2D
void researchBodyEquation(Rigid* rigid, float alpha, float3x3& hessian, float3& gradient)
{
    float stepSquared = rigid->solver->dt * rigid->solver->dt;
    hessian = diagonal(rigid->mass, rigid->mass, rigid->moment) / stepSquared;
    gradient = hessian * (rigid->position - rigid->inertial);
    for (Force* force = rigid->forces; force; force = force->bodyA == rigid ? force->nextA : force->nextB)
    {
        if (auto* manifold = dynamic_cast<Manifold*>(force); manifold && !researchStep->experiment->researchExtensions)
        {
            researchContactEquation(manifold, rigid, alpha, hessian, gradient);
            continue;
        }
        force->computeConstraint(alpha);
        force->computeDerivatives(rigid);
        bool contact = researchStep->experiment->researchExtensions && dynamic_cast<Manifold*>(force);
        for (int row = 0; row < force->rows(); ++row)
        {
            float lambda = isinf(force->stiffness[row]) ? force->lambda[row] : 0;
            float value = clamp(force->penalty[row] * force->C[row] + lambda, force->fmin[row], force->fmax[row]);
            float3x3 geometry = contact ? diagonal(0, 0, max(0.0f, force->H[row][2][2] * value))
                : diagonal(length(force->H[row].col(0)), length(force->H[row].col(1)), length(force->H[row].col(2))) * abs(value);
            gradient += force->J[row] * value;
            hessian += outer(force->J[row], force->J[row] * researchContactPenalty(force, row, value)) + geometry;
        }
    }
    researchPrimal(rigid, hessian, gradient);
}
#else
// Algebraic assembly of Giles joint.cpp::Joint::updatePrimal. The diagonal
// geometric stiffness uses the same column norms as diagonalize(H).
static void addJointEquation(Joint* joint, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& crossBlock, float3& gradientLin, float3& gradientAng)
{
    float sign = body == joint->bodyA ? 1.0f : -1.0f;
    if (lengthSq(joint->penaltyLin) > 0)
    {
        float3 rA = joint->bodyA ? rotate(joint->bodyA->positionAng, joint->rA) : joint->rA;
        float3 rB = rotate(joint->bodyB->positionAng, joint->rB);
        float3 residual = (joint->bodyA ? joint->bodyA->positionLin + rA : rA)
            - (joint->bodyB->positionLin + rB);
        if (isinf(joint->stiffnessLin)) residual -= joint->C0Lin * alpha;
        float3 force{joint->penaltyLin.x * residual.x + joint->lambdaLin.x,
            joint->penaltyLin.y * residual.y + joint->lambdaLin.y,
            joint->penaltyLin.z * residual.z + joint->lambdaLin.z};
        float3 r = body == joint->bodyA ? rA : rB;
        gradientLin += force * sign;
        gradientAng += cross(r, force) * sign;
        float product = dot(r * sign, force);
        for (int axis = 0; axis < 3; ++axis)
        {
            float3 direction{0, 0, 0}; direction[axis] = 1;
            float3 turn = cross(r, direction);
            linear[axis][axis] += joint->penaltyLin[axis];
            researchAddBodyMetric(angular, turn, joint->penaltyLin[axis]);
            crossBlock += outer(turn, direction) * joint->penaltyLin[axis];
            float3 column = r * (force[axis] * sign);
            column[axis] -= product;
            angular[axis][axis] += length(column);
        }
    }
    if (lengthSq(joint->penaltyAng) > 0)
    {
        float3 residual = researchJointRotationError(joint) * joint->torqueArm;
        if (isinf(joint->stiffnessAng)) residual -= joint->C0Ang * alpha;
        for (int axis = 0; axis < 3; ++axis)
        {
            float force = joint->penaltyAng[axis] * residual[axis] + joint->lambdaAng[axis];
            angular[axis][axis] += joint->penaltyAng[axis] * joint->torqueArm * joint->torqueArm;
            gradientAng[axis] += force * sign * joint->torqueArm;
        }
    }
}

void researchBodyEquation(Rigid* rigid, float alpha, float3x3& linear, float3x3& angular,
    float3x3& cross, float3& gradientLin, float3& gradientAng)
{
    int bodyIndex = researchStep->experiment->index(rigid);
    float stepSquared = rigid->solver->dt * rigid->solver->dt;
    AngularInertia inertia = researchAngularInertia(rigid);
    linear = diagonal(rigid->mass, rigid->mass, rigid->mass) / stepSquared;
    angular = {}; cross = {};
    gradientLin = linear * (rigid->positionLin - rigid->inertialLin);
    gradientAng = {};
    for (int row = 0; row < 3; ++row)
    {
        gradientAng[row] = (float)(inertia.gradient[row] / stepSquared);
        for (int column = 0; column < 3; ++column) angular[row][column] = (float)(inertia.hessian[row * 3 + column] / stepSquared);
    }
    if (!researchStep->experiment->researchExtensions)
    {
        for (const auto& term : researchStep->experiment->bodyForces[bodyIndex])
        {
            if (term.contact)
            {
                if (researchNewtonContact(term.contact))
                    researchContactEquation(term.contact, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
                else researchPairedContactEquation(term.contact, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
            }
            else if (term.joint) addJointEquation(term.joint, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
            else term.force->updatePrimal(rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
        }
    }
    else for (Force* force = rigid->forces; force; force = force->bodyA == rigid ? force->nextA : force->nextB)
    {
        if (auto* manifold = dynamic_cast<Manifold*>(force); manifold && researchNewtonContact(manifold))
            researchContactEquation(manifold, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
        else if (manifold && !researchStep->experiment->researchExtensions)
            researchPairedContactEquation(manifold, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
        else if (auto* joint = dynamic_cast<Joint*>(force); joint && !researchStep->experiment->researchExtensions)
            addJointEquation(joint, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
        else force->updatePrimal(rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
    }
    researchPrimal(bodyIndex, linear, angular, cross, gradientLin, gradientAng);
}
#endif

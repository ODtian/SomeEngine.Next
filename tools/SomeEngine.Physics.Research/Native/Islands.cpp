#include "BridgeState.h"
#include "ResearchStep.h"

// World-wide SI criteria, shared by every solver and scene.
static constexpr float sleepSpeed = 0.03f;
static constexpr float sleepDelay = 0.5f;
static constexpr float sleepError = constraintAccuracy;

static float3 readCollisionExtent(Experiment* experiment, Experiment::CollisionSweep& bounds)
{
    if (bounds.hasExtent) return bounds.extent;
    Rigid* body = experiment->bodies[bounds.body];
    auto& geometry = experiment->collisionExtents[bounds.body];
#ifdef DEMO_2D
    if (!geometry.valid || geometry.angle != body->position.z)
    {
        float cosine = fabsf(cosf(body->position.z)), sine = fabsf(sinf(body->position.z));
        geometry.extent = {(cosine * body->size.x + sine * body->size.y) * 0.5f,
            (sine * body->size.x + cosine * body->size.y) * 0.5f, 0};
        geometry.angle = body->position.z;
        geometry.valid = true;
    }
    float angularSpeed = fabsf(body->velocity.z);
#else
    quat rotation = body->positionAng;
    if (!geometry.valid || geometry.rotation.x != rotation.x || geometry.rotation.y != rotation.y
        || geometry.rotation.z != rotation.z || geometry.rotation.w != rotation.w)
    {
        float3 xHalfEdge = rotate(rotation, float3{body->size.x * 0.5f, 0, 0});
        float3 yHalfEdge = rotate(rotation, float3{0, body->size.y * 0.5f, 0});
        float3 zHalfEdge = rotate(rotation, float3{0, 0, body->size.z * 0.5f});
        geometry.extent = {fabsf(xHalfEdge.x) + fabsf(yHalfEdge.x) + fabsf(zHalfEdge.x),
            fabsf(xHalfEdge.y) + fabsf(yHalfEdge.y) + fabsf(zHalfEdge.y),
            fabsf(xHalfEdge.z) + fabsf(yHalfEdge.z) + fabsf(zHalfEdge.z)};
        geometry.rotation = rotation;
        geometry.valid = true;
    }
    float angularSpeed = length(body->velocityAng);
#endif
    float3 extent = geometry.extent;
    float padding = bounds.radius - body->radius;
    if (body->mass > 0) padding += angularSpeed * body->radius * experiment->solver.dt;
#ifdef DEMO_2D
    // The 2D predicted pose is evaluated with another swept contact test.
    padding *= 2;
#endif
    padding += COLLISION_MARGIN * 0.5f;
    bounds.extent = extent + float3{padding, padding, padding};
    bounds.hasExtent = true;
    return bounds.extent;
}

static void findCollisionPairs()
{
    RESEARCH_TIME(phaseTime, CandidatePairs);
    Experiment* experiment = researchStep->experiment;
    auto& sweep = experiment->collisionSweep;
    auto& pairs = experiment->collisionPairs;
    int count = (int)experiment->bodies.size();
    sweep.resize(count); pairs.clear();
    experiment->collisionExtents.resize(count);
    float3 minimum{INFINITY, INFINITY, INFINITY}, maximum{-INFINITY, -INFINITY, -INFINITY};
    for (int i = 0; i < count; ++i)
    {
        Rigid* body = experiment->bodies[i];
#ifdef DEMO_2D
        float3 center{body->position.x, body->position.y, 0};
#else
        float3 center = body->positionLin;
#endif
        sweep[i] = {i, center, body->radius + readBodyTravel(body)};
        readCollisionExtent(experiment, sweep[i]);
        for (int axis = 0; axis < 3; ++axis)
        {
            minimum[axis] = min(minimum[axis], center[axis]);
            maximum[axis] = max(maximum[axis], center[axis]);
        }
    }
    float3 spread = maximum - minimum;
    int axis = spread.y > spread.x ? 1 : 0;
    if (spread.z > spread[axis]) axis = 2;
    std::sort(sweep.begin(), sweep.end(), [axis](const auto& a, const auto& b)
    { return a.center[axis] - a.extent[axis] < b.center[axis] - b.extent[axis]; });
    for (int i = 0; i < count; ++i)
        for (int j = i + 1; j < count && sweep[j].center[axis] - sweep[j].extent[axis] <= sweep[i].center[axis] + sweep[i].extent[axis]; ++j)
        {
            RESEARCH_COUNT(SweepVisits, 1);
            int a = sweep[i].body, b = sweep[j].body;
            bool awakeA = experiment->bodies[a]->mass > 0 && !experiment->islands.sleeping[a];
            bool awakeB = experiment->bodies[b]->mass > 0 && !experiment->islands.sleeping[b];
            if (!awakeA && !awakeB) continue;
            float3 distance = sweep[i].center - sweep[j].center;
            float radius = sweep[i].radius + sweep[j].radius;
            if (dot(distance, distance) > radius * radius || experiment->collisionExclusions[a * count + b]) continue;
            if (researchContactExists(experiment->bodies[a], experiment->bodies[b])) continue;
            float3 extent = readCollisionExtent(experiment, sweep[i]) + readCollisionExtent(experiment, sweep[j]);
            if (fabsf(distance.x) > extent.x || fabsf(distance.y) > extent.y || fabsf(distance.z) > extent.z) continue;
            pairs.push_back({std::max(a, b), std::min(a, b)});
            RESEARCH_COUNT(CandidatePairs, 1);
        }
    // Preserve the original solver's pair order after spatial pruning.
    std::sort(pairs.begin(), pairs.end(), std::greater<std::pair<int, int>>());
}

static ContactManifold* createContact(Experiment* experiment, int a, int b)
{
    RESEARCH_TIME(phaseTime, ContactGeometry);
    Rigid* first = experiment->bodies[a];
    Rigid* second = experiment->bodies[b];
#ifndef DEMO_2D
    if (!experiment->newton.compliant && !experiment->researchExtensions)
    {
        Manifold::Contact contacts[8]{};
        float3x3 basis;
        bool structural = researchStructuralBody(first) || researchStructuralBody(second);
        researchStep->contactQueryDistance = structural ? .02f : 2 * newtonShapeGap;
        RESEARCH_COUNT(NarrowQueries, 1);
        int count = Manifold::collide(first, second, contacts, basis);
        researchStep->contactQueryDistance = 0;
        if (count == 0) return nullptr;
        auto* manifold = new ContactManifold(&experiment->solver, first, second, a, b);
        std::copy(contacts, contacts + count, manifold->contacts);
        manifold->basis = basis;
        manifold->detectedCount = count;
        return manifold;
    }
#endif
    return new ContactManifold(&experiment->solver, first, second, a, b);
}

void researchDetectCollisions()
{
    findCollisionPairs();
    Experiment* experiment = researchStep->experiment;
    for (auto [a, b] : experiment->collisionPairs) createContact(experiment, a, b);
}

// A coupled correction can close a surface gap beyond the original free-motion
// candidates. These contacts join the same world before the block is committed.
bool researchAddContacts()
{
    findCollisionPairs();
    Experiment* experiment = researchStep->experiment;
    bool added = false;
    for (auto [a, b] : experiment->collisionPairs)
    {
        auto* manifold = createContact(experiment, a, b);
        if (!manifold) continue;
        bool newtonContacts = experiment->newton.compliant;
#ifndef DEMO_2D
        newtonContacts |= !experiment->researchExtensions;
#endif
        bool active = newtonContacts ? initializeNewtonContact(manifold, true) : researchInitializeCurrentContacts(manifold);
        if (!active) { researchRemoveForce(manifold); delete manifold; continue; }
        if (newtonContacts)
        {
            added = true;
            continue;
        }
#ifdef DEMO_2D
        float initialSeparation = experiment->researchExtensions ? 0 : readBoxSeparation(manifold->bodyA, manifold->bodyB,
            manifold->bodyA->initial, manifold->bodyB->initial);
#else
        float initialSeparation = experiment->researchExtensions ? -INFINITY : readBoxSeparation(manifold->bodyA, manifold->bodyB,
            manifold->bodyA->initialLin, manifold->bodyA->initialAng, manifold->bodyB->initialLin, manifold->bodyB->initialAng);
#endif
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            auto& contact = manifold->contacts[point];
#ifdef DEMO_2D
            float2 delta = manifold->bodyA->initial.xy() - manifold->bodyB->initial.xy()
                + researchRotateContactPoint(manifold->bodyA->initial.z, contact.rA)
                - researchRotateContactPoint(manifold->bodyB->initial.z, contact.rB);
            contact.C0 = {min(0.0f, max(dot(contact.normal, delta), initialSeparation)),
                dot(float2{contact.normal.y, -contact.normal.x}, delta)};
#else
            float3 delta = transform(manifold->bodyA->initialLin, manifold->bodyA->initialAng, contact.rA)
                - transform(manifold->bodyB->initialLin, manifold->bodyB->initialAng, contact.rB);
            contact.C0 = manifold->basis * delta;
            if (!experiment->researchExtensions) contact.C0.x = min(0.0f, max(contact.C0.x, initialSeparation));
#endif
        }
        researchSaveNewStabilizationContacts(manifold);
        added = true;
    }
    if (added) experiment->supportGraph.manifoldCount = -1;
    return added;
}

void researchBeginCollisionMotion()
{
    Experiment* experiment = researchStep->experiment;
    if (!experiment->longRange.enabled && !experiment->newton.compliant) return;
    auto& poses = experiment->collisionPoses;
    poses.resize(experiment->bodies.size());
    bool rigidContacts = !experiment->newton.compliant && !experiment->researchExtensions;
    for (int index = 0; index < (int)poses.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
#ifdef DEMO_2D
        float3 pose = rigidContacts ? body->position : body->inertial;
        poses[index] = {{pose.x, pose.y, 0}, pose.z};
#else
        poses[index] = {rigidContacts ? body->initialLin : body->inertialLin,
            rigidContacts ? body->initialAng : body->inertialAng};
#endif
    }
}

bool researchAddMovedContacts()
{
    Experiment* experiment = researchStep->experiment;
    if (!experiment->longRange.enabled && !experiment->newton.compliant) return false;
    auto& poses = experiment->collisionPoses;
    bool moved = false;
    for (int index = 0; index < (int)poses.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (!researchBodyAwake(body)) continue;
        const auto& pose = poses[index];
#ifdef DEMO_2D
        float travel = length(body->position.xy() - float2{pose.center.x, pose.center.y})
            + fabsf(body->position.z - pose.angle) * body->radius;
#else
        float travel = length(body->positionLin - pose.center)
            + length(researchRotationDifference(body->positionAng, pose.rotation)) * body->radius;
#endif
        if (travel > COLLISION_MARGIN * 0.5f) { moved = true; break; }
    }
    if (!moved) return false;
    if (experiment->newton.compliant) ++experiment->newton.collisionPasses;
    Force* firstExistingForce = experiment->solver.forces;
    bool added = researchAddContacts();
    for (int index = 0; index < (int)poses.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
#ifdef DEMO_2D
        poses[index] = {{body->position.x, body->position.y, 0}, body->position.z};
#else
        poses[index] = {body->positionLin, body->positionAng};
#endif
    }
    if (added)
    {
        researchBuildIslands();
        auto& islands = experiment->islands;
        std::fill(islands.ready.begin(), islands.ready.end(), 1);
        for (Force* force = experiment->solver.forces; force != firstExistingForce; force = force->next)
            for (Rigid* body : {force->bodyA, force->bodyB})
                if (body->mass > 0) islands.ready[islands.roots[experiment->index(body)]] = 0;
        for (int index = 0; index < (int)experiment->bodies.size(); ++index)
            if (!islands.ready[islands.roots[index]]) islands.directContacts[index] = 0;
    }
    return added;
}

void initializeIslands(Experiment* experiment)
{
    auto& islands = experiment->islands;
    int count = (int)experiment->bodies.size();
    if ((int)islands.roots.size() == count) return;
    islands.roots.resize(count);
    islands.sleeping.assign(count, 0);
    islands.quietSeconds.assign(count, 0);
    islands.awake.resize(count);
    islands.ready.resize(count);
    islands.sizes.resize(count);
    islands.directContacts.assign(count, 0);
    islands.contactVelocities.resize(count);
    for (int body = 0; body < count; ++body) islands.roots[body] = body;
}

static int root(Experiment::Islands& islands, int body)
{
    while (islands.roots[body] != body)
    {
        islands.roots[body] = islands.roots[islands.roots[body]];
        body = islands.roots[body];
    }
    return body;
}

bool researchBodyAwake(Rigid* body)
{
    return body && body->mass > 0 && (!researchStep->experiment->islands.sleepEnabled
        || !researchStep->experiment->islands.sleeping[researchStep->experiment->index(body)]);
}

bool researchForceAwake(Force* force)
{
    return researchBodyAwake(force->bodyA) || researchBodyAwake(force->bodyB);
}

bool researchBodySolves(Rigid* body)
{
    if (!researchBodyAwake(body)) return false;
    auto* experiment = researchStep->experiment;
#ifndef DEMO_2D
    // A zero-gravity free body's predicted pose already minimizes its inertial
    // block. The first attached force makes it participate in AVBD again.
    if (!experiment->newton.compliant && !experiment->researchExtensions && experiment->solver.gravity == 0
        && !body->forces && experiment->longRange.jointRangeCount == 0) return false;
    if (!experiment->newton.compliant && !experiment->researchExtensions && experiment->solver.gravity == 0
        && experiment->longRange.jointRangeCount == 0 && lengthSq(body->positionLin - body->inertialLin) == 0
        && body->positionAng.x == body->inertialAng.x && body->positionAng.y == body->inertialAng.y
        && body->positionAng.z == body->inertialAng.z && body->positionAng.w == body->inertialAng.w)
    {
        bool active = false;
        for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
        {
            auto* manifold = dynamic_cast<Manifold*>(force);
            if (!manifold || researchContactTouches(manifold)) { active = true; break; }
        }
        if (!active) return false;
    }
#endif
    return !experiment->researchExtensions || !experiment->islands.directContacts[experiment->index(body)];
}

bool researchForceSolves(Force* force)
{
    return researchBodySolves(force->bodyA) || researchBodySolves(force->bodyB);
}

void wakeBody(Experiment* experiment, int body)
{
    initializeIslands(experiment);
    if (experiment->bodies[body]->mass <= 0)
    {
        Rigid* rigid = experiment->bodies[body];
        for (Force* force = rigid->forces; force; force = force->bodyA == rigid ? force->nextA : force->nextB)
        {
            Rigid* other = force->bodyA == rigid ? force->bodyB : force->bodyA;
            if (other && other->mass > 0) wakeBody(experiment, experiment->index(other));
        }
        return;
    }
    auto& islands = experiment->islands;
    int selected = islands.roots[body];
    for (int index = 0; index < (int)islands.roots.size(); ++index)
        if (islands.roots[index] == selected)
        {
            islands.sleeping[index] = 0;
            islands.quietSeconds[index] = 0;
        }
}

void wakeAllBodies(Experiment* experiment)
{
    initializeIslands(experiment);
    std::fill(experiment->islands.sleeping.begin(), experiment->islands.sleeping.end(), 0);
    std::fill(experiment->islands.quietSeconds.begin(), experiment->islands.quietSeconds.end(), 0.0f);
}

bool researchInitializeForce(Force* force)
{
    if (researchForceAwake(force)) return force->initialize();
    researchStep->experiment->islands.deferredForces.push_back(force);
    return true;
}

void researchBuildIslands()
{
    Experiment* experiment = researchStep->experiment;
    auto& islands = experiment->islands;
    int count = (int)experiment->bodies.size();
    for (int body = 0; body < count; ++body) islands.roots[body] = body;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!force->bodyA || force->bodyA->mass <= 0 || force->bodyB->mass <= 0) continue;
        if (auto* manifold = dynamic_cast<Manifold*>(force))
            if (manifold->numContacts == 0) continue;
        int a = root(islands, experiment->index(force->bodyA));
        int b = root(islands, experiment->index(force->bodyB));
        islands.roots[b] = a;
    }
    for (const auto& bound : experiment->longRange.jointBounds)
    {
        int a = experiment->longRange.jointAnchors[bound.anchorA].body;
        int b = experiment->longRange.jointAnchors[bound.anchorB].body;
        if (a < 0 || b < 0 || experiment->bodies[a]->mass <= 0 || experiment->bodies[b]->mass <= 0) continue;
        islands.roots[root(islands, b)] = root(islands, a);
    }
    std::fill(islands.awake.begin(), islands.awake.end(), 0);
    std::fill(islands.sizes.begin(), islands.sizes.end(), 0);
    for (int body = 0; body < count; ++body)
    {
        islands.roots[body] = root(islands, body);
        if (experiment->bodies[body]->mass > 0) ++islands.sizes[islands.roots[body]];
        if (experiment->bodies[body]->mass > 0 && !islands.sleeping[body]) islands.awake[islands.roots[body]] = 1;
    }
    for (int body = 0; body < count; ++body)
        if (islands.sleeping[body] && islands.awake[islands.roots[body]])
        {
            islands.sleeping[body] = 0;
            islands.quietSeconds[body] = 0;
        }
    RESEARCH_MAX(Islands, std::count_if(islands.sizes.begin(), islands.sizes.end(), [](int size) { return size > 0; }));
    RESEARCH_MAX(LargestIsland, *std::max_element(islands.sizes.begin(), islands.sizes.end()));
    islands.supportEquations.resize(count);
#ifndef DEMO_2D
    islands.bodyGroups.resize(count);
    islands.updatedBodies.resize(count);
    islands.bodyChangeSquared.resize(count);
    islands.groupRoots.clear();
    for (auto& group : islands.bodyGroups) group.clear();
    for (int body = count - 1; body >= 0; --body)
    {
        if (experiment->bodies[body]->mass <= 0) continue;
        int group = islands.roots[body];
        if (islands.bodyGroups[group].empty()) islands.groupRoots.push_back(group);
        islands.bodyGroups[group].push_back(body);
    }
    if (experiment->solver.gravity != 0 && experiment->joints.empty() && experiment->longRange.jointBounds.empty())
        for (int group : islands.groupRoots)
            std::stable_sort(islands.bodyGroups[group].begin(), islands.bodyGroups[group].end(), [&](int a, int b)
            {
                float heightA = experiment->bodies[a]->positionLin.z;
                float heightB = experiment->bodies[b]->positionLin.z;
                return experiment->solver.gravity < 0 ? heightA < heightB : heightA > heightB;
            });
#endif
    for (Force* force : islands.deferredForces)
        if (researchForceAwake(force)) force->initialize();
    islands.deferredForces.clear();
}

static float3 point(Rigid* body, float3 local)
{
    if (!body) return local;
#ifdef DEMO_2D
    float2 rotated = rotate(body->position.z, local.xy());
    return {body->position.x + rotated.x, body->position.y + rotated.y, 0};
#else
    return transform(body->positionLin, body->positionAng, local);
#endif
}

// Free islands exchange only internal forces. Project the reconstructed linear
// velocities and center travel using the original pre-step velocities.
void preserveIslandCenterMotion(Experiment* experiment)
{
    auto& islands = experiment->islands;
    std::fill(islands.ready.begin(), islands.ready.end(), 1);
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (force->bodyA && force->bodyA->mass > 0 && force->bodyB->mass > 0) continue;
        for (Rigid* body : {force->bodyA, force->bodyB})
            if (body && body->mass > 0) islands.ready[islands.roots[experiment->index(body)]] = 0;
    }
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
        if (islands.directContacts[index]) islands.ready[islands.roots[index]] = 0;
    bool any = false;
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
        any |= researchBodyAwake(experiment->bodies[index]) && islands.ready[islands.roots[index]];
    if (!any) return;
    islands.momentum.assign(experiment->bodies.size(), {});
    double dt = experiment->solver.dt;
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (!researchBodyAwake(body) || !islands.ready[islands.roots[index]]) continue;
        auto& momentum = islands.momentum[islands.roots[index]];
        momentum.mass += body->mass;
#ifdef DEMO_2D
        float3 velocity{body->velocity.x, body->velocity.y, 0};
        float3 previous{body->prevVelocity.x, body->prevVelocity.y, 0};
        float3 position{body->position.x, body->position.y, 0};
        float3 initial{body->initial.x, body->initial.y, 0};
        float3 gravity{0, experiment->solver.gravity, 0};
#else
        float3 velocity = body->velocityLin, previous = body->prevVelocityLin;
        float3 position = body->positionLin, initial = body->initialLin;
        float3 gravity{0, 0, experiment->solver.gravity};
#endif
        for (int axis = 0; axis < 3; ++axis)
        {
            momentum.linearMomentum[axis] += body->mass * (double)velocity[axis];
            momentum.inertialMomentum[axis] += body->mass * ((double)previous[axis] + dt * gravity[axis]);
            momentum.positionDifference[axis] += body->mass * ((double)initial[axis]
                + dt * ((double)previous[axis] + dt * gravity[axis]) - position[axis]);
        }
    }
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (!researchBodyAwake(body) || !islands.ready[islands.roots[index]]) continue;
        const auto& momentum = islands.momentum[islands.roots[index]];
#ifdef DEMO_2D
        for (int axis = 0; axis < 2; ++axis)
        {
            body->velocity[axis] += (float)((momentum.inertialMomentum[axis] - momentum.linearMomentum[axis]) / momentum.mass);
            body->position[axis] += (float)(momentum.positionDifference[axis] / momentum.mass);
        }
#else
        for (int axis = 0; axis < 3; ++axis)
        {
            body->velocityLin[axis] += (float)((momentum.inertialMomentum[axis] - momentum.linearMomentum[axis]) / momentum.mass);
            body->positionLin[axis] += (float)(momentum.positionDifference[axis] / momentum.mass);
        }
#endif
    }
}

void finishIslandStep(Experiment* experiment)
{
    auto& islands = experiment->islands;
    int count = (int)experiment->bodies.size();
    std::fill(islands.ready.begin(), islands.ready.end(), 1);
    for (int index = 0; index < count; ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (body->mass <= 0 || islands.sleeping[index]) continue;
#ifdef DEMO_2D
        float speed = length(body->velocity.xy()) + fabsf(body->velocity.z) * body->radius;
        float movement = length((body->position - body->initial).xy()) + fabsf(body->position.z - body->initial.z) * body->radius;
#else
        float speed = length(body->velocityLin) + length(body->velocityAng) * body->radius;
        float movement = length(body->positionLin - body->initialLin) + length(body->positionAng - body->initialAng) * body->radius;
#endif
        if (speed > sleepSpeed || movement > sleepSpeed * experiment->solver.dt) islands.ready[islands.roots[index]] = 0;
    }
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceAwake(force)) continue;
        float error = 0;
        if (auto* joint = dynamic_cast<Joint*>(force))
        {
#ifdef DEMO_2D
            error = length(point(joint->bodyA, {joint->rA.x, joint->rA.y, 0}) - point(joint->bodyB, {joint->rB.x, joint->rB.y, 0}));
#else
            error = length(point(joint->bodyA, joint->rA) - point(joint->bodyB, joint->rB));
#endif
        }
        else if (auto* manifold = dynamic_cast<Manifold*>(force))
            for (int i = 0; i < manifold->numContacts; ++i)
            {
                const auto& contact = manifold->contacts[i];
#ifdef DEMO_2D
                float3 normal{contact.normal.x, contact.normal.y, 0};
                float3 a = point(force->bodyA, {contact.rA.x, contact.rA.y, 0}), b = point(force->bodyB, {contact.rB.x, contact.rB.y, 0});
#else
                float3 normal = manifold->basis[0];
                float3 a = point(force->bodyA, contact.rA), b = point(force->bodyB, contact.rB);
#endif
                error = std::max(error, -dot(normal, a - b));
            }
        if (error > sleepError)
            for (Rigid* body : {force->bodyA, force->bodyB})
                if (body && body->mass > 0) islands.ready[islands.roots[experiment->index(body)]] = 0;
    }
    for (int body = 0; body < count; ++body)
    {
        if (experiment->bodies[body]->mass <= 0 || islands.sleeping[body]) continue;
        islands.quietSeconds[body] = islands.ready[islands.roots[body]] ? islands.quietSeconds[body] + experiment->solver.dt : 0;
    }
    for (int body = 0; body < count; ++body)
        if (experiment->bodies[body]->mass > 0 && !islands.sleeping[body] && islands.quietSeconds[body] < sleepDelay)
            islands.ready[islands.roots[body]] = 0;
    if (islands.sleepEnabled)
        for (int body = 0; body < count; ++body)
        {
            Rigid* rigid = experiment->bodies[body];
            if (rigid->mass <= 0 || !islands.ready[islands.roots[body]]) continue;
            islands.sleeping[body] = 1;
#ifdef DEMO_2D
            rigid->velocity = rigid->prevVelocity = {0, 0, 0};
#else
            rigid->velocityLin = rigid->prevVelocityLin = rigid->velocityAng = {0, 0, 0};
#endif
        }
    // At rest, original contact multipliers retain the entire support reaction.
    for (int reaction = 0; reaction < (int)experiment->reactionContacts.size();)
    {
        auto* manifold = experiment->reactionContacts[reaction];
        if (researchForceAwake(manifold)) { ++reaction; continue; }
        for (int i = 0; i < manifold->numContacts; ++i)
        {
#ifdef DEMO_2D
            manifold->lambda[i * 2] += manifold->reactions[i].x;
            manifold->lambda[i * 2 + 1] += manifold->reactions[i].y;
#else
            manifold->contacts[i].lambda += manifold->reactions[i];
#endif
        }
        experiment->contactForces.erase(manifold);
        clearContactReactions(experiment, manifold);
    }
    islands.activeCount = islands.sleepingCount = 0;
    std::fill(islands.awake.begin(), islands.awake.end(), 0);
    for (int body = 0; body < count; ++body)
        if (experiment->bodies[body]->mass > 0)
        {
            islands.sleepingCount += islands.sleeping[body];
            islands.awake[islands.roots[body]] |= !islands.sleeping[body];
        }
    for (unsigned char active : islands.awake) islands.activeCount += active;
}

#include <algorithm>
#include <vector>
#include <unordered_map>
#include <cstring>
#include "solver.h"
#include "scenes.h"
#include "ResearchStep.h"
#include "BridgeState.h"


#define EXPORT extern "C" __declspec(dllexport)

thread_local ResearchStep* researchStep = nullptr;

static void readBodies(Experiment* experiment, BodyData* output);

// Box overlap uses the separating axes themselves. Clipped witness depths also
// contain the manifold's face choice and describe a different geometric value.
static float readBoxGap(Rigid* a, Rigid* b, float3& normal)
{
#ifdef DEMO_2D
    float2x2 ra = rotation(a->position.z), rb = rotation(b->position.z);
    float3 axesA[2]{{ra[0][0], ra[1][0], 0}, {ra[0][1], ra[1][1], 0}};
    float3 axesB[2]{{rb[0][0], rb[1][0], 0}, {rb[0][1], rb[1][1], 0}};
    float3 delta{b->position.x - a->position.x, b->position.y - a->position.y, 0};
    constexpr int count = 2;
#else
    float3 axesA[3]{rotate(a->positionAng, {1, 0, 0}), rotate(a->positionAng, {0, 1, 0}), rotate(a->positionAng, {0, 0, 1})};
    float3 axesB[3]{rotate(b->positionAng, {1, 0, 0}), rotate(b->positionAng, {0, 1, 0}), rotate(b->positionAng, {0, 0, 1})};
    float3 delta = b->positionLin - a->positionLin;
    constexpr int count = 3;
#endif
    float separation = -INFINITY;
    auto axisGap = [&](float3 axis)
    {
        float gap = fabsf(dot(delta, axis));
        for (int i = 0; i < count; ++i)
            gap -= .5f * (a->size[i] * fabsf(dot(axesA[i], axis)) + b->size[i] * fabsf(dot(axesB[i], axis)));
        if (gap > separation)
        {
            separation = gap;
            normal = axis * (dot(delta, axis) >= 0 ? -1.0f : 1.0f);
        }
    };
    for (int i = 0; i < count; ++i) { axisGap(axesA[i]); axisGap(axesB[i]); }
#ifndef DEMO_2D
    for (int i = 0; i < count; ++i)
        for (int j = 0; j < count; ++j)
        {
            float3 axis = cross(axesA[i], axesB[j]);
            float magnitude = length(axis);
            if (magnitude > 1.e-6f) axisGap(axis / magnitude);
        }
#endif
    return separation;
}

float readBodyTravel(Rigid* body)
{
    // ModelBuilder.rigid_gap is 0.1 m per shape; NarrowPhase combines the
    // endpoints' gaps. This expands candidate queries at the collide pose.
    if (researchStep && researchStep->experiment->newton.compliant) return newtonShapeGap;
#ifndef DEMO_2D
    if (researchStep && !researchStep->experiment->researchExtensions && !researchStructuralBody(body)) return newtonShapeGap;
#endif
    if (body->mass <= 0) return 0;
    float dt = body->solver->dt;
#ifdef DEMO_2D
    float speed = length(body->velocity.xy());
#else
    float speed = length(body->velocityLin);
#endif
    return dt * (speed + fabsf(body->solver->gravity) * dt);
}

float researchContactDistance()
{
    if (researchStep && researchStep->contactQueryDistance > 0) return researchStep->contactQueryDistance;
    return researchStep && researchStep->experiment->newton.compliant ? 2 * newtonShapeGap : COLLISION_MARGIN;
}

#ifdef DEMO_2D
int readPredictedContacts(Rigid* bodyA, Rigid* bodyB, Manifold::Contact* contacts)
#else
int readPredictedContacts(Rigid* bodyA, Rigid* bodyB, Manifold::Contact* contacts, float3x3& basis)
#endif
{
#ifdef DEMO_2D
    int count = Manifold::collide(bodyA, bodyB, contacts);
#else
    int count = Manifold::collide(bodyA, bodyB, contacts, basis);
#endif
    if (count || researchStep->experiment->newton.compliant || researchStep->contactQueryDistance > 0) return count;
    float dt = bodyA->solver->dt;
#ifdef DEMO_2D
    float3 positionA = bodyA->position, positionB = bodyB->position;
    for (Rigid* body : {bodyA, bodyB})
        if (body->mass > 0)
        {
            float omega = !researchStep->experiment->newton.compliant ? clamp(body->velocity.z, -50.0f, 50.0f) : body->velocity.z;
            body->position.x += body->velocity.x * dt; body->position.y += body->velocity.y * dt;
            body->position.z = !researchStep->experiment->newton.compliant ? body->position.z + omega * dt
                : newtonPlanarAdvance(body->position.z, omega * dt);
            body->position.y += body->solver->gravity * dt * dt;
        }
    count = Manifold::collide(bodyA, bodyB, contacts);
    bodyA->position = positionA; bodyB->position = positionB;
#else
    float3 positionA = bodyA->positionLin, positionB = bodyB->positionLin;
    quat rotationA = bodyA->positionAng, rotationB = bodyB->positionAng;
    for (Rigid* body : {bodyA, bodyB})
        if (body->mass > 0)
        {
            body->positionLin += body->velocityLin * dt + float3{0, 0, body->solver->gravity * dt * dt};
            float3 omega = !researchStep->experiment->newton.compliant ? body->velocityAng : newtonAngularPrediction(body);
            body->positionAng = researchAdvanceRotation(body->positionAng, omega * dt);
        }
    count = Manifold::collide(bodyA, bodyB, contacts, basis);
    bodyA->positionLin = positionA; bodyB->positionLin = positionB;
    bodyA->positionAng = rotationA; bodyB->positionAng = rotationB;
#endif
    return count;
}

EXPORT Experiment* demo_create(int scene, int iterations)
{
    auto* experiment = new Experiment();
    if (scene >= 100 && scene <= 102)
    {
        for (int i = 0; i < 20; ++i)
        {
#ifdef DEMO_2D
            auto* body = new Rigid(&experiment->solver, {1, 0.5f}, scene != 100 && i == 0 ? 0.0f : 1.0f, 0.5f, {(float)i, 10, 0});
            if (i) new Joint(&experiment->solver, body->next, body, {0.5f, 0}, {-0.5f, 0}, {INFINITY, INFINITY, 0});
#else
            auto* body = new Rigid(&experiment->solver, {1, 0.5f, 0.5f}, scene != 100 && i == 0 ? 0.0f : 1.0f, 0.5f, {(float)i, 0, 10});
            if (i) new Joint(&experiment->solver, body->next, body, {0.5f, 0, 0}, {-0.5f, 0, 0});
#endif
        }
    }
#ifndef DEMO_2D
    else if (scene == 103)
    {
        std::vector<Rigid*> spine;
        for (int i = 0; i < 8; ++i)
        {
            auto* body = new Rigid(&experiment->solver, {1, 0.3f, 0.3f}, i == 0 ? 0.0f : 1.0f, 0.5f, {(float)i, 0, 10});
            if (i) new Joint(&experiment->solver, spine.back(), body, {0.5f, 0, 0}, {-0.5f, 0, 0});
            spine.push_back(body);
        }
        Rigid* previous = spine[3];
        for (int i = 0; i < 4; ++i)
        {
            auto* body = new Rigid(&experiment->solver, {0.3f, 1, 0.3f}, 1, 0.5f, {3, 0.65f + i, 10});
            new Joint(&experiment->solver, previous, body, i == 0 ? float3{0, 0.15f, 0} : float3{0, 0.5f, 0}, {0, -0.5f, 0});
            previous = body;
        }
        previous = spine[5];
        for (int i = 0; i < 4; ++i)
        {
            auto* body = new Rigid(&experiment->solver, {0.3f, 0.3f, 1}, 1, 0.5f, {5, 0, 9.35f - i});
            new Joint(&experiment->solver, previous, body, i == 0 ? float3{0, 0, -0.15f} : float3{0, 0, -0.5f}, {0, 0, 0.5f});
            previous = body;
        }
    }
#endif
    else scenes[scene == 104 ? 4 : scene](&experiment->solver);
    experiment->solver.iterations = iterations;
#ifdef DEMO_2D
    // Each compared method selects its 2D stabilization after creation.
    experiment->solver.postStabilize = false;
#endif
    for (Rigid* body = experiment->solver.bodies; body; body = body->next)
        experiment->bodies.push_back(body);
    std::reverse(experiment->bodies.begin(), experiment->bodies.end());
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
        experiment->indices[experiment->bodies[i]] = i;
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* joint = dynamic_cast<Joint*>(force)) experiment->joints.push_back(joint);
    std::reverse(experiment->joints.begin(), experiment->joints.end());
    if (scene == 104)
        for (int i = 1; i < (int)experiment->bodies.size(); ++i)
        {
            Rigid* body = experiment->bodies[i];
            Rigid* ground = experiment->bodies[0];
#ifdef DEMO_2D
            int layer = (int)roundf(body->position.y / 0.85f);
            body->position.y = ground->position.y + ground->size.y * 0.5f + body->size.y * (layer + 0.5f);
#else
            int layer = (int)roundf((body->positionLin.z - 0.5f) / 0.85f);
            body->positionLin.z = ground->positionLin.z + ground->size.z * 0.5f + body->size.z * (layer + 0.5f);
#endif
        }
#ifdef DEMO_2D
    bool alignAnchors = scene == 7 || scene == 8;
#else
    bool alignAnchors = scene == 6;
#endif
    if (alignAnchors)
        for (Joint* joint : experiment->joints)
        {
#ifdef DEMO_2D
            float2 first = joint->bodyA ? transform(joint->bodyA->position, joint->rA) : joint->rA;
            float2 second = transform(joint->bodyB->position, joint->rB);
            joint->bodyB->position.x += first.x - second.x;
            joint->bodyB->position.y += first.y - second.y;
#else
            float3 first = joint->bodyA ? transform(joint->bodyA->positionLin, joint->bodyA->positionAng, joint->rA) : joint->rA;
            float3 second = transform(joint->bodyB->positionLin, joint->bodyB->positionAng, joint->rB);
            joint->bodyB->positionLin += first - second;
#endif
        }
    return experiment;
}

EXPORT void demo_destroy(Experiment* experiment) { delete experiment; }
EXPORT int demo_body_count(Experiment* experiment) { return (int)experiment->bodies.size(); }
EXPORT int demo_joint_version(Experiment* experiment) { return experiment->jointVersion; }
EXPORT int demo_iterations(Experiment* experiment) { return experiment->lastIterations; }
EXPORT int demo_tree_iterations(Experiment* experiment) { return experiment->lastTreeIterations; }
EXPORT int demo_stabilization_iterations(Experiment* experiment) { return experiment->lastStabilizationIterations; }
EXPORT float demo_get_gravity(Experiment* experiment) { return experiment->solver.gravity; }
EXPORT void demo_set_gravity(Experiment* experiment, float gravity) { experiment->solver.gravity = gravity; wakeAllBodies(experiment); }
EXPORT void demo_sleep_enabled(Experiment* experiment, int enabled)
{
    experiment->islands.sleepEnabled = enabled != 0;
    if (!enabled) wakeAllBodies(experiment);
}
EXPORT void demo_activity(Experiment* experiment, int* output)
{
    initializeIslands(experiment);
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        output[i * 2] = experiment->bodies[i]->mass > 0 ? experiment->islands.roots[i] : -1;
        output[i * 2 + 1] = experiment->islands.sleeping[i];
    }
}
EXPORT int demo_body_updates(Experiment* experiment) { return experiment->islands.bodyUpdates; }
EXPORT int demo_contact_solves(Experiment* experiment) { return experiment->islands.contactSolves; }
EXPORT double demo_ground_reaction(Experiment* experiment)
{
    double result = 0;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold || (manifold->bodyA->mass > 0 && manifold->bodyB->mass > 0)) continue;
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            float3 value;
            if (experiment->newton.compliant) value = newtonContactForce(experiment, manifold, point);
            else
            {
                auto* stored = findContactReactions(manifold);
                float3 extra = stored == nullptr ? float3{0, 0, 0} : (*stored)[point];
#ifdef DEMO_2D
                float2 n = manifold->contacts[point].normal;
                float2 t{n.y, -n.x};
                float2 force = n * (manifold->lambda[point * 2] + extra.x) + t * (manifold->lambda[point * 2 + 1] + extra.y);
                value = {force.x, force.y, 0};
#else
                value = transpose(manifold->basis) * (manifold->contacts[point].lambda + extra);
#endif
            }
#ifdef DEMO_2D
            float vertical = value.y;
#else
            float vertical = value.z;
#endif
            if (manifold->bodyA->mass == 0) result += vertical;
            if (manifold->bodyB->mass == 0) result -= vertical;
        }
    }
    return result;
}
EXPORT int demo_collision_passes(Experiment* experiment) { return experiment->newton.collisionPasses; }
EXPORT int demo_get_contact_interval(Experiment* experiment) { return experiment->newton.contactDetectionInterval; }
EXPORT void demo_set_contact_interval(Experiment* experiment, int interval) { experiment->newton.contactDetectionInterval = interval; }
EXPORT void demo_set_contact_history(Experiment* experiment, int enabled) { experiment->newton.contactHistory = enabled != 0; }
EXPORT int demo_get_contact_history(Experiment* experiment) { return experiment->newton.contactHistory; }
EXPORT void demo_set_step_seconds(Experiment* experiment, float seconds) { experiment->solver.dt = seconds; }
EXPORT void demo_compliant_materials(Experiment* experiment, int enabled) { experiment->newton.compliant = enabled != 0; }
EXPORT int demo_get_compliant_materials(Experiment* experiment) { return experiment->newton.compliant; }
EXPORT float demo_step_seconds(Experiment* experiment) { return experiment->solver.dt; }

EXPORT void demo_materials(Experiment* experiment, float contact, float linear, float angular)
{
    experiment->newton.contactStiffness = contact;
    experiment->newton.jointLinearStiffness = linear;
    experiment->newton.jointAngularStiffness = angular;
}

EXPORT void demo_read_materials(Experiment* experiment, double* values)
{
    values[0] = experiment->newton.contactStiffness;
    values[1] = experiment->newton.jointLinearStiffness;
    values[2] = experiment->newton.jointAngularStiffness;
    values[3] = experiment->newton.contactDamping;
}
EXPORT void demo_contact_damping(Experiment* experiment, float value) { experiment->newton.contactDamping = value; }

EXPORT int demo_contact_model(Experiment* experiment, ContactGeometryData** output)
{
    auto& rows = experiment->contactGeometry;
    rows.clear();
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            const auto& contact = manifold->contacts[point];
            ContactGeometryData row{};
            row.bodyA = experiment->index(manifold->bodyA); row.bodyB = experiment->index(manifold->bodyB);
            row.friction = manifold->friction;
#ifdef DEMO_2D
            float3 a{contact.rA.x, contact.rA.y, 0}, b{contact.rB.x, contact.rB.y, 0};
            float3 n{contact.normal.x, contact.normal.y, 0}, t{contact.normal.y, -contact.normal.x, 0};
            float2 frame = experiment->newton.compliant
                ? static_cast<ContactManifold*>(manifold)->frames[point].xy() : contact.C0;
            float3 gap = n * frame.x + t * frame.y;
#else
            float3 a = contact.rA, b = contact.rB, n = manifold->basis[0];
            float3 frame = experiment->newton.compliant
                ? static_cast<ContactManifold*>(manifold)->frames[point] : contact.C0;
            float3 gap = transpose(manifold->basis) * frame;
#endif
            for (int axis = 0; axis < 3; ++axis)
            {
                row.pointA[axis] = a[axis]; row.pointB[axis] = b[axis];
                row.normal[axis] = -n[axis]; row.frameGap[axis] = gap[axis];
            }
            rows.push_back(row);
        }
    }
    *output = rows.data();
    return (int)rows.size();
}

struct InitialBodyData
{
    double position[3], rotation[4], size[3], mass, inertia[3], friction, velocity[3], angularVelocity[3];
};

struct CollisionPairData
{
    int bodyA, bodyB;
};

EXPORT void demo_collision_exclusions(Experiment* experiment, const CollisionPairData* pairs, int count)
{
    int bodyCount = (int)experiment->bodies.size();
    auto& exclusions = experiment->collisionExclusions;
    exclusions.assign(bodyCount * bodyCount, 0);
    for (int i = 0; i < count; ++i)
    {
        exclusions[pairs[i].bodyA * bodyCount + pairs[i].bodyB] = 1;
        exclusions[pairs[i].bodyB * bodyCount + pairs[i].bodyA] = 1;
    }
}

bool researchCollisionExcluded(Rigid* bodyA, Rigid* bodyB)
{
    Experiment* experiment = researchStep->experiment;
    int a = experiment->index(bodyA), b = experiment->index(bodyB);
    return experiment->collisionExclusions[a * experiment->bodies.size() + b] != 0;
}

bool researchContactExists(Rigid* bodyA, Rigid* bodyB)
{
    for (Force* force = bodyA->forces; force; force = force->bodyA == bodyA ? force->nextA : force->nextB)
        if ((force->bodyA == bodyB || force->bodyB == bodyB) && dynamic_cast<Manifold*>(force)) return true;
    return false;
}

EXPORT void demo_input_bodies(Experiment* experiment, InitialBodyData* output)
{
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        Rigid* body = experiment->bodies[i];
        auto& data = output[i];
        data = {};
        data.mass = body->mass;
        data.friction = body->friction;
#ifdef DEMO_2D
        data.position[0] = body->position.x; data.position[1] = body->position.y;
        data.rotation[2] = sin(body->position.z * 0.5); data.rotation[3] = cos(body->position.z * 0.5);
        data.size[0] = body->size.x; data.size[1] = body->size.y; data.size[2] = 0.25;
        data.inertia[2] = body->moment;
        data.velocity[0] = body->velocity.x; data.velocity[1] = body->velocity.y;
        data.angularVelocity[2] = body->velocity.z;
#else
        for (int axis = 0; axis < 3; ++axis)
        {
            data.position[axis] = body->positionLin[axis]; data.size[axis] = body->size[axis];
            data.inertia[axis] = body->moment[axis]; data.velocity[axis] = body->velocityLin[axis];
            data.angularVelocity[axis] = body->velocityAng[axis];
        }
        data.rotation[0] = body->positionAng.x; data.rotation[1] = body->positionAng.y;
        data.rotation[2] = body->positionAng.z; data.rotation[3] = body->positionAng.w;
#endif
    }
}

static void setInputBodies(Experiment* experiment, const InitialBodyData* input, float gravity, float seconds)
{
    experiment->solver.gravity = gravity;
    experiment->solver.dt = seconds;
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        Rigid* body = experiment->bodies[i];
        const auto& data = input[i];
        body->mass = (float)data.mass;
        body->friction = (float)data.friction;
#ifdef DEMO_2D
        body->position = {(float)data.position[0], (float)data.position[1], (float)(2 * atan2(data.rotation[2], data.rotation[3]))};
        body->velocity = {(float)data.velocity[0], (float)data.velocity[1], (float)data.angularVelocity[2]};
        body->prevVelocity = body->velocity;
        body->size = {(float)data.size[0], (float)data.size[1]};
        body->moment = (float)data.inertia[2];
#else
        body->positionLin = {(float)data.position[0], (float)data.position[1], (float)data.position[2]};
        body->positionAng = {(float)data.rotation[0], (float)data.rotation[1], (float)data.rotation[2], (float)data.rotation[3]};
        body->velocityLin = {(float)data.velocity[0], (float)data.velocity[1], (float)data.velocity[2]};
        body->velocityAng = {(float)data.angularVelocity[0], (float)data.angularVelocity[1], (float)data.angularVelocity[2]};
        body->prevVelocityLin = body->velocityLin;
        body->size = {(float)data.size[0], (float)data.size[1], (float)data.size[2]};
        body->moment = {(float)data.inertia[0], (float)data.inertia[1], (float)data.inertia[2]};
#endif
        body->radius = length(body->size * 0.5f);
    }
}

EXPORT Experiment* demo_create_input(const InitialBodyData* bodies, int bodyCount, const JointData* joints,
    int jointCount, int iterations, float gravity, float seconds)
{
    auto* experiment = new Experiment();
    experiment->solver.iterations = iterations;
#ifdef DEMO_2D
    experiment->solver.postStabilize = false;
#endif
    experiment->bodies.reserve(bodyCount);
    for (int i = 0; i < bodyCount; ++i)
    {
        const auto& data = bodies[i];
#ifdef DEMO_2D
        float2 size{(float)data.size[0], (float)data.size[1]};
        float density = (float)(data.mass / (data.size[0] * data.size[1]));
        auto* body = new Rigid(&experiment->solver, size, density, (float)data.friction,
            {(float)data.position[0], (float)data.position[1], 0});
#else
        float3 size{(float)data.size[0], (float)data.size[1], (float)data.size[2]};
        float density = (float)(data.mass / (data.size[0] * data.size[1] * data.size[2]));
        auto* body = new Rigid(&experiment->solver, size, density, (float)data.friction,
            {(float)data.position[0], (float)data.position[1], (float)data.position[2]});
#endif
        experiment->bodies.push_back(body);
        experiment->indices[body] = i;
    }
    setInputBodies(experiment, bodies, gravity, seconds);
    experiment->joints.reserve(jointCount);
    for (int i = 0; i < jointCount; ++i)
    {
        const auto& data = joints[i];
        Rigid* bodyA = data.bodyA < 0 ? nullptr : experiment->bodies[data.bodyA];
        Rigid* bodyB = experiment->bodies[data.bodyB];
#ifdef DEMO_2D
        auto* joint = new Joint(&experiment->solver, bodyA, bodyB,
            {(float)data.pointA[0], (float)data.pointA[1]}, {(float)data.pointB[0], (float)data.pointB[1]},
            {INFINITY, INFINITY, data.angularFixed ? INFINITY : 0.0f});
#else
        auto* joint = new Joint(&experiment->solver, bodyA, bodyB,
            {(float)data.pointA[0], (float)data.pointA[1], (float)data.pointA[2]},
            {(float)data.pointB[0], (float)data.pointB[1], (float)data.pointB[2]},
            INFINITY, data.angularFixed ? INFINITY : 0.0f);
#endif
        experiment->joints.push_back(joint);
    }
    return experiment;
}

EXPORT void demo_remove_joint(Experiment* experiment, int index)
{
    Joint* joint = experiment->joints[index];
    removeDemoJoint(experiment, joint);
    delete joint;
}

EXPORT void demo_move_body(Experiment* experiment, int index, float x, float y, float z)
{
    wakeBody(experiment, index);
#ifdef DEMO_2D
    experiment->bodies[index]->position.x = x;
    experiment->bodies[index]->position.y = y;
#else
    experiment->bodies[index]->positionLin = {x, y, z};
#endif
    Rigid* moved = experiment->bodies[index];
    if (moved->mass <= 0)
        for (int other = 0; other < (int)experiment->bodies.size(); ++other)
        {
            Rigid* body = experiment->bodies[other];
            if (body->mass <= 0) continue;
            Manifold::Contact contacts[8]{};
#ifdef DEMO_2D
            int count = Manifold::collide(moved, body, contacts);
#else
            float3x3 basis;
            int count = Manifold::collide(moved, body, contacts, basis);
#endif
            if (count) wakeBody(experiment, other);
        }
}

EXPORT void demo_velocities(Experiment* experiment, double* output)
{
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
#ifdef DEMO_2D
        output[i * 3] = experiment->bodies[i]->velocity.x;
        output[i * 3 + 1] = experiment->bodies[i]->velocity.y;
        output[i * 3 + 2] = 0;
#else
        for (int axis = 0; axis < 3; ++axis) output[i * 3 + axis] = experiment->bodies[i]->velocityLin[axis];
#endif
    }
}

EXPORT void demo_research_extensions(Experiment* experiment, int enabled)
{
    experiment->researchExtensions = enabled != 0;
}

EXPORT void demo_post_stabilize(Experiment* experiment, int enabled)
{
#ifdef DEMO_2D
    experiment->solver.postStabilize = enabled != 0;
#else
    experiment->postStabilize = enabled != 0;
#endif
}

// Read geometric contact gaps at the final poses, using the original narrow phase.
// Each pair contributes its deepest point; translating the pair preserves its shape.
EXPORT int demo_contacts(Experiment* experiment, BodyData* bodies, ConstraintData** output)
{
    readBodies(experiment, bodies);
    experiment->constraints.clear();
    for (int b = 1; b < (int)experiment->bodies.size(); ++b)
    {
        Rigid* bodyB = experiment->bodies[b];
        for (int a = 0; a < b; ++a)
        {
            Rigid* bodyA = experiment->bodies[a];
            if (bodyA->mass <= 0 && bodyB->mass <= 0) continue;
#ifdef DEMO_2D
            float2 distance = bodyA->position.xy() - bodyB->position.xy();
#else
            float3 distance = bodyA->positionLin - bodyB->positionLin;
#endif
            float radius = bodyA->radius + bodyB->radius;
            if (dot(distance, distance) > radius * radius) continue;
            if (experiment->collisionExclusions[a * experiment->bodies.size() + b]) continue;
            float3 normal;
            float separation = readBoxGap(bodyA, bodyB, normal);
            if (separation > COLLISION_MARGIN) continue;
            ConstraintData row = {};
            row.bodyA = a; row.bodyB = b; row.kind = 1;
            row.error = separation;
#ifdef DEMO_2D
            constexpr int axes = 2;
#else
            constexpr int axes = 3;
#endif
            for (int axis = 0; axis < axes; ++axis)
            {
                row.jacobianA[axis] = normal[axis];
                row.jacobianB[axis] = -normal[axis];
            }
            experiment->constraints.push_back(row);
        }
    }
    *output = experiment->constraints.data();
    return (int)experiment->constraints.size();
}

EXPORT void demo_add_velocity(Experiment* experiment, int index, float x, float y, float z)
{
    wakeBody(experiment, index);
    Rigid* body = experiment->bodies[index];
#ifdef DEMO_2D
    body->velocity.x += x;
    body->velocity.y += y;
#else
    body->velocityLin += float3{x, y, z};
#endif
}

EXPORT void demo_step(Experiment* experiment, void (*correct)(void*), void* context, int iteration)
{
    initializeIslands(experiment);
    experiment->islands.bodyUpdates = 0;
    experiment->islands.contactSolves = 0;
    std::fill(experiment->islands.directContacts.begin(), experiment->islands.directContacts.end(), 0);
    bool active = false;
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
        active |= experiment->bodies[i]->mass > 0 && !experiment->islands.sleeping[i];
    if (!active)
    {
        experiment->newton.collisionPasses = 0;
        experiment->lastIterations = experiment->lastTreeIterations = experiment->lastStabilizationIterations = 0;
        return;
    }
    ResearchStep step{experiment, correct, context, iteration};
    step.configuredIterations = experiment->solver.iterations;
    step.bodies = experiment->bodies.data();
    step.bodyCount = (int)experiment->bodies.size();
    ResearchStep* previous = researchStep;
    researchStep = &step;
    experiment->lastStabilizationIterations = 0;
    experiment->lastTreeIterations = 0;
    if (experiment->newton.compliant) { experiment->solver.alpha = 0; experiment->solver.gamma = 0.999f; }
    beginLongRangeStep(experiment);
    if (experiment->newton.compliant) stepNewtonRigid();
    else experiment->solver.step();
    restoreStabilizationForces(experiment);
    experiment->lastIterations = experiment->solver.iterations;
    experiment->solver.iterations = step.configuredIterations;
    if (experiment->researchExtensions) preserveIslandCenterMotion(experiment);
    finishIslandStep(experiment);
    researchStep = previous;
}

EXPORT void demo_frame(Experiment* experiment, BodyFrame* output)
{
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        Rigid* body = experiment->bodies[i];
        BodyFrame& frame = output[i];
        frame = {};
        frame.mass = body->mass;
#ifdef DEMO_2D
        frame.position[0] = body->position.x;
        frame.position[1] = body->position.y;
        frame.rotation[2] = sin(body->position.z * 0.5);
        frame.rotation[3] = cos(body->position.z * 0.5);
        frame.size[0] = body->size.x;
        frame.size[1] = body->size.y;
        frame.size[2] = 0.25;
#else
        for (int axis = 0; axis < 3; ++axis)
        {
            frame.position[axis] = body->positionLin[axis];
            frame.size[axis] = body->size[axis];
        }
        frame.rotation[0] = body->positionAng.x;
        frame.rotation[1] = body->positionAng.y;
        frame.rotation[2] = body->positionAng.z;
        frame.rotation[3] = body->positionAng.w;
#endif
    }
}

static void readBodies(Experiment* experiment, BodyData* output)
{
    double inverseTimeSquared = 1.0 / (experiment->solver.dt * experiment->solver.dt);
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        Rigid* body = experiment->bodies[i];
        BodyData& data = output[i];
        data = {};
        if (body->mass <= 0) continue;
#ifdef DEMO_2D
        float3 difference = body->position - body->inertial;
        if (experiment->newton.compliant) difference.z = newtonPlanarDifference(body->position.z, body->inertial.z);
        data.inertia[0] = data.inertia[1] = body->mass * inverseTimeSquared;
        data.inertia[2] = body->moment * inverseTimeSquared;
        for (int axis = 0; axis < 3; ++axis) data.difference[axis] = difference[axis];
#else
        float3 linear = body->positionLin - body->inertialLin;
        float3 angular = body->positionAng - body->inertialAng;
        AngularInertia inertia{};
        if (experiment->newton.compliant)
        {
            inertia = newtonAngularInertia(body);
            angular = -rotate(body->positionAng, newtonRotationDifference(inverse(body->positionAng) * body->inertialAng, quat{0, 0, 0, 1}));
        }
        for (int axis = 0; axis < 3; ++axis)
        {
            data.inertia[axis] = body->mass * inverseTimeSquared;
            data.inertia[axis + 3] = !experiment->newton.compliant ? body->moment[axis] * inverseTimeSquared
                : inertia.hessian[axis * 3 + axis] * inverseTimeSquared;
            data.difference[axis] = linear[axis];
            data.difference[axis + 3] = angular[axis];
            if (experiment->newton.compliant)
                for (int column = 0; column < 3; ++column)
                    if (axis != column) data.curvature[axis * 3 + column] = inertia.hessian[axis * 3 + column] * inverseTimeSquared;
        }
#endif
    }
}

static ConstraintData& addRow(Experiment* experiment, ConstraintData* output, int& count,
    Force* force, int kind, int normalRow, float error, float* multiplier, float penalty, float friction)
{
    ConstraintData& row = output[count++];
    row = {};
    row.bodyA = experiment->index(force->bodyA);
    row.bodyB = experiment->index(force->bodyB);
    row.kind = kind;
    row.normalRow = normalRow;
    row.error = error;
    row.force = *multiplier;
    row.penalty = penalty;
    row.friction = friction;
    experiment->multipliers.push_back(multiplier);
    return row;
}

EXPORT int demo_read(Experiment* experiment, BodyData* bodies, ConstraintData** data, float alpha)
{
    readBodies(experiment, bodies);
    experiment->multipliers.clear();
    experiment->forces.clear();
    for (Force* force = experiment->solver.forces; force; force = force->next)
        experiment->forces.push_back(force);
    int capacity = 0;
    for (Force* force : experiment->forces)
    {
        if (dynamic_cast<Joint*>(force)) capacity += 3;
        else if (auto* manifold = dynamic_cast<Manifold*>(force)) capacity += manifold->numContacts * 3;
    }
    experiment->constraints.resize(capacity);
    ConstraintData* output = experiment->constraints.data();
    *data = output;
    std::stable_sort(experiment->forces.begin(), experiment->forces.end(), [experiment](Force* a, Force* b)
    {
        int a0 = experiment->index(a->bodyA), a1 = experiment->index(a->bodyB);
        int b0 = experiment->index(b->bodyA), b1 = experiment->index(b->bodyB);
        if (std::max(a0, a1) != std::max(b0, b1)) return std::max(a0, a1) < std::max(b0, b1);
        return std::min(a0, a1) < std::min(b0, b1);
    });
    int count = 0;
    for (Force* force : experiment->forces)
    {
        if (Joint* joint = dynamic_cast<Joint*>(force))
        {
#ifdef DEMO_2D
            float2 rA = joint->bodyA ? rotate(joint->bodyA->position.z, joint->rA) : joint->rA;
            float2 rB = rotate(joint->bodyB->position.z, joint->rB);
            float2 error = (joint->bodyA ? joint->bodyA->position.xy() : float2{0, 0}) + rA - joint->bodyB->position.xy() - rB;
            float angularScale = !experiment->newton.compliant ? joint->torqueArm : 1.0f;
            float3 value = { error.x, error.y, ((joint->bodyA ? joint->bodyA->position.z : 0) - joint->bodyB->position.z - joint->restAngle) * angularScale };
            for (int axis = 0; axis < 3; ++axis)
            {
                if (!isinf(joint->stiffness[axis])) continue;
                ConstraintData& row = addRow(experiment, output, count, joint, 0, -1,
                    alpha == 0 ? value[axis] : value[axis] - alpha * joint->C0[axis], &joint->lambda[axis], joint->penalty[axis], 0);
                if (axis < 2)
                {
                    row.jacobianA[axis] = 1;
                    row.jacobianB[axis] = -1;
                    row.jacobianA[2] = axis == 0 ? -rA.y : rA.x;
                    row.jacobianB[2] = axis == 0 ? rB.y : -rB.x;
                    if (!experiment->newton.compliant)
                    {
                        if (row.bodyA >= 0) bodies[row.bodyA].curvature[0] += fabs(row.force * rA[axis]);
                        bodies[row.bodyB].curvature[0] += fabs(row.force * rB[axis]);
                    }
                }
                else
                {
                    row.jacobianA[2] = angularScale;
                    row.jacobianB[2] = -angularScale;
                }
            }
#else
            if (!isinf(joint->stiffnessLin)) continue;
            float3 rA = joint->bodyA ? rotate(joint->bodyA->positionAng, joint->rA) : joint->rA;
            float3 rB = rotate(joint->bodyB->positionAng, joint->rB);
            float3 error = (joint->bodyA ? joint->bodyA->positionLin : float3{0, 0, 0}) + rA - joint->bodyB->positionLin - rB;
            if (alpha != 0) error -= joint->C0Lin * alpha;
            for (int axis = 0; axis < 3; ++axis)
            {
                ConstraintData& row = addRow(experiment, output, count, joint, 0, -1,
                    error[axis], &joint->lambdaLin[axis], joint->penaltyLin[axis], 0);
                float3 direction = {0, 0, 0}; direction[axis] = 1;
                float3 angularA = cross(rA, direction), angularB = -cross(rB, direction);
                row.jacobianA[axis] = 1;
                row.jacobianB[axis] = -1;
                for (int rotation = 0; rotation < 3; ++rotation)
                {
                    row.jacobianA[rotation + 3] = angularA[rotation];
                    row.jacobianB[rotation + 3] = angularB[rotation];
                }
            }
            // Retain the angular coupling and its free-twist direction in the coarse solve.
            for (int side = 0; !experiment->newton.compliant && side < 2; ++side)
            {
                Rigid* body = side == 0 ? joint->bodyA : joint->bodyB;
                if (!body || body->mass <= 0) continue;
                float3 r = side == 0 ? rA : -rB;
                float3x3 h = {0, 0, 0, 0, 0, 0, 0, 0, 0};
                for (int axis = 0; axis < 3; ++axis)
                {
                    float3x3 term = diagonal(-r[axis], -r[axis], -r[axis]);
                    for (int k = 0; k < 3; ++k) term[k][axis] += r[k];
                    h += term * joint->lambdaLin[axis];
                }
                int bodyIndex = experiment->index(body);
                for (int row = 0; row < 3; ++row)
                    for (int column = 0; column < 3; ++column)
                        bodies[bodyIndex].curvature[row * 3 + column] += h[row][column];
            }
#endif
        }
        else if (Manifold* manifold = dynamic_cast<Manifold*>(force))
        {
            for (int contactIndex = 0; contactIndex < manifold->numContacts; ++contactIndex)
            {
                auto& contact = manifold->contacts[contactIndex];
                int normalRow = count;
#ifdef DEMO_2D
                float2 rA = rotate(manifold->bodyA->position.z, contact.rA);
                float2 rB = rotate(manifold->bodyB->position.z, contact.rB);
                float2 gap = manifold->bodyA->position.xy() + rA - manifold->bodyB->position.xy() - rB;
                for (int axis = 0; axis < 2; ++axis)
                {
                    float2 direction = axis == 0 ? contact.normal : float2{contact.normal.y, -contact.normal.x};
                    float3 jacobianA{direction.x, direction.y, cross(rA, direction)};
                    float3 jacobianB{-direction.x, -direction.y, -cross(rB, direction)};
                    float error = dot(direction, gap) - alpha * (axis == 0 ? contact.C0.x : contact.C0.y);
                    int component = contactIndex * 2 + axis;
                    ConstraintData& row = addRow(experiment, output, count, manifold, axis == 0 ? 1 : 2, normalRow,
                        error, &manifold->lambda[component], manifold->penalty[component], manifold->friction);
                    for (int k = 0; k < 3; ++k) { row.jacobianA[k] = jacobianA[k]; row.jacobianB[k] = jacobianB[k]; }
                }
#else
                float3 rA = rotate(manifold->bodyA->positionAng, contact.rA);
                float3 rB = rotate(manifold->bodyB->positionAng, contact.rB);
                float3 gap = manifold->bodyA->positionLin + rA - manifold->bodyB->positionLin - rB;
                for (int axis = 0; axis < 3; ++axis)
                {
                    float3 linearA = manifold->basis[axis], linearB = -linearA;
                    float3 angularA = cross(rA, linearA), angularB = cross(rB, linearB);
                    float error = dot(linearA, gap) - alpha * (axis == 0 ? std::min(contact.C0.x, 0.0f) : contact.C0[axis]);
                    ConstraintData& row = addRow(experiment, output, count, manifold, axis == 0 ? 1 : 2, normalRow,
                        error, &contact.lambda[axis], contact.penalty[axis], manifold->friction);
                    for (int k = 0; k < 3; ++k)
                    {
                        row.jacobianA[k] = linearA[k]; row.jacobianB[k] = linearB[k];
                        row.jacobianA[k + 3] = angularA[k]; row.jacobianB[k + 3] = angularB[k];
                    }
                }
#endif
            }
        }
    }
    return count;
}

EXPORT void demo_apply(Experiment* experiment, const double* changes, const double* multipliers, int count)
{
    for (int i = 0; i < (int)experiment->bodies.size(); ++i)
    {
        Rigid* body = experiment->bodies[i];
        if (body->mass <= 0) continue;
        const double* change = changes + i * 6;
#ifdef DEMO_2D
        body->position += float3{(float)change[0], (float)change[1], (float)change[2]};
#else
        body->positionLin += float3{(float)change[0], (float)change[1], (float)change[2]};
        body->positionAng = body->positionAng + float3{(float)change[3], (float)change[4], (float)change[5]};
#endif
    }
    for (int i = 0; i < count; ++i) *experiment->multipliers[i] = (float)multipliers[i];
}

#ifdef AVBD_PROFILE
EXPORT void demo_step_measurements(Experiment* experiment, double* values)
{
    for (int i = 0; i < (int)StepPhase::Count; ++i) values[i] = experiment->measurements.milliseconds[i];
    for (int i = 0; i < (int)SceneCount::Count; ++i) values[(int)StepPhase::Count + i] = experiment->measurements.counts[i];
    experiment->measurements = {};
}
#endif

bool researchContactPenetrates(Manifold* contact, float tolerance, float3& normal, float& gap)
{
    RESEARCH_TIME(phaseTime, ContactGeometry);
    Rigid* first = contact->bodyA; Rigid* second = contact->bodyB;
#ifndef DEMO_2D
    normal = contact->basis[0];
    float3 firstNormal = rotate(conjugate(first->positionAng), normal);
    float3 secondNormal = rotate(conjugate(second->positionAng), normal);
    gap = fabsf(dot(second->positionLin - first->positionLin, normal));
    for (int axis = 0; axis < 3; ++axis)
        gap -= .5f * (first->size[axis] * fabsf(firstNormal[axis]) + second->size[axis] * fabsf(secondNormal[axis]));
    // One complete-shape projection can certify the separation tolerance.
    // A penetrating projection proceeds to all face and edge separating axes.
    if (gap >= -tolerance) return false;
#endif
    gap = readBoxGap(first, second, normal);
    return gap < -tolerance;
}

#include "BridgeState.h"
#include "ResearchStep.h"
#include <limits>

static float3 crossVector(float3 a, float3 b)
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

static float3 center(Rigid* body, bool initial)
{
#ifdef DEMO_2D
    const auto& pose = initial ? body->initial : body->position;
    return {pose.x, pose.y, 0};
#else
    return initial ? body->initialLin : body->positionLin;
#endif
}

static float3 tangentVelocity(Rigid* body)
{
#ifdef DEMO_2D
    return {body->velocity.x, 0, 0};
#else
    return {body->velocityLin.x, body->velocityLin.y, 0};
#endif
}

static float3 up()
{
#ifdef DEMO_2D
    return {0, 1, 0};
#else
    return {0, 0, 1};
#endif
}

static float3 offset(Rigid* body, const Manifold::Contact& contact, bool first)
{
#ifdef DEMO_2D
    float2 point = rotate(body->initial.z, first ? contact.rA : contact.rB);
    return {point.x, point.y, 0};
#else
    return rotate(body->initialAng, first ? contact.rA : contact.rB);
#endif
}

// The dual of collinear Coulomb translation is a box-constrained tridiagonal
// quadratic. A free interval uses one Thomas solve; a bound fixes one impulse.
static void solveImpulses(std::vector<ContactColumnFriction>& rows)
{
    int count = (int)rows.size();
    double tolerance = 64 * std::numeric_limits<double>::epsilon();
    for (;;)
    {
        for (int i = 0; i < count; ++i)
        {
            auto& row = rows[i];
            if (row.boundDirection != 0) continue;
            double lowerMass = i > 0 ? rows[i - 1].inverseMass : 0;
            row.diagonal = row.inverseMass + lowerMass;
            row.right = -(row.speed - (i > 0 ? rows[i - 1].speed : 0));
            if (i > 0)
            {
                const auto& previous = rows[i - 1];
                if (previous.boundDirection == 0)
                {
                    row.diagonal -= lowerMass * lowerMass / previous.diagonal;
                    row.right += lowerMass * previous.right / previous.diagonal;
                }
                else row.right += lowerMass * previous.impulse;
            }
            if (i + 1 < count && rows[i + 1].boundDirection != 0)
                row.right += row.inverseMass * rows[i + 1].impulse;
        }
        for (int i = count - 1; i >= 0; --i)
        {
            auto& row = rows[i];
            if (row.boundDirection != 0) continue;
            double right = row.right;
            if (i + 1 < count && rows[i + 1].boundDirection == 0)
                right += row.inverseMass * (rows[i + 1].impulse + rows[i + 1].change);
            row.change = right / row.diagonal - row.impulse;
        }
        double fraction = 1;
        int hit = -1, direction = 0;
        for (int i = 0; i < count; ++i)
        {
            const auto& row = rows[i];
            if (row.boundDirection != 0 || row.change == 0) continue;
            int side = row.change > 0 ? 1 : -1;
            double rate = (side * row.bound - row.impulse) / row.change;
            if (rate <= fraction) { fraction = rate; hit = i; direction = side; }
        }
        for (auto& row : rows) if (row.boundDirection == 0) row.impulse += fraction * row.change;
        if (hit >= 0)
        {
            rows[hit].impulse = direction * rows[hit].bound;
            rows[hit].boundDirection = direction;
        }
        if (fraction < 1) continue;
        int release = -1;
        double violation = tolerance;
        for (int i = 0; i < count; ++i)
        {
            const auto& row = rows[i];
            if (row.boundDirection == 0 || row.bound == 0) continue;
            double gradient = row.speed - (i > 0 ? rows[i - 1].speed : 0)
                + row.inverseMass * (row.impulse - (i + 1 < count ? rows[i + 1].impulse : 0));
            if (i > 0) gradient += rows[i - 1].inverseMass * (row.impulse - rows[i - 1].impulse);
            double error = row.boundDirection * gradient;
            if (error > violation) { violation = error; release = i; }
        }
        if (release < 0) break;
        rows[release].boundDirection = 0;
    }
}

bool researchSolveColumnFriction(Experiment* experiment)
{
    auto& columns = experiment->contactColumns;
    auto& entries = columns.bodies;
    const auto& order = columns.order;
    auto& rows = columns.friction;
    rows.assign(order.size(), {});
    float3 direction{};
    for (int index : order)
    {
        float3 velocity = tangentVelocity(experiment->bodies[index]);
        if (lengthSq(velocity) > 0) { direction = velocity / length(velocity); break; }
    }
    double dt = experiment->solver.dt;
    for (int i = 0; i < (int)order.size(); ++i)
    {
        int index = order[i];
        Rigid* body = experiment->bodies[index];
        float3 velocity = tangentVelocity(body);
        double roundoff = 16 * std::numeric_limits<float>::epsilon() * std::numeric_limits<float>::epsilon();
        if (lengthSq(crossVector(velocity, direction)) > roundoff * lengthSq(velocity)) return false;
        auto& row = rows[i];
        row.speed = dot(velocity, direction);
        row.inverseMass = 1.0 / body->mass;
        row.bound = entries[index].below ? entries[index].below->friction * entries[index].force * dt : 0;
        row.boundDirection = row.bound == 0 ? 1 : 0;
    }
    solveImpulses(rows);
    int firstPatch = (int)columns.patches.size();
    for (int i = 0; i < (int)order.size(); ++i)
    {
        int index = order[i];
        Rigid* body = experiment->bodies[index];
        auto& entry = entries[index];
        double speed = rows[i].speed + rows[i].inverseMass * (rows[i].impulse - (i + 1 < (int)order.size() ? rows[i + 1].impulse : 0));
        entry.tangentVelocity = direction * (float)speed;
        float3 position = center(body, true) + entry.tangentVelocity * (float)dt;
#ifdef DEMO_2D
        body->position = {position.x, (float)entry.position, body->initial.z};
#else
        body->positionLin = {position.x, position.y, (float)entry.position};
        body->positionAng = body->initialAng;
#endif
    }
    auto restore = [&]()
    {
        columns.patches.resize(firstPatch);
        for (int index : order)
        {
            Rigid* body = experiment->bodies[index];
            const auto& pose = columns.poses[index];
#ifdef DEMO_2D
            body->position = {pose.center.x, pose.center.y, pose.angle};
#else
            body->positionLin = pose.center; body->positionAng = pose.rotation;
#endif
            entries[index].frictionPatch = -1;
        }
        return false;
    };
    float3 forceAbove{}, pointAbove{};
    for (int i = (int)order.size() - 1; i >= 0; --i)
    {
        int index = order[i];
        auto& entry = entries[index];
        Rigid* body = experiment->bodies[index];
        if (!entry.below || entry.force == 0)
        {
            if (lengthSq(forceAbove) > 0) return restore();
            continue;
        }
        Manifold* manifold = entry.below;
        ContactColumnPatch patch{};
        patch.body = index;
        patch.force = up() * (float)entry.force + direction * (float)(rows[i].impulse / dt);
#ifdef DEMO_2D
        patch.count = Manifold::collide(manifold->bodyA, manifold->bodyB, patch.contacts.data());
        if (patch.count == 0 || patch.contacts[0].normal.x != 0) return restore();
#else
        patch.count = researchClipContactFace(manifold, patch.contacts.data(), patch.basis);
        if (patch.count == 0 || patch.basis[0].x != 0 || patch.basis[0].y != 0) return restore();
#endif
        float3 bodyCenter = center(body, false);
        float3 torque = crossVector(pointAbove - bodyCenter, forceAbove);
        float3 first = offset(body, patch.contacts[0], manifold->bodyA == body);
        double points[8][2], target[2];
#ifdef DEMO_2D
        target[0] = bodyCenter.x + (torque.z + first.y * patch.force.x) / entry.force;
        target[1] = 0;
#else
        target[0] = bodyCenter.x + (first.z * patch.force.x - torque.y) / entry.force;
        target[1] = bodyCenter.y + (torque.x + first.z * patch.force.y) / entry.force;
#endif
        for (int point = 0; point < patch.count; ++point)
        {
            float3 contact = bodyCenter + offset(body, patch.contacts[point], manifold->bodyA == body);
            points[point][0] = contact.x; points[point][1] = contact.y;
        }
        entry.weights.fill(0);
        if (!researchDistributePressure(points, patch.count, target, entry.weights.data())) return restore();
        float3 pressurePoint{};
        for (int point = 0; point < patch.count; ++point)
            pressurePoint += (bodyCenter + offset(body, patch.contacts[point], manifold->bodyA == body)) * (float)entry.weights[point];
        float3 error = crossVector(pressurePoint - bodyCenter, patch.force) - torque;
        if (lengthSq(error) > contactGeometryAccuracy * contactGeometryAccuracy * lengthSq(patch.force)) return restore();
        forceAbove = patch.force; pointAbove = pressurePoint;
        entry.frictionPatch = (int)columns.patches.size();
        columns.patches.push_back(patch);
    }
    for (int index : order) experiment->islands.directContacts[index] = 1;
    return true;
}

void researchCommitColumnFriction(Experiment* experiment)
{
    for (const auto& patch : experiment->contactColumns.patches)
    {
        auto& entry = experiment->contactColumns.bodies[patch.body];
        Manifold* manifold = entry.below;
        float side = manifold->bodyA == experiment->bodies[patch.body] ? 1.0f : -1.0f;
#ifdef DEMO_2D
        float initialSeparation = experiment->longRange.enabled ? 0 : readBoxSeparation(manifold->bodyA, manifold->bodyB,
            manifold->bodyA->initial, manifold->bodyB->initial);
#endif
        manifold->numContacts = patch.count;
#ifndef DEMO_2D
        static_cast<ContactManifold*>(manifold)->offsetsValid = false;
        manifold->basis = patch.basis;
#endif
        for (int point = 0; point < patch.count; ++point)
        {
            auto& contact = manifold->contacts[point];
            contact = patch.contacts[point];
            float3 a = center(manifold->bodyA, true) + offset(manifold->bodyA, contact, true);
            float3 b = center(manifold->bodyB, true) + offset(manifold->bodyB, contact, false);
            float3 force = patch.force * (float)(-side * entry.weights[point]);
#ifdef DEMO_2D
            float2 normal = contact.normal, tangent{normal.y, -normal.x};
            float3 n{normal.x, normal.y, 0}, t{tangent.x, tangent.y, 0};
            contact.C0 = {min(0.0f, max(dot(n, a - b), initialSeparation)), dot(t, a - b)};
            float3 ra = offset(manifold->bodyA, contact, true), rb = offset(manifold->bodyB, contact, false);
            contact.JAn = {n.x, n.y, crossVector(ra, n).z}; contact.JBn = {-n.x, -n.y, -crossVector(rb, n).z};
            contact.JAt = {t.x, t.y, crossVector(ra, t).z}; contact.JBt = {-t.x, -t.y, -crossVector(rb, t).z};
            manifold->lambda[point * 2] = dot(n, force); manifold->lambda[point * 2 + 1] = dot(t, force);
            manifold->penalty[point * 2] = manifold->penalty[point * 2 + 1] = 0;
#else
            contact.C0 = manifold->basis * (a - b);
            contact.lambda = manifold->basis * force;
            contact.penalty = {0, 0, 0};
#endif
            contact.stick = false;
            researchInitializeContact(manifold, point);
            researchTransferContactPressure(manifold, point);
        }
        clearContactReactions(experiment, manifold);
        experiment->contactForces.erase(manifold);
    }
}

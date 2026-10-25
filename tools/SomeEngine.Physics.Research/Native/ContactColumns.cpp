#include "BridgeState.h"
#include "ResearchStep.h"

static float3 initialCenter(Rigid* body)
{
#ifdef DEMO_2D
    return {body->initial.x, body->initial.y, 0};
#else
    return body->initialLin;
#endif
}

static float height(float3 point)
{
#ifdef DEMO_2D
    return point.y;
#else
    return point.z;
#endif
}

static float3 contactOffset(Manifold* manifold, int point, Rigid* body)
{
    const auto& contact = manifold->contacts[point];
#ifdef DEMO_2D
    float2 offset = rotate(body->initial.z, body == manifold->bodyA ? contact.rA : contact.rB);
    return {offset.x, offset.y, 0};
#else
    return rotate(body->initialAng, body == manifold->bodyA ? contact.rA : contact.rB);
#endif
}

// Pressure centers certify the zero-angular-impulse branch of the full rigid
// contact equations. The height projection alone does not establish that branch.
static bool distributePressure(Manifold* manifold, Rigid* body, double x, double y, std::array<double, 8>& weights)
{
    double points[8][2];
    float3 center = initialCenter(body);
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        float3 offset = contactOffset(manifold, point, body);
        points[point][0] = (double)center.x + offset.x;
        points[point][1] = (double)center.y + offset.y;
    }
    double target[2] = {x, y};
    return researchDistributePressure(points, manifold->numContacts, target, weights.data());
}

static bool angularRest(Rigid* body)
{
#ifdef DEMO_2D
    return body->velocity.z == 0;
#else
    return lengthSq(body->velocityAng) == 0;
#endif
}

static bool readColumnBody(Experiment* experiment, int index)
{
    Rigid* body = experiment->bodies[index];
    if (!angularRest(body)) return false;
    auto& entry = experiment->contactColumns.bodies[index];
    for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) return false;
        if (manifold->numContacts == 0) continue;
#ifdef DEMO_2D
        float3 normal{manifold->contacts[0].normal.x, manifold->contacts[0].normal.y, 0};
        if (normal.x != 0) return false;
#else
        float3 normal = manifold->basis[0];
        if (normal.x != 0 || normal.y != 0) return false;
#endif
        Rigid* upper = height(normal) > 0 ? manifold->bodyA : manifold->bodyB;
        Rigid* lower = upper == manifold->bodyA ? manifold->bodyB : manifold->bodyA;
        if (upper->mass <= 0) return false;
        if (body == lower)
        {
            if (entry.upper >= 0) return false;
            entry.upper = experiment->index(upper);
            continue;
        }
        if (entry.below) return false;
        entry.below = manifold; entry.lower = experiment->index(lower);
        double minimum = INFINITY, maximum = -INFINITY;
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            double separation = (double)height(contactOffset(manifold, point, lower)) - height(contactOffset(manifold, point, upper));
            minimum = std::min(minimum, separation); maximum = std::max(maximum, separation);
        }
        if (maximum - minimum > contactGeometryAccuracy) return false;
        entry.separation = maximum;
    }
    return true;
}

static bool solveColumn(Experiment* experiment, int bottom)
{
    auto& columns = experiment->contactColumns;
    auto& order = columns.order;
    auto& pools = columns.pools;
    auto& entries = columns.bodies;
    order.clear(); pools.clear();
    for (int body = bottom; body >= 0; body = entries[body].upper) order.push_back(body);
    if (order.size() == 1 && !entries[bottom].below) return false;
    bool grounded = entries[bottom].lower >= 0;
    double offset = grounded ? height(initialCenter(experiment->bodies[entries[bottom].lower])) : 0;
    double dt = experiment->solver.dt, gravity = experiment->solver.gravity;
    for (int i = 0; i < (int)order.size(); ++i)
    {
        int index = order[i];
        Rigid* body = experiment->bodies[index];
        auto& entry = entries[index];
        offset += entry.separation;
        entry.offset = offset;
#ifdef DEMO_2D
        double velocity = body->velocity.y;
#else
        double velocity = body->velocityLin.z;
#endif
        entry.predicted = height(initialCenter(body)) + dt * velocity + dt * dt * gravity;
        pools.push_back({i, i + 1, body->mass, body->mass * (entry.predicted - offset)});
        while (pools.size() > 1)
        {
            auto& left = pools[pools.size() - 2];
            const auto& right = pools.back();
            if (left.total / left.mass <= right.total / right.mass) break;
            left.last = right.last; left.mass += right.mass; left.total += right.total;
            pools.pop_back();
        }
    }
    for (const auto& pool : pools)
    {
        double value = pool.total / pool.mass;
        if (grounded) value = std::max(0.0, value);
        for (int i = pool.first; i < pool.last; ++i) entries[order[i]].position = value + entries[order[i]].offset;
    }
    bool sliding = false;
    for (int index : order)
    {
#ifdef DEMO_2D
        sliding |= experiment->bodies[index]->velocity.x != 0;
#else
        const auto& velocity = experiment->bodies[index]->velocityLin;
        sliding |= velocity.x != 0 || velocity.y != 0;
#endif
    }
    if (sliding)
    {
#ifndef DEMO_2D
        if (order.size() == 1) return false;
#endif
        double force = 0;
        for (int i = (int)order.size() - 1; i >= 0; --i)
        {
            auto& entry = entries[order[i]];
            force = std::max(0.0, force + experiment->bodies[order[i]]->mass * (entry.position - entry.predicted) / (dt * dt));
            entry.force = force;
        }
        return researchSolveColumnFriction(experiment);
    }
    double forceAbove = 0, centerAboveX = 0, centerAboveY = 0;
    for (int i = (int)order.size() - 1; i >= 0; --i)
    {
        int index = order[i];
        Rigid* body = experiment->bodies[index];
        auto& entry = entries[index];
        float3 center = initialCenter(body);
        double ownForce = body->mass * (entry.position - entry.predicted) / (dt * dt);
        entry.force = std::max(0.0, forceAbove + ownForce);
        if (entry.below && entry.force > 0)
        {
            entry.centerX = center.x + forceAbove * (centerAboveX - center.x) / entry.force;
#ifndef DEMO_2D
            entry.centerY = center.y + forceAbove * (centerAboveY - center.y) / entry.force;
#endif
            if (!distributePressure(entry.below, body, entry.centerX, entry.centerY, entry.weights)) return false;
        }
        else if (forceAbove > 0)
        {
            if (fabs(centerAboveX - center.x) > contactGeometryAccuracy) return false;
#ifndef DEMO_2D
            if (fabs(centerAboveY - center.y) > contactGeometryAccuracy) return false;
#endif
        }
        forceAbove = entry.force; centerAboveX = entry.centerX; centerAboveY = entry.centerY;
    }
    for (int index : order)
    {
        Rigid* body = experiment->bodies[index];
        const auto& entry = entries[index];
#ifdef DEMO_2D
        body->position = body->initial; body->position.y = (float)entry.position;
#else
        body->positionLin = body->initialLin; body->positionLin.z = (float)entry.position;
        body->positionAng = body->initialAng;
#endif
        experiment->islands.directContacts[index] = 1;
    }
    return true;
}

static void commitColumns(Experiment* experiment)
{
    const auto& entries = experiment->contactColumns.bodies;
    for (int index = 0; index < (int)entries.size(); ++index)
    {
        if (!experiment->islands.directContacts[index]) continue;
        Rigid* body = experiment->bodies[index];
        const auto& entry = entries[index];
        double velocity = (entry.position - height(initialCenter(body))) / experiment->solver.dt;
#ifdef DEMO_2D
        experiment->islands.contactVelocities[index] = {entry.tangentVelocity.x, (float)velocity, 0};
#else
        experiment->islands.contactVelocities[index] = {entry.tangentVelocity.x, entry.tangentVelocity.y, (float)velocity};
#endif
        ++experiment->islands.contactSolves;
        if (entry.frictionPatch >= 0) continue;
        if (!entry.below) continue;
        Manifold* manifold = entry.below;
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            float pressure = (float)(entry.force * entry.weights[point]);
#ifdef DEMO_2D
            manifold->lambda[point * 2] = -pressure; manifold->lambda[point * 2 + 1] = 0;
#else
            manifold->contacts[point].lambda = {-pressure, 0, 0}; manifold->contacts[point].stick = true;
#endif
            if (pressure > 0) researchTransferContactPressure(manifold, point);
        }
        clearContactReactions(experiment, manifold);
        experiment->contactForces.erase(manifold);
    }
    researchCommitColumnFriction(experiment);
}

// Parallel horizontal contact planes and a chain of dynamic bodies reduce the
// translational implicit Euler QP to weighted isotonic regression. Pooling is
// linear in the chain length and retains all original masses and surface gaps.
void researchSolveContactColumns()
{
    Experiment* experiment = researchStep->experiment;
    auto& islands = experiment->islands;
    auto& columns = experiment->contactColumns;
    auto& entries = columns.bodies;
    columns.poses.resize(experiment->bodies.size());
    for (int index = 0; index < (int)columns.poses.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
#ifdef DEMO_2D
        columns.poses[index] = {{body->position.x, body->position.y, 0}, body->position.z};
#else
        columns.poses[index] = {body->positionLin, body->positionAng};
#endif
    }
    for (;;)
    {
        for (int index = 0; index < (int)columns.poses.size(); ++index)
        {
            if (!islands.directContacts[index]) continue;
            Rigid* body = experiment->bodies[index];
            const auto& pose = columns.poses[index];
#ifdef DEMO_2D
            body->position = {pose.center.x, pose.center.y, pose.angle};
#else
            body->positionLin = pose.center; body->positionAng = pose.rotation;
#endif
        }
        std::fill(islands.directContacts.begin(), islands.directContacts.end(), 0);
        entries.assign(experiment->bodies.size(), {});
        columns.patches.clear();
        std::fill(islands.ready.begin(), islands.ready.end(), 1);
        for (int index = 0; index < (int)entries.size(); ++index)
        {
            if (!researchBodySolves(experiment->bodies[index]) || !islands.ready[islands.roots[index]]) continue;
            if (!readColumnBody(experiment, index)) islands.ready[islands.roots[index]] = 0;
        }
        for (int index = 0; index < (int)entries.size(); ++index)
        {
            if (!researchBodySolves(experiment->bodies[index]) || !islands.ready[islands.roots[index]]) continue;
            if (entries[index].lower >= 0 && experiment->bodies[entries[index].lower]->mass > 0) continue;
            solveColumn(experiment, index);
        }
        bool changedCandidates = false;
        for (int index = 0; index < (int)entries.size(); ++index)
        {
            if (!islands.directContacts[index]) continue;
            const auto& entry = entries[index];
#ifdef DEMO_2D
            float3 velocity{experiment->bodies[index]->velocity.x, 0, 0};
#else
            const auto& bodyVelocity = experiment->bodies[index]->velocityLin;
            float3 velocity{bodyVelocity.x, bodyVelocity.y, 0};
#endif
            double travel = fabs(entry.position - entry.predicted)
                + length(entry.tangentVelocity - velocity) * experiment->solver.dt;
            changedCandidates |= travel > COLLISION_MARGIN * 0.5;
        }
        if (!changedCandidates || !researchAddContacts()) break;
        researchBuildIslands();
    }
    commitColumns(experiment);
}

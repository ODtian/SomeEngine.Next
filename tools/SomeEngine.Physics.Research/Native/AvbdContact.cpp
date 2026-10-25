// Portions adapted from Newton v1.7.0, Copyright (c) 2025 The Newton Developers
// (Apache-2.0). Multi-contact reduction derives from Jitter Physics 2,
// Copyright (c) Thorben Linneweber and contributors (MIT).
// License texts: References/Newton-LICENSE.md and References/Jitter-LICENSE.md.
#include "BridgeState.h"
#include "ResearchStep.h"

float researchContactResponse(Rigid* body, float3 point, float3 direction)
{
    if (body->mass <= 0) return 0;
#ifdef DEMO_2D
    float3 offset = point - float3{body->position.x, body->position.y, 0};
    float angular = offset.x * direction.y - offset.y * direction.x;
    return 1 / body->mass + angular * angular / body->moment;
#else
    float3 angular = cross(point - body->positionLin, direction);
    return 1 / body->mass + ((researchStep->experiment->newton.compliant || (!researchStep->experiment->researchExtensions && !researchStructuralBody(body))) ? dot(angular, newtonInverseInertia(body, angular))
        : angular.x * angular.x / body->moment.x + angular.y * angular.y / body->moment.y + angular.z * angular.z / body->moment.z);
#endif
}

void researchInitializeContact(Manifold* manifold, int point)
{
#ifndef DEMO_2D
    static_cast<ContactManifold*>(manifold)->offsetsValid = false;
#endif
    auto& contact = manifold->contacts[point];
    float dt = manifold->bodyA->solver->dt;
#ifdef DEMO_2D
    auto response = [](Rigid* body, float3 jacobian)
    {
        return body->mass > 0 ? (jacobian.x * jacobian.x + jacobian.y * jacobian.y) / body->mass
            + jacobian.z * jacobian.z / body->moment : 0.0f;
    };
    for (int axis = 0; axis < 2; ++axis)
    {
        int row = point * 2 + axis;
        // Newton v1.7 rigid_vbd_kernels.py uses one rho for both hard-contact axes.
        if (axis == 1 && !researchStep->experiment->researchExtensions)
        {
            manifold->penalty[row] = manifold->penalty[point * 2];
            continue;
        }
        if (manifold->penalty[row] != 0) continue;
        float3 a = axis == 0 ? contact.JAn : contact.JAt;
        float3 b = axis == 0 ? contact.JBn : contact.JBt;
        float inverseMass = response(manifold->bodyA, a) + response(manifold->bodyB, b);
        manifold->penalty[row] = clamp(1.0f / (dt * dt * inverseMass), PENALTY_MIN, PENALTY_MAX);
    }
#else
    if (contact.penalty.x != 0)
    {
        if (researchNewtonContact(manifold)) contact.penalty.y = contact.penalty.z = contact.penalty.x;
        return;
    }
    float3 pointA = transform(manifold->bodyA->positionLin, manifold->bodyA->positionAng, contact.rA);
    float3 pointB = transform(manifold->bodyB->positionLin, manifold->bodyB->positionAng, contact.rB);
    float inverseMass = researchContactResponse(manifold->bodyA, pointA, manifold->basis[0])
        + researchContactResponse(manifold->bodyB, pointB, manifold->basis[0]);
    contact.penalty.x = clamp(1.0f / (dt * dt * inverseMass), PENALTY_MIN, PENALTY_MAX);
    if (researchNewtonContact(manifold)) { contact.penalty.y = contact.penalty.z = contact.penalty.x; return; }
    double first = 0, second = 0, mixed = 0;
    for (Rigid* body : {manifold->bodyA, manifold->bodyB})
    {
        if (body->mass <= 0) continue;
        float3 point = body == manifold->bodyA ? pointA : pointB;
        float3 a = cross(point - body->positionLin, manifold->basis[1]);
        float3 b = cross(point - body->positionLin, manifold->basis[2]);
        first += 1.0 / body->mass;
        second += 1.0 / body->mass;
        for (int axis = 0; axis < 3; ++axis)
        {
            first += (double)a[axis] * a[axis] / body->moment[axis];
            second += (double)b[axis] * b[axis] / body->moment[axis];
            mixed += (double)a[axis] * b[axis] / body->moment[axis];
        }
    }
    double response = 0.5 * (first + second + sqrt((first - second) * (first - second) + 4 * mixed * mixed));
    float tangent = clamp((float)(1 / (dt * dt * response)), PENALTY_MIN, PENALTY_MAX);
    contact.penalty.y = contact.penalty.z = tangent;
#endif
}

#ifdef DEMO_2D
// Batched Giles AVBD contact terms: retain its positive angular curvature bound.
// Newton's hard-contact tangent metric remains positive during Coulomb sliding.
void researchContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& hessian, float3& gradient)
{
    manifold->computeConstraint(alpha);
    bool supported = researchSupportedContact(manifold);
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        const auto& contact = manifold->contacts[point];
        int normal = point * 2, tangent = normal + 1;
        float rho = manifold->penalty[normal];
        float normalForce = min(0.0f, manifold->lambda[normal] + rho * manifold->C[normal]);
        float tangentForce = clamp(manifold->lambda[tangent] + rho * manifold->C[tangent], manifold->fmin[tangent], manifold->fmax[tangent]);
        float3 normalJacobian = body == manifold->bodyA ? contact.JAn : contact.JBn;
        float3 tangentJacobian = body == manifold->bodyA ? contact.JAt : contact.JBt;
        gradient += normalJacobian * normalForce + tangentJacobian * tangentForce;
        if (!supported || normalForce != 0) hessian += outer(normalJacobian, normalJacobian * rho);
        if (manifold->fmin[tangent] != manifold->fmax[tangent]) hessian += outer(tangentJacobian, tangentJacobian * rho);
        hessian[2][2] += fabsf(tangentJacobian.z * normalForce) + fabsf(normalJacobian.z * tangentForce);
    }
}

// Newton v1.7 update_duals_body_body_contacts: project both axes with the same
// old rho, then ramp rho from the raw gap. LRC contributes its own reaction.
void researchUpdateContactPressure(Manifold* manifold, float alpha)
{
    manifold->computeConstraint(alpha);
    auto& geometry = *static_cast<ContactManifold*>(manifold);
    geometry.penetration = 0;
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        int normal = point * 2, tangent = normal + 1;
        float rho = manifold->penalty[normal];
        float3 extra = researchContactChange(manifold, point);
        manifold->lambda[normal] = min(0.0f, manifold->lambda[normal] + rho * manifold->C[normal]);
        float bound = -min(0.0f, manifold->lambda[normal] + extra.x) * manifold->friction;
        manifold->lambda[tangent] = clamp(manifold->lambda[tangent] + rho * manifold->C[tangent], -bound - extra.y, bound - extra.y);
        const auto& contact = manifold->contacts[point];
        float raw = dot(contact.normal, manifold->bodyA->position.xy() - manifold->bodyB->position.xy()) - contact.JAt.z - contact.JBt.z;
        geometry.penetration = max(geometry.penetration, -raw);
        manifold->penalty[normal] = min(rho + manifold->solver->beta * max(0.0f, -raw), PENALTY_MAX);
        // Project policy: maintain the existing pressure/margin conditioning
        // after every dual ascent as well as after a support reaction transfer.
        researchTransferContactPressure(manifold, point);
    }
}

float researchContactPenalty(Force* force, int row, float value)
{
    if (row % 2 != 0 && (value == force->fmin[row] || value == force->fmax[row])
        && dynamic_cast<Manifold*>(force) && researchStep->experiment->researchExtensions) return 0;
    // AVBD keeps the clamped normal potential's K upper bound across activation.
    // Support height constraints supply the response for supported normals.
    if ((row % 2 == 0 ? value == 0 : force->fmin[row] == force->fmax[row])
        && dynamic_cast<Manifold*>(force)
        && (row % 2 != 0 || researchSupportedContact(static_cast<Manifold*>(force)))) return 0;
    return force->penalty[row];
}
#endif

#ifndef DEMO_2D
// Newton v1.7.0 multicontact.py::approx_max_quadrilateral_area_with_calipers.
// The box clipper supplies an ordered convex loop; its tangent-plane projection
// supplies the same signed areas and squared distances as Newton's 2D hull.
static float contactNormalGap(Manifold* manifold, const Manifold::Contact& contact)
{
    Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
    return dot(manifold->basis[0], a->positionLin - b->positionLin
        + rotate(a->positionAng, contact.rA) - rotate(b->positionAng, contact.rB));
}

static bool contactPatchOverlaps(Manifold* manifold)
{
    Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
    return readBoxSeparation(a, b, a->positionLin, a->positionAng, b->positionLin, b->positionAng) < -contactGeometryAccuracy;
}

static int reduceNewtonContactPatch(Manifold* manifold, Manifold::Contact* contacts, int count)
{
    if (count <= 4) return count;
    float3 normal = rotate(conjugate(manifold->bodyA->positionAng), manifold->basis[0]);
    float orientation = 0;
    for (int point = 0; point < count; ++point)
        orientation += dot(normal, cross(contacts[point].rA, contacts[(point + 1) % count].rA));
    if (orientation < 0) normal = -normal;
    auto area = [&](int first, int second, int third)
    {
        return dot(normal, cross(contacts[second].rA - contacts[first].rA, contacts[third].rA - contacts[first].rA));
    };
    auto distance = [&](int first, int second)
    {
        float3 difference = contacts[first].rA - contacts[second].rA;
        float height = dot(normal, difference);
        return lengthSq(difference) - height * height;
    };
    constexpr float tieRatio = 1.001f;
    int first = 0, third = 1, opposite = 1;
    float diameter = distance(first, third);
    for (int point = 0; point < count; ++point)
    {
        int next = (point + 1) % count;
        while (area(point, next, (opposite + 1) % count) > area(point, next, opposite))
            opposite = (opposite + 1) % count;
        float current = distance(point, opposite);
        if (current > diameter * tieRatio) { diameter = current; first = point; third = opposite; }
        current = distance(next, opposite);
        if (current > diameter * tieRatio) { diameter = current; first = next; third = opposite; }
    }
    int second = 0, fourth = 0;
    float positiveArea = 0, negativeArea = 0;
    for (int point = 0; point < count; ++point)
    {
        float current = area(first, third, point);
        if (current > positiveArea * tieRatio) { positiveArea = current; second = point; }
        else if (-current > negativeArea * tieRatio) { negativeArea = -current; fourth = point; }
    }
    int selected[4]{first, second, third, fourth};
    int deepest = 0;
    float deepestGap = contactNormalGap(manifold, contacts[0]);
    for (int point = 1; point < count; ++point)
    {
        float gap = contactNormalGap(manifold, contacts[point]);
        if (gap < deepestGap) { deepest = point; deepestGap = gap; }
    }
    float selectedGap = INFINITY;
    for (int point : selected) selectedGap = min(selectedGap, contactNormalGap(manifold, contacts[point]));
    // Area coverage also retains a penetrating extremum of the full polygon.
    if (deepestGap < -contactGeometryAccuracy && selectedGap - deepestGap > contactGeometryAccuracy
        && contactPatchOverlaps(manifold))
    {
        int retained[4]{};
        float maximumArea = -INFINITY;
        for (int replaced = 0; replaced < 4; ++replaced)
        {
            int candidate[4]{first, second, third, fourth};
            candidate[replaced] = deepest;
            std::sort(candidate, candidate + 4);
            float coveredArea = area(candidate[0], candidate[1], candidate[2]) + area(candidate[0], candidate[2], candidate[3]);
            if (coveredArea > maximumArea * tieRatio)
            { maximumArea = coveredArea; std::copy_n(candidate, 4, retained); }
        }
        std::copy_n(retained, 4, selected);
    }
    Manifold::Contact reduced[4]{contacts[selected[0]], contacts[selected[1]], contacts[selected[2]], contacts[selected[3]]};
    for (int point = 0; point < 4; ++point) contacts[point] = reduced[point];
    return 4;
}

int researchContactPatch(Manifold* manifold, Manifold::Contact* contacts, int count)
{
    if (manifold->solver->gravity == 0 || !researchNewtonContact(manifold))
        count = reduceNewtonContactPatch(manifold, contacts, count);
    if (manifold->solver->gravity == 0) return count;
    if (count < 3 || manifold->bodyA->mass == 0 || manifold->bodyB->mass == 0) return count;
    constexpr int capacity = sizeof(manifold->contacts) / sizeof(manifold->contacts[0]);
    Manifold::Contact interior{};
    for (int point = 0; point < count; ++point)
    {
        interior.rA += contacts[point].rA;
        interior.rB += contacts[point].rB;
    }
    interior.rA = interior.rA / (float)count;
    interior.rB = interior.rB / (float)count;
    interior.feature.key = (contacts[0].feature.key & 0xFFFFFF00) | 0x80;
    if (count == capacity)
    {
        float3 normal = rotate(conjugate(manifold->bodyA->positionAng), manifold->basis[0]);
        float minimumArea = INFINITY;
        int removed = 0;
        for (int point = 0; point < count; ++point)
        {
            const auto& previous = contacts[(point + count - 1) % count];
            const auto& current = contacts[point];
            const auto& next = contacts[(point + 1) % count];
            float area = fabsf(dot(normal, cross(previous.rA - current.rA, next.rA - current.rA)));
            if (area < minimumArea) { minimumArea = area; removed = point; }
        }
        float removedGap = contactNormalGap(manifold, contacts[removed]);
        float retainedGap = INFINITY;
        for (int point = 0; point < count; ++point)
            if (point != removed) retainedGap = min(retainedGap, contactNormalGap(manifold, contacts[point]));
        if (removedGap < -contactGeometryAccuracy && retainedGap - removedGap > contactGeometryAccuracy
            && contactPatchOverlaps(manifold))
        {
            int deepest = removed;
            minimumArea = INFINITY;
            for (int point = 0; point < count; ++point)
            {
                if (point == deepest) continue;
                const auto& previous = contacts[(point + count - 1) % count];
                const auto& current = contacts[point];
                const auto& next = contacts[(point + 1) % count];
                float area = fabsf(dot(normal, cross(previous.rA - current.rA, next.rA - current.rA)));
                if (area < minimumArea) { minimumArea = area; removed = point; }
            }
        }
        for (int point = removed; point + 1 < count; ++point) contacts[point] = contacts[point + 1];
        --count;
    }
    contacts[count++] = interior;
    return count;
}

void researchContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& crossBlock, float3& gradientLin, float3& gradientAng)
{
    if (!researchPostStabilize() && alpha != 0) alpha = rigidContactAlpha;
    auto* experiment = researchStep->experiment;
    auto* changes = findContactReactions(manifold);
    const auto* extras = changes == nullptr ? nullptr : &(*changes);
    bool supported = researchSupportedContact(manifold);
    float3 normal = manifold->basis[0];
    bool first = body == manifold->bodyA;
    const auto& geometry = readContactOffsets(manifold);
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto& contact = manifold->contacts[point];
        float3 rA = geometry.offsetsA[point];
        float3 rB = geometry.offsetsB[point];
        float3 raw = manifold->basis * (manifold->bodyA->positionLin - manifold->bodyB->positionLin + rA - rB);
        float3 residual = raw - float3{researchContactNormalStart(contact.C0.x), contact.C0.y, contact.C0.z} * alpha;
        if (raw.x > 0) residual.x = raw.x;
        float rho = contact.penalty.x;
        float3 force = contact.lambda + residual * rho;
        force.x = min(force.x, 0.0f);
        float3 extra = extras ? (*extras)[point] : float3{0, 0, 0};
        if (raw.x > 0 && contact.lambda.x >= 0 && extra.x >= 0) continue;
        float bound = -min(0.0f, force.x + extra.x) * manifold->friction;
        float magnitude = length(float2{force.y + extra.y, force.z + extra.z});
        if (magnitude > bound)
        {
            float scale = bound / magnitude;
            force.y = (force.y + extra.y) * scale - extra.y;
            force.z = (force.z + extra.z) * scale - extra.z;
        }
        float3 r = first ? rA : rB;
        float3 worldForce = transpose(manifold->basis) * force * (first ? 1.0f : -1.0f);
        gradientLin += worldForce;
        gradientAng += cross(r, worldForce);
        // Giles AVBD's positive column-norm bound for rotational curvature.
        float product = dot(r, worldForce);
        for (int axis = 0; axis < 3; ++axis)
        {
            float3 column = r * worldForce[axis];
            column[axis] -= product;
            angular[axis][axis] += length(column);
        }
        bool normalMetric = !supported || force.x != 0;
        bool tangentMetric = bound > 0;
        // Newton legacy hard contact: K_n + K_t = rho * I when both are live.
        // Expand J^T K J once in world coordinates for the original body block.
        if (normalMetric && tangentMetric)
        {
            linear[0][0] += rho; linear[1][1] += rho; linear[2][2] += rho;
            float3 scaled = r * rho;
            angular[0][0] += scaled.y * r.y + scaled.z * r.z;
            angular[1][0] -= scaled.y * r.x;
            angular[1][1] += scaled.x * r.x + scaled.z * r.z;
            angular[2][0] -= scaled.z * r.x;
            angular[2][1] -= scaled.z * r.y;
            angular[2][2] += scaled.x * r.x + scaled.y * r.y;
            crossBlock[0][1] -= scaled.z; crossBlock[0][2] += scaled.y;
            crossBlock[1][0] += scaled.z; crossBlock[1][2] -= scaled.x;
            crossBlock[2][0] -= scaled.y; crossBlock[2][1] += scaled.x;
            continue;
        }
        for (int axis = 0; axis < 3; ++axis)
        {
            if (axis == 0 ? !normalMetric : !tangentMetric) continue;
            float3 direction = manifold->basis[axis];
            float3 angularDirection = cross(r, direction);
            researchAddBodyProduct(linear, direction * rho, direction);
            researchAddBodyProduct(angular, angularDirection * rho, angularDirection);
            crossBlock += outer(angularDirection * rho, direction);
        }
        // Newton rigid_vbd_kernels.py forms J^T K J for the angular block.
    }
}

void researchUpdateContactPressure(Manifold* manifold, float alpha)
{
    if (!researchPostStabilize() && alpha != 0) alpha = rigidContactAlpha;
    auto* experiment = researchStep->experiment;
    auto& geometry = readContactOffsets(manifold);
    geometry.penetration = 0;
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto& contact = manifold->contacts[point];
        float3 rA = geometry.offsetsA[point];
        float3 rB = geometry.offsetsB[point];
        float3 raw = manifold->basis * (manifold->bodyA->positionLin - manifold->bodyB->positionLin + rA - rB);
        geometry.penetration = max(geometry.penetration, -raw.x);
        float3 residual = raw - float3{researchContactNormalStart(contact.C0.x), contact.C0.y, contact.C0.z} * alpha;
        if (raw.x > 0) residual.x = raw.x;
        float rho = contact.penalty.x;
        float3 force = contact.lambda + residual * rho;
        force.x = min(force.x, 0.0f);
        float3 extra = researchContactChange(manifold, point);
        float bound = -min(0.0f, force.x + extra.x) * manifold->friction;
        float tangent = length(float2{force.y + extra.y, force.z + extra.z});
        if (tangent > bound)
        {
            float scale = bound / tangent;
            force.y = (force.y + extra.y) * scale - extra.y;
            force.z = (force.z + extra.z) * scale - extra.z;
        }
        contact.lambda = force;
        contact.stick = tangent <= bound && length(float2{residual.y, residual.z}) < STICK_THRESH;
        contact.penalty.x = min(rho + manifold->solver->betaLin * max(0.0f, -raw.x), PENALTY_MAX);
        researchTransferContactPressure(manifold, point);
        contact.penalty.y = contact.penalty.z = contact.penalty.x;
    }
}

void researchIncreaseFrictionPenalty(Manifold* manifold, int point, float2 travel)
{
    auto& penalty = manifold->contacts[point].penalty;
    float tangent = min(penalty.y + manifold->solver->betaLin * length(travel), PENALTY_MAX);
    penalty.y = penalty.z = tangent;
}

// Batched assembly of the Giles contact block: J^T K J, its projected
// Coulomb tangent metric and the original positive angular curvature bound.
void researchPairedContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& crossBlock, float3& gradientLin, float3& gradientAng)
{
    const auto& basis = manifold->basis;
    bool first = body == manifold->bodyA;
    bool supported = researchSupportedContact(manifold);
    auto* changes = findContactReactions(manifold);
    const auto* extras = changes == nullptr ? nullptr : &(*changes);
    quat rotationA = manifold->bodyA->positionAng, rotationB = manifold->bodyB->positionAng;
    float3 normal = basis[0];
    float3 localNormalA = rotate(conjugate(rotationA), normal), localNormalB = rotate(conjugate(rotationB), normal);
    float normalOrigin = dot(normal, manifold->bodyA->positionLin - manifold->bodyB->positionLin);
    const auto& geometry = readContactOffsets(manifold);
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        const auto& contact = manifold->contacts[point];
        float3 extra = extras ? (*extras)[point] : float3{0, 0, 0};
        float separated = normalOrigin + dot(localNormalA, contact.rA) - dot(localNormalB, contact.rB);
        if (separated > constraintAccuracy && contact.lambda.x >= 0 && extra.x == 0 && extra.y == 0 && extra.z == 0)
        {
            if (!supported)
            {
                float3 r = first ? geometry.offsetsA[point] : geometry.offsetsB[point];
                float3 turn = cross(r, normal);
                researchAddBodyMetric(linear, normal, contact.penalty.x);
                researchAddBodyMetric(angular, turn, contact.penalty.x);
                crossBlock += outer(turn, normal) * contact.penalty.x;
            }
            continue;
        }
        float3 rA = geometry.offsetsA[point];
        float3 rB = geometry.offsetsB[point];
        float3 residual = basis * (manifold->bodyA->positionLin + rA - manifold->bodyB->positionLin - rB)
            - float3{researchContactNormalStart(contact.C0.x), contact.C0.y, contact.C0.z} * alpha;
        float3 force{contact.lambda.x + contact.penalty.x * residual.x,
            contact.lambda.y + contact.penalty.y * residual.y, contact.lambda.z + contact.penalty.z * residual.z};
        force.x = min(0.0f, force.x);
        float bound = fabsf(force.x + extra.x) * manifold->friction;
        float magnitude = length(float2{force.y + extra.y, force.z + extra.z});
        if (magnitude > bound && magnitude > 0)
        {
            force.y = (force.y + extra.y) * bound / magnitude - extra.y;
            force.z = (force.z + extra.z) * bound / magnitude - extra.z;
        }
        float3 r = first ? rA : rB;
        float sign = first ? 1.0f : -1.0f;
        float3 rows[3]{cross(r, basis[0]), cross(r, basis[1]), cross(r, basis[2])};
        gradientLin += transpose(basis) * force * sign;
        gradientAng += float3{rows[0].x * force.x + rows[1].x * force.y + rows[2].x * force.z,
            rows[0].y * force.x + rows[1].y * force.y + rows[2].y * force.z,
            rows[0].z * force.x + rows[1].z * force.y + rows[2].z * force.z} * sign;
        float curvature = length(force + extra) * length(r);
        for (int axis = 0; axis < 3; ++axis) angular[axis][axis] += curvature;
        auto addMetric = [&](float3 direction, float3 turn, float stiffness)
        {
            researchAddBodyMetric(linear, direction, stiffness);
            researchAddBodyMetric(angular, turn, stiffness);
            crossBlock += outer(turn, direction) * stiffness;
        };
        if (!supported || force.x != 0) addMetric(basis[0], rows[0], contact.penalty.x);
        if (bound > 0)
        {
            if (magnitude <= bound)
            {
                addMetric(basis[1], rows[1], contact.penalty.y);
                addMetric(basis[2], rows[2], contact.penalty.z);
            }
            else
            {
                float x = (force.y + extra.y) / bound, y = (force.z + extra.z) / bound;
                float3 direction = basis[1] * y - basis[2] * x;
                addMetric(direction, cross(r, direction), contact.penalty.y * bound / magnitude);
            }
        }
    }
}

void researchAdjustFriction(float3x3& hessian, float3 force, float3 extra, float bound, float magnitude)
{
    // An isotropic penalty and the Coulomb disk projection supply a rank-one
    // positive-semidefinite response along the disk tangent during sliding.
    if (bound == 0)
    {
        hessian[1][1] = hessian[2][2] = 0;
        return;
    }
    if (magnitude <= bound) return;
    float x = (force.y + extra.y) / bound, y = (force.z + extra.z) / bound;
    float tangent = hessian[1][1] * bound / magnitude;
    hessian[1][1] = tangent * y * y;
    hessian[2][2] = tangent * x * x;
    hessian[1][2] = hessian[2][1] = -tangent * x * y;
}

#endif

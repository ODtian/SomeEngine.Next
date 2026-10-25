#include "BridgeState.h"
#include "ResearchStep.h"

#define EXPORT extern "C" __declspec(dllexport)

void researchInitializeJointBounds(Experiment* experiment);

void removeDemoJoint(Experiment* experiment, Joint* joint)
{
    if (joint->bodyA) wakeBody(experiment, experiment->index(joint->bodyA));
    wakeBody(experiment, experiment->index(joint->bodyB));
    auto& joints = experiment->joints;
    joints.erase(std::find(joints.begin(), joints.end(), joint));
    experiment->newton.joints.erase(joint);
    ++experiment->jointVersion;
    experiment->longRange = {};
    clearContactReactions(experiment);
}

void researchRemoveForce(Force* force)
{
    researchStep->experiment->bodyForceContactVersion = -1;
    if (auto* joint = dynamic_cast<Joint*>(force)) removeDemoJoint(researchStep->experiment, joint);
    else if (auto* manifold = dynamic_cast<Manifold*>(force))
    {
        if (manifold->numContacts > 0) ++researchStep->experiment->contactVersion;
        clearContactReactions(researchStep->experiment, manifold);
        researchStep->experiment->contactForces.erase(manifold);


    }
}

static float3 readVector(const double* value)
{
    return {(float)value[0], (float)value[1], (float)value[2]};
}

static float3 readUpDirection()
{
#ifdef DEMO_2D
    return {0, 1, 0};
#else
    return {0, 0, 1};
#endif
}

static float3 rotateOffset(Rigid* body, float3 local)
{
#ifdef DEMO_2D
    float2 value = rotate(body->position.z, {local.x, local.y});
    return {value.x, value.y, 0};
#else
    return rotate(body->positionAng, local);
#endif
}

static float3 transformPoint(Experiment* experiment, int body, const double* local)
{
    float3 value = readVector(local);
    if (body < 0) return value;
    Rigid* rigid = experiment->bodies[body];
#ifdef DEMO_2D
    float3 center = {rigid->position.x, rigid->position.y, 0};
#else
    float3 center = rigid->positionLin;
#endif
    return center + rotateOffset(rigid, value);
}

static float3 transformInitialPoint(Experiment* experiment, int body, const double* local)
{
    float3 value = readVector(local);
    if (body < 0) return value;
    Rigid* rigid = experiment->bodies[body];
#ifdef DEMO_2D
    float2 rotated = rotate(rigid->initial.z, {value.x, value.y});
    return {rigid->initial.x + rotated.x, rigid->initial.y + rotated.y, 0};
#else
    return rigid->initialLin + rotate(rigid->initialAng, value);
#endif
}

static void updateJointAnchor(Experiment* experiment, LongRangeData::JointAnchor& anchor, bool initial)
{
    if (anchor.body < 0)
    {
        anchor.offset = {0, 0, 0}; anchor.point = anchor.initialPoint = anchor.local;
        return;
    }
    Rigid* body = experiment->bodies[anchor.body];
    anchor.offset = rotateOffset(body, anchor.local);
#ifdef DEMO_2D
    anchor.point = float3{body->position.x, body->position.y, 0} + anchor.offset;
    if (initial)
    {
        float2 rotated = rotate(body->initial.z, {anchor.local.x, anchor.local.y});
        anchor.initialPoint = {body->initial.x + rotated.x, body->initial.y + rotated.y, 0};
    }
#else
    anchor.point = body->positionLin + anchor.offset;
    if (initial) anchor.initialPoint = body->initialLin + rotate(body->initialAng, anchor.local);
#endif
}

void researchBodyChanged(int body)
{
    Experiment* experiment = researchStep->experiment;
    auto& data = experiment->longRange;
    if (data.jointBounds.empty()) return;
    for (int index : data.bodyAnchors[body]) updateJointAnchor(experiment, data.jointAnchors[index], false);
}

void researchBodyChanged(Rigid* body)
{
    if (researchStep->experiment->longRange.jointBounds.empty()) return;
    researchBodyChanged(researchStep->experiment->index(body));
}

static void readStartErrors(Experiment* experiment, int first)
{
    auto& data = experiment->longRange;
    data.startErrors.resize(data.ranges.size());
    data.rangeForces.resize(data.ranges.size(), float2{0, 0});
    for (int i = first; i < (int)data.ranges.size(); ++i)
    {
        const auto& range = data.ranges[i];
        float3 delta;
        if (i < data.jointRangeCount)
        {
            const auto& bound = data.jointBounds[i];
            delta = data.jointAnchors[bound.anchorB].initialPoint - data.jointAnchors[bound.anchorA].initialPoint;
        }
        else delta = transformInitialPoint(experiment, range.bodyB, range.pointB) - transformInitialPoint(experiment, range.bodyA, range.pointA);
        if (range.kind == 1)
            data.startErrors[i] = {std::max(0.0f, (float)range.minimum - dot(delta, readUpDirection())), 0};
        else
        {
            float distance = length(delta);
            data.startErrors[i] = {std::max(0.0f, distance - (float)range.maximum), std::max(0.0f, (float)range.minimum - distance)};
        }
    }
}

static void copy(double* destination, float3 value)
{
    destination[0] = value.x; destination[1] = value.y; destination[2] = value.z;
}


EXPORT int demo_joints(Experiment* experiment, JointData* output)
{
    int count = 0;
    for (int i = 0; i < (int)experiment->joints.size(); ++i)
    {
        Joint* joint = experiment->joints[i];
#ifdef DEMO_2D
        if (!isinf(joint->stiffness[0]) || !isinf(joint->stiffness[1])) continue;
#else
        if (!isinf(joint->stiffnessLin) || joint->broken) continue;
#endif
        if (output)
        {
            JointData& data = output[count];
            data = {};
            data.bodyA = experiment->index(joint->bodyA);
            data.bodyB = experiment->index(joint->bodyB);
            data.source = i;
#ifdef DEMO_2D
            data.angularFixed = isinf(joint->stiffness[2]);
            copy(data.pointA, {joint->rA.x, joint->rA.y, 0});
            copy(data.pointB, {joint->rB.x, joint->rB.y, 0});
#else
            data.angularFixed = isinf(joint->stiffnessAng);
            copy(data.pointA, joint->rA); copy(data.pointB, joint->rB);
#endif
        }
        ++count;
    }
    return count;
}

EXPORT int demo_support_contacts(Experiment* experiment, SupportContact** output)
{
    experiment->supportContacts.clear();
    experiment->contactReferences.clear();
    experiment->supportGraph.manifoldCount = 0;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (researchStep && !researchForceSolves(force)) continue;
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        ++experiment->supportGraph.manifoldCount;
        Rigid* bodyA = manifold->bodyA;
        Rigid* bodyB = manifold->bodyB;
        const auto* endpoints = static_cast<ContactManifold*>(manifold);
        int indexA = endpoints->indexA, indexB = endpoints->indexB;
        auto* stored = findContactReactions(manifold);
        const auto* changes = stored == nullptr ? nullptr : &(*stored);
#ifndef DEMO_2D
        const auto& geometry = readContactOffsets(manifold);
#endif
        for (int i = 0; i < manifold->numContacts; ++i)
        {
            auto& contact = manifold->contacts[i];
            float3 extra = changes ? (*changes)[i] : float3{0, 0, 0};
            SupportContact data{};
            data.bodyA = indexA;
            data.bodyB = indexB;
            data.source = (int)experiment->contactReferences.size();
            data.friction = manifold->friction;
#ifdef DEMO_2D
            float2 rA = rotate(bodyA->position.z, contact.rA);
            float2 rB = rotate(bodyB->position.z, contact.rB);
            float3 pointA{bodyA->position.x + rA.x, bodyA->position.y + rA.y, 0};
            float3 pointB{bodyB->position.x + rB.x, bodyB->position.y + rB.y, 0};
            float3 normal = {contact.normal.x, contact.normal.y, 0};
            float3 tangent = {normal.y, -normal.x, 0};
            float normalForce = manifold->lambda[i * 2] + extra.x, tangentForce = manifold->lambda[i * 2 + 1] + extra.y;
            copy(data.force, normal * normalForce + tangent * tangentForce);
            float tangentTravel = dot(tangent, pointA - pointB) - contact.C0.y;
            float travelLength = fabsf(tangentTravel);
#else
            float3 pointA = bodyA->positionLin + geometry.offsetsA[i];
            float3 pointB = bodyB->positionLin + geometry.offsetsB[i];
            float3 normal = manifold->basis[0];
            float3 total = contact.lambda + extra;
            copy(data.force, transpose(manifold->basis) * total);
            float3 gap = pointA - pointB;
            float2 tangentTravel{dot(manifold->basis[1], gap) - contact.C0.y,
                dot(manifold->basis[2], gap) - contact.C0.z};
            float travelLength = length(tangentTravel);
#endif
            copy(data.pointA, pointA); copy(data.pointB, pointB);
            data.sticks = travelLength < STICK_THRESH;
            if (!data.sticks)
            {
#ifdef DEMO_2D
                float3 direction = tangent * (tangentTravel / travelLength);
#else
                float3 direction = (manifold->basis[1] * tangentTravel.x + manifold->basis[2] * tangentTravel.y) / travelLength;
#endif
                float response = researchContactResponse(bodyA, pointA, direction)
                    + researchContactResponse(bodyB, pointB, direction);
                float pressure = fabsf(dot(readVector(data.force), normal));
                float dt = experiment->solver.dt;
                data.sticks = travelLength < STICK_THRESH + manifold->friction * pressure * response * dt * dt;
            }
            copy(data.normal, normal);
            experiment->supportContacts.push_back(data);
            experiment->contactReferences.push_back({manifold, i});
        }
    }
    *output = experiment->supportContacts.data();
    return (int)experiment->supportContacts.size();
}

void indexLongRange(Experiment* experiment)
{
    auto& data = experiment->longRange;
    data.bodySupports.resize(experiment->bodies.size());
    for (auto& ranges : data.bodySupports) ranges.clear();
    for (int i = data.jointRangeCount; i < (int)data.ranges.size(); ++i)
    {
        const auto& range = data.ranges[i];
        data.bodySupports[range.bodyB].push_back(i);
    }
    for (auto& ranges : data.bodySupports)
        std::sort(ranges.begin(), ranges.end(), [&data](int a, int b)
        {
            float radiusA = lengthSq(readVector(data.ranges[a].pointB));
            float radiusB = lengthSq(readVector(data.ranges[b].pointB));
            return radiusA != radiusB ? radiusA < radiusB : a < b;
        });
}

void clearSupportRanges(Experiment* experiment)
{
    auto& data = experiment->longRange;
    data.ranges.resize(data.jointRangeCount);
    data.supports.clear(); data.loads.clear();
    for (auto& ranges : data.bodySupports) ranges.clear();
}

static int addJointAnchor(LongRangeData& data, int body, const double* point)
{
    float3 local = readVector(point);
    for (int i = 0; i < (int)data.jointAnchors.size(); ++i)
    {
        const auto& anchor = data.jointAnchors[i];
        if (anchor.body == body && anchor.local.x == local.x && anchor.local.y == local.y && anchor.local.z == local.z) return i;
    }
    int index = (int)data.jointAnchors.size();
    data.jointAnchors.push_back({body, local});
    if (body >= 0) data.bodyAnchors[body].push_back(index);
    return index;
}

EXPORT void demo_lrc_joints(Experiment* experiment, const DistanceRange* ranges, int count)
{
    auto& data = experiment->longRange;
    data.ranges.clear();
    data.enabled = true;
    if (count) data.ranges.assign(ranges, ranges + count);
    data.jointRangeCount = count;
    data.jointAnchors.clear(); data.jointBounds.clear();
    data.bodyAnchors.assign(experiment->bodies.size(), {});
    data.bodyJointRanges.assign(experiment->bodies.size(), {});
    for (int i = 0; i < count; ++i)
    {
        const auto& range = ranges[i];
        int first = addJointAnchor(data, range.bodyA, range.pointA);
        int last = addJointAnchor(data, range.bodyB, range.pointB);
        data.jointBounds.push_back({first, last, (float)range.minimum, (float)range.maximum, 0});
        if (range.bodyA >= 0) data.bodyJointRanges[range.bodyA].push_back(i);
        if (range.bodyB >= 0) data.bodyJointRanges[range.bodyB].push_back(i);
    }
    data.rangeForces.assign(count, float2{0, 0});
    indexLongRange(experiment);
}

EXPORT void demo_lrc_supports(Experiment* experiment, const DistanceRange* ranges, int count,
    const SupportNode* supports, int supportCount, const ContactLoad* loads, int loadCount)
{
    auto& data = experiment->longRange;
    data.ranges.resize(data.jointRangeCount);
    if (count) data.ranges.insert(data.ranges.end(), ranges, ranges + count);
    data.supports.clear(); data.loads.clear();
    if (supportCount) data.supports.assign(supports, supports + supportCount);
    if (loadCount) data.loads.assign(loads, loads + loadCount);
    researchFinishSupportRanges(experiment);
}

void researchBeginSupportRanges(Experiment* experiment)
{
    auto& data = experiment->longRange;
    readStartErrors(experiment, data.jointRangeCount);
    std::fill(data.rangeForces.begin() + data.jointRangeCount, data.rangeForces.end(), float2{0, 0});
}

void researchFinishSupportRanges(Experiment* experiment)
{
    auto& data = experiment->longRange;
    data.supportLoads.resize(data.supports.size());
    readStartErrors(experiment, data.jointRangeCount);
    indexLongRange(experiment);
}

float3 researchContactChange(Manifold* manifold, int contact)
{
    auto* stored = findContactReactions(manifold);
    return stored == nullptr ? float3{0, 0, 0} : (*stored)[contact];
}

bool researchContactTouches(Manifold* manifold)
{
    auto* experiment = researchStep->experiment;
    auto* changes = findContactReactions(manifold);
    for (int i = 0; i < manifold->numContacts; ++i)
    {
        const auto& contact = manifold->contacts[i];
        float pressure = changes == nullptr ? 0.0f : (*changes)[i].x;
#ifdef DEMO_2D
        pressure += manifold->lambda[i * 2];
        if (pressure < 0) return true;
        float2 gap = manifold->bodyA->position.xy() + rotate(manifold->bodyA->position.z, contact.rA)
            - manifold->bodyB->position.xy() - rotate(manifold->bodyB->position.z, contact.rB);
        if (dot(contact.normal, gap) <= 0.00001f) return true;
#else
        pressure += contact.lambda.x;
        if (pressure < 0) return true;
        float3 gap = transform(manifold->bodyA->positionLin, manifold->bodyA->positionAng, contact.rA)
            - transform(manifold->bodyB->positionLin, manifold->bodyB->positionAng, contact.rB);
        if (dot(manifold->basis[0], gap) <= 0.00001f) return true;
#endif
    }
    return false;
}

bool researchContactActive(Manifold* manifold)
{
    return researchForceSolves(manifold) && researchContactTouches(manifold);
}

bool researchSupports()
{
    if (researchStep->experiment->longRange.enabled && researchStep->experiment->lastStabilizationIterations > 0) return false;
    const auto& data = researchStep->experiment->longRange;
    return data.ranges.size() > data.jointRangeCount;
}


void researchTransferContactPressure(Manifold* manifold, int point)
{
    if (researchStep->experiment->newton.compliant) return;
#ifdef DEMO_2D
    float force = manifold->lambda[point * 2];
    float& penalty = manifold->penalty[point * 2];
#else
    float force = manifold->contacts[point].lambda.x + (researchNewtonContact(manifold) ? 0.0f : researchContactChange(manifold, point).x);
    float& penalty = manifold->contacts[point].penalty.x;
#endif
    // Project hard-row conditioning: bound the pressure/rho length scale by
    // the collision margin; the material model remains a rigid constraint.
    float lengthScale = COLLISION_MARGIN;
#ifndef DEMO_2D
    if (!researchStep->experiment->researchExtensions) lengthScale = .0005f;
#endif
    float stiffness = fabsf(force) / lengthScale;
    penalty = std::max(penalty, clamp(stiffness, PENALTY_MIN, PENALTY_MAX));
#ifdef DEMO_2D
    if (!researchStep->experiment->researchExtensions) manifold->penalty[point * 2 + 1] = penalty;
#else
    if (researchNewtonContact(manifold)) manifold->contacts[point].penalty.y = manifold->contacts[point].penalty.z = penalty;
#endif
}

void beginLongRangeStep(Experiment* experiment)
{
    auto& data = experiment->longRange;
    // Project LRC retains the author's hard auxiliary row policy independently
    // of Newton's compliant-row alpha/gamma configuration.
#ifdef DEMO_2D
    float decay = !experiment->newton.compliant ? experiment->solver.gamma * experiment->solver.alpha : .99f * .99f;
#else
    float decay = !experiment->newton.compliant ? experiment->solver.gamma * experiment->solver.alpha : .999f * .99f;
#endif
#ifdef DEMO_2D
    if (experiment->solver.postStabilize) decay = 1;
#endif
    for (int i = 0; i < data.jointRangeCount; ++i) data.rangeForces[i] = data.rangeForces[i] * decay;
    if (experiment->newton.compliant)
    {
        // Newton's midpoint matcher transports the LRC-owned contribution
        // together with fresh geometry in initializeNewtonContact.
        for (auto* manifold : experiment->reactionContacts)
            for (int point = 0; point < manifold->numContacts; ++point) manifold->reactions[point] = manifold->reactions[point] * decay;
        return;
    }
    for (auto& [manifold, forces] : experiment->contactForces) forces.count = 0;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        auto* changes = findContactReactions(manifold);
        if (changes == nullptr) continue;
        if (!experiment->researchExtensions)
        {
#ifdef DEMO_2D
            float retention = experiment->solver.gamma * rigidContactAlpha;
#else
            float retention = experiment->solver.gamma * (researchNewtonContact(manifold) ? rigidContactAlpha : experiment->solver.alpha);
#endif
            for (int point = 0; point < manifold->numContacts; ++point) (*changes)[point] = (*changes)[point] * retention;
            continue;
        }
        ContactForces* stored = nullptr;
        for (int i = 0; i < manifold->numContacts; ++i)
        {
#ifdef DEMO_2D
            auto& contact = manifold->contacts[i];
            float3 normal{contact.normal.x, contact.normal.y, 0}, tangent{normal.y, -normal.x, 0};
            float3 world = normal * (*changes)[i].x + tangent * (*changes)[i].y;
            int feature = contact.feature.value;
#else
            float3 world = transpose(manifold->basis) * (*changes)[i];
            int feature = manifold->contacts[i].feature.key;
#endif
            world = world * decay;
            if (world.x == 0 && world.y == 0 && world.z == 0) continue;
            if (!stored) stored = &experiment->contactForces[manifold];
            stored->features[stored->count] = feature;
            stored->forces[stored->count++] = world;
        }
#ifndef DEMO_2D
        clearContactReactions(experiment, manifold);
#endif
    }
#ifdef DEMO_2D
    if (experiment->researchExtensions) clearContactReactions(experiment);
#endif
}

void researchRestoreContactForces(Experiment* experiment)
{
    if (experiment->newton.compliant) return;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
#ifdef DEMO_2D
        if (!experiment->researchExtensions) continue;
#else
        if (researchNewtonContact(manifold)) continue;
#endif
        auto found = experiment->contactForces.find(manifold);
        if (found == experiment->contactForces.end()) continue;
        const auto& stored = found->second;
        if (stored.count == 0) continue;
        for (int i = 0; i < manifold->numContacts; ++i)
        {
#ifdef DEMO_2D
            int feature = manifold->contacts[i].feature.value;
#else
            int feature = manifold->contacts[i].feature.key;
#endif
            int previous = 0;
            while (previous < stored.count && stored.features[previous] != feature) ++previous;
            if (previous == stored.count) continue;
#ifdef DEMO_2D
            auto normal = manifold->contacts[i].normal;
            float3 n{normal.x, normal.y, 0}, t{normal.y, -normal.x, 0};
            editContactReactions(experiment, manifold)[i] = {dot(n, stored.forces[previous]), dot(t, stored.forces[previous]), 0};
#else
            editContactReactions(experiment, manifold)[i] = manifold->basis * stored.forces[previous];
#endif
        }
    }
}


#ifndef DEMO_2D
void researchRefreshContacts()
{
    Experiment* experiment = researchStep->experiment;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceAwake(force)) continue;
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        if (!experiment->newton.compliant && !experiment->researchExtensions)
        {
            if (researchNewtonContact(manifold) || (researchStep->currentIteration > 0
                && researchStep->currentIteration < experiment->solver.iterations))
            {
                if (researchStep->currentIteration >= experiment->solver.iterations) continue;
                const auto& geometry = *static_cast<ContactManifold*>(manifold);
                Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
                float motion = length((a->positionLin - b->positionLin) - (geometry.geometryCenterA - geometry.geometryCenterB))
                    + length(a->positionAng - geometry.geometryRotationA) * a->radius
                    + length(b->positionAng - geometry.geometryRotationB) * b->radius;
                if (manifold->numContacts > 0 && motion <= constraintAccuracy) continue;
                float3 normal;
                float gap;
                if (!researchContactPenetrates(manifold, constraintAccuracy, normal, gap)) continue;
                float witnessGap = INFINITY;
                const auto& offsets = readContactOffsets(manifold);
                for (int point = 0; point < manifold->numContacts; ++point)
                    witnessGap = min(witnessGap, dot(manifold->basis[0], a->positionLin - b->positionLin
                        + offsets.offsetsA[point] - offsets.offsetsB[point]));
                if (dot(normal, manifold->basis[0]) >= .995f && witnessGap <= gap + constraintAccuracy) continue;
                initializeNewtonContact(manifold, true);
                experiment->supportGraph.manifoldCount = -1;
                continue;
            }
            if (researchStep->currentIteration != 0 && researchStep->currentIteration != experiment->solver.iterations) continue;
            float refreshDistance = (float)contactGeometryAccuracy;
            bool moved = false;
            float3 relative = (manifold->bodyA->positionLin - manifold->bodyA->initialLin)
                - (manifold->bodyB->positionLin - manifold->bodyB->initialLin);
            float3 normal = manifold->basis[0];
            moved = length(relative - normal * dot(relative, normal)) > refreshDistance;
            for (Rigid* body : {manifold->bodyA, manifold->bodyB})
                moved |= length(body->positionAng - body->initialAng) * body->radius > refreshDistance;
            if (!moved) continue;
            initializeNewtonContact(manifold, true);
            experiment->supportGraph.manifoldCount = -1;
            continue;
        }
        if (researchSupportedContact(manifold)) continue;
        if (researchNewtonContact(manifold))
        {
            if (researchStep->currentIteration > 0) initializeNewtonContact(manifold, true);
            continue;
        }
        Rigid* a = manifold->bodyA;
        Rigid* b = manifold->bodyB;
        float rotationA = length(a->positionAng - a->initialAng) * a->radius;
        float rotationB = length(b->positionAng - b->initialAng) * b->radius;
        if (manifold->numContacts > 0 && rotationA <= constraintAccuracy && rotationB <= constraintAccuracy)
        {
            const auto& contact = manifold->contacts[0];
            float3 gap = transform(a->positionLin, a->positionAng, contact.rA)
                - transform(b->positionLin, b->positionAng, contact.rB);
            float2 travel{dot(manifold->basis[1], gap) - contact.C0.y,
                dot(manifold->basis[2], gap) - contact.C0.z};
            if (length(travel) <= constraintAccuracy) continue;
        }
        Manifold::Contact contacts[8]{};
        float3x3 basis;
        int count = Manifold::collide(a, b, contacts, basis);
        if (!experiment->researchExtensions) count = researchContactPatch(manifold, contacts, count);
        float initialSeparation = experiment->researchExtensions ? -INFINITY
            : readBoxSeparation(a, b, a->initialLin, a->initialAng, b->initialLin, b->initialAng);
        bool normalChanged = count > 0 && (basis[0].x != manifold->basis[0].x
            || basis[0].y != manifold->basis[0].y || basis[0].z != manifold->basis[0].z);
        bool stabilization = experiment->lastStabilizationIterations > 0;
        if (stabilization) researchSaveStabilizationForces(experiment);
        auto* changes = findContactReactions(manifold);
        float3x3 previousToWorld = transpose(manifold->basis);
        bool pressureMoved[8]{};
        for (int i = 0; i < count; ++i)
        {
            for (int j = 0; j < manifold->numContacts; ++j)
            {
                const auto& previous = manifold->contacts[j];
                if (contacts[i].feature.key != previous.feature.key) continue;
                float3 extra = changes == nullptr ? float3{0, 0, 0} : (*changes)[j];
                float3 total = previous.lambda + extra;
                contacts[i].lambda = normalChanged ? basis * (previousToWorld * total) : total;
                contacts[i].penalty = previous.penalty;
                contacts[i].stick = previous.stick;
                pressureMoved[i] = extra.x != 0 || extra.y != 0 || extra.z != 0;
                break;
            }
            float3 initialA = transform(a->initialLin, a->initialAng, contacts[i].rA);
            float3 initialB = transform(b->initialLin, b->initialAng, contacts[i].rB);
            contacts[i].C0 = basis * (initialA - initialB);
            if (!experiment->researchExtensions) contacts[i].C0.x = min(0.0f, max(contacts[i].C0.x, initialSeparation));
        }
        manifold->numContacts = count;
        if (count) manifold->basis = basis;
        std::copy_n(contacts, count, manifold->contacts);
        if (changes != nullptr) (*changes).fill(float3{0, 0, 0});
        for (int i = 0; i < count; ++i)
        {
            researchInitializeContact(manifold, i);
            if (pressureMoved[i]) researchTransferContactPressure(manifold, i);
        }
        researchSaveNewStabilizationContacts(manifold);
    }
}

#endif

static float readLrcAlpha(Experiment* experiment)
{
    if (researchStep->currentIteration >= experiment->solver.iterations) return 0;
    if (researchPostStabilize()) return 1;
    return !experiment->newton.compliant ? experiment->solver.alpha : .99f;
}

static float readPointResponse(Rigid* body, float3 offset, float3 direction)
{
    if (!body || body->mass <= 0) return 0;
#ifdef DEMO_2D
    float torque = offset.x * direction.y - offset.y * direction.x;
    return 1 / body->mass + torque * torque / body->moment;
#else
    float3 torque = rotate(conjugate(body->positionAng), cross(offset, direction));
    return 1 / body->mass + torque.x * torque.x / body->moment.x
        + torque.y * torque.y / body->moment.y + torque.z * torque.z / body->moment.z;
#endif
}

void researchInitializeJointBounds(Experiment* experiment)
{
    RESEARCH_TIME(phaseTime, JointBounds);
    auto& data = experiment->longRange;
    if (data.jointBounds.empty()) return;
    // Project LRC joint bounds remain part of the same block/dual solve when
    // the island reaches a contact, as in the pre-Newton integration.
    for (auto& anchor : data.jointAnchors) updateJointAnchor(experiment, anchor, true);
    readStartErrors(experiment, 0);
    float timeSquared = experiment->solver.dt * experiment->solver.dt;
    for (auto& bound : data.jointBounds)
    {
        const auto& a = data.jointAnchors[bound.anchorA];
        const auto& b = data.jointAnchors[bound.anchorB];
        float3 delta = b.point - a.point;
        float distance = length(delta);
        float3 direction = distance > 0 ? delta / distance : float3{0, 0, 0};
        Rigid* bodyA = a.body < 0 ? nullptr : experiment->bodies[a.body];
        Rigid* bodyB = b.body < 0 ? nullptr : experiment->bodies[b.body];
        float response = readPointResponse(bodyA, a.offset, direction) + readPointResponse(bodyB, b.offset, direction);
        bound.penalty = 1 / (timeSquared * response);
    }
}

struct JointBoundGeometry
{
    float3 delta;
    float squared, minimum, maximum;
};

static inline bool activeJointBound(const LongRangeData& data, int index, float alpha, JointBoundGeometry& geometry)
{
    const auto& bound = data.jointBounds[index];
    float2 previous = data.rangeForces[index];
    geometry.delta = data.jointAnchors[bound.anchorB].point - data.jointAnchors[bound.anchorA].point;
    geometry.squared = lengthSq(geometry.delta);
    geometry.maximum = bound.maximum + alpha * data.startErrors[index].x;
    geometry.minimum = bound.minimum - alpha * data.startErrors[index].y;
    return !(bound.minimum != bound.maximum && previous.x == 0 && previous.y == 0
        && geometry.squared <= geometry.maximum * geometry.maximum
        && (geometry.minimum <= 0 || geometry.squared >= geometry.minimum * geometry.minimum));
}

// Positive error violates a bound. Each auxiliary multiplier has one holder.
static float evaluateJointBound(const LongRangeData& data, int index, float alpha, const JointBoundGeometry& geometry,
    float3& direction, float& distance, float& penalty, float2* updated = nullptr)
{
    const auto& bound = data.jointBounds[index];
    float2 previous = data.rangeForces[index];
    distance = sqrtf(geometry.squared);
    direction = distance > 0 ? geometry.delta / distance : float3{0, 0, 0};
    if (bound.minimum == bound.maximum)
    {
        float force = previous.x + bound.penalty * (distance - bound.maximum
            - alpha * (data.startErrors[index].x - data.startErrors[index].y));
        penalty = bound.penalty;
        if (updated) *updated = {force, 0};
        return force;
    }
    float upper = std::max(0.0f, previous.x + bound.penalty * (distance - geometry.maximum));
    float lower = std::max(0.0f, previous.y + bound.penalty * (geometry.minimum - distance));
    penalty = bound.penalty * ((upper > 0 ? 1 : 0) + (lower > 0 ? 1 : 0));
    if (updated) *updated = {upper, lower};
    return upper - lower;
}

#ifdef DEMO_2D
void researchPrimal(Rigid* body, float3x3& lhs, float3& rhs)
#else
void researchPrimal(int bodyIndex, float3x3& lhsLin, float3x3& lhsAng,
    float3x3& lhsCross, float3& rhsLin, float3& rhsAng)
#endif
{
    Experiment* experiment = researchStep->experiment;
    auto& data = experiment->longRange;
    if (data.enabled && experiment->lastStabilizationIterations > 0) return;
    if (data.jointBounds.empty()) return;
#ifdef DEMO_2D
    int bodyIndex = experiment->index(body);
#endif
    float alpha = readLrcAlpha(experiment);
    for (int index : data.bodyJointRanges[bodyIndex])
    {
        const auto& bound = data.jointBounds[index];
        JointBoundGeometry geometry;
        if (!activeJointBound(data, index, alpha, geometry)) continue;
        float3 direction;
        float distance, penalty;
        float force = evaluateJointBound(data, index, alpha, geometry, direction, distance, penalty);
        if (penalty == 0) continue;
        const auto& a = data.jointAnchors[bound.anchorA];
        const auto& b = data.jointAnchors[bound.anchorB];
        float side = bodyIndex == b.body ? 1.0f : -1.0f;
        float3 linear = direction * side;
        float3 offset = bodyIndex == b.body ? b.offset : a.offset;
#ifdef DEMO_2D
        float angular = offset.x * linear.y - offset.y * linear.x;
        float3 jacobian{linear.x, linear.y, angular};
        lhs += outer(jacobian, jacobian) * penalty;
        rhs += jacobian * force;
        if (force != 0 && distance > 0)
        {
            float curvature = fabsf(force) / distance;
            lhs[0][0] += curvature * std::max(0.0f, 1 - direction.x * direction.x);
            lhs[1][1] += curvature * std::max(0.0f, 1 - direction.y * direction.y);
            float angularCurvature = (lengthSq(offset) - angular * angular) / distance - side * dot(offset, direction);
            lhs[2][2] += fabsf(force * angularCurvature);
        }
#else
        float3 angular = cross(offset, linear);
        researchAddBodyMetric(lhsLin, linear, penalty);
        researchAddBodyMetric(lhsAng, angular, penalty);
        lhsCross += outer(angular, linear) * penalty;
        rhsLin += linear * force; rhsAng += angular * force;
        if (force != 0 && distance > 0)
        {
            float magnitude = fabsf(force);
            float radiusSquared = lengthSq(offset);
            float radial = dot(offset, direction);
            for (int axis = 0; axis < 3; ++axis)
            {
                lhsLin[axis][axis] += magnitude / distance * std::max(0.0f, 1 - direction[axis] * direction[axis]);
                float3 column = offset * direction[axis];
                column[axis] -= radial;
                float angularCurvature = std::max(0.0f,
                    (radiusSquared - offset[axis] * offset[axis] - angular[axis] * angular[axis]) / distance);
                lhsAng[axis][axis] += magnitude * (angularCurvature + length(column));
            }
        }
#endif
    }
}
static double readSupportResponse(const SupportEquation& height, const SupportEquation& force)
{
#ifdef DEMO_2D
    return dot(height.gradient, force.response);
#else
    return force.translationResponse.z + dot(height.angularGradient, force.rotationResponse);
#endif
}

static bool solveSupportForces(std::vector<SupportEquation>& equations, const int* active, int count)
{
    double coefficients[3][4]{};
    double scale = 0;
    for (int row = 0; row < count; ++row)
    {
        for (int column = 0; column < count; ++column)
            coefficients[row][column] = readSupportResponse(equations[active[row]], equations[active[column]]);
        coefficients[row][count] = equations[active[row]].heightError;
        scale = std::max(scale, coefficients[row][row]);
    }
    // Coincident points and collinear triples describe dependent height bounds.
    for (int column = 0; column < count; ++column)
    {
        double diagonal = coefficients[column][column];
        if (diagonal <= scale * 0.000001) return false;
        for (int row = column + 1; row < count; ++row)
        {
            double ratio = coefficients[row][column] / diagonal;
            for (int component = column + 1; component <= count; ++component)
                coefficients[row][component] -= ratio * coefficients[column][component];
        }
    }
    double forces[3]{};
    for (int row = count - 1; row >= 0; --row)
    {
        double value = coefficients[row][count];
        for (int column = row + 1; column < count; ++column) value -= coefficients[row][column] * forces[column];
        forces[row] = value / coefficients[row][row];
        if (forces[row] < 0) return false;
    }
    for (const auto& equation : equations)
    {
        double height = 0;
        for (int i = 0; i < count; ++i) height += readSupportResponse(equation, equations[active[i]]) * forces[i];
        if (equation.heightError > height + 0.000001) return false;
    }
    for (int i = 0; i < count; ++i) equations[active[i]].reaction = (float)forces[i];
    return true;
}

static void findSupportForces(std::vector<SupportEquation>& equations)
{
    // Height and tilt span two coordinates in 2D and three in 3D.
    // A nonnegative reaction on at most that many points gives the constrained minimum.
    for (int first = 0; first < (int)equations.size(); ++first)
    {
        int active[3] = {first, 0, 0};
        if (solveSupportForces(equations, active, 1)) return;
    }
    for (int first = 0; first < (int)equations.size(); ++first)
        for (int second = first + 1; second < (int)equations.size(); ++second)
        {
            int active[3] = {first, second, 0};
            if (solveSupportForces(equations, active, 2)) return;
        }
#ifndef DEMO_2D
    for (int first = 0; first < (int)equations.size(); ++first)
        for (int second = first + 1; second < (int)equations.size(); ++second)
            for (int third = second + 1; third < (int)equations.size(); ++third)
            {
                int active[3] = {first, second, third};
                if (solveSupportForces(equations, active, 3)) return;
            }
#endif
}

#ifdef DEMO_2D
void researchSupport(Rigid* body, const BodySolve& factor, float3& change)
#else
void researchSupport(int bodyIndex, const BodySolve& factor, float3& changeLin, float3& changeAng)
#endif
{
    Experiment* experiment = researchStep->experiment;
    auto& data = experiment->longRange;
    if (data.ranges.size() == data.jointRangeCount) return;
    float alpha = readLrcAlpha(experiment);
#ifdef DEMO_2D
    int bodyIndex = experiment->index(body);
    float3 center{body->position.x, body->position.y, 0};
#else
    Rigid* body = experiment->bodies[bodyIndex];
    float3 center = body->positionLin;
#endif
    auto& equations = experiment->islands.supportEquations[experiment->islands.roots[bodyIndex]];
    equations.clear();
    bool hasError = false;
    for (int index : data.bodySupports[bodyIndex])
    {
        const auto& range = data.ranges[index];
        float minimum = (float)range.minimum - alpha * data.startErrors[index].x;
        float3 r = rotateOffset(body, readVector(range.pointB));
        float3 point = center + r;
        SupportEquation equation{};
        equation.range = index;
#ifdef DEMO_2D
        equation.gradient = {0, 1, r.x};
        equation.heightError = minimum - point.y - dot(equation.gradient, change);
#else
        equation.angularGradient = {r.y, -r.x, 0};
        equation.heightError = minimum - point.z - changeLin.z - dot(equation.angularGradient, changeAng);
#endif
        hasError |= equation.heightError > 0;
        equations.push_back(equation);
        if (researchStep->currentIteration < experiment->solver.iterations)
            data.rangeForces[index] = {0, 0};
    }
    if (!hasError) return;
#ifdef DEMO_2D
    float3 heightResponse = factor.solve(float3{0, 1, 0});
    equations[0].response = heightResponse;
#else
    float3 heightLin, heightAng;
    factor.solve(float3{0, 0, 1}, float3{0, 0, 0}, heightLin, heightAng);
    equations[0].translationResponse = heightLin;
    equations[0].rotationResponse = heightAng;
#endif
    // The COM row is first; its single reaction can satisfy every height bound.
    int centerActive[3] = {0, 0, 0};
    bool centerOnly = experiment->supportGraph.bodySupported[bodyIndex]
        && solveSupportForces(equations, centerActive, 1);
    if (!centerOnly)
    {
#ifdef DEMO_2D
        float3 angleResponse = factor.solve(float3{0, 0, 1});
        for (auto& equation : equations)
            equation.response = heightResponse + angleResponse * equation.gradient.z;
#else
        float3 tiltXLin, tiltXAng, tiltYLin, tiltYAng;
        factor.solve(float3{0, 0, 0}, float3{1, 0, 0}, tiltXLin, tiltXAng);
        factor.solve(float3{0, 0, 0}, float3{0, 1, 0}, tiltYLin, tiltYAng);
        for (auto& equation : equations)
        {
            equation.translationResponse = heightLin + tiltXLin * equation.angularGradient.x + tiltYLin * equation.angularGradient.y;
            equation.rotationResponse = heightAng + tiltXAng * equation.angularGradient.x + tiltYAng * equation.angularGradient.y;
        }
#endif
        findSupportForces(equations);
    }
    for (const auto& equation : equations)
    {
#ifdef DEMO_2D
        change += equation.response * equation.reaction;
#else
        changeLin += equation.translationResponse * equation.reaction;
        changeAng += equation.rotationResponse * equation.reaction;
#endif
    }
    if (researchStep->currentIteration < experiment->solver.iterations)
        for (const auto& equation : equations) data.rangeForces[equation.range] = {equation.reaction, 0};
}

static void addContactLoad(Experiment* experiment, int source, float3 force)
{
    auto& reference = experiment->contactReferences[source];
    Manifold* manifold = reference.manifold;
    float3 change;
#ifdef DEMO_2D
    auto& contact = manifold->contacts[reference.point];
    float3 normal{contact.normal.x, contact.normal.y, 0}, tangent{normal.y, -normal.x, 0};
    float* base = manifold->lambda + reference.point * 2;
    float3 total{base[0] + dot(normal, force), base[1] + dot(tangent, force), 0};
    total.x = std::min(0.0f, total.x);
    float bound = -total.x * manifold->friction;
    total.y = clamp(total.y, -bound, bound);
    change = total - float3{base[0], base[1], 0};
#else
    float3 base = manifold->contacts[reference.point].lambda;
    float3 total = base + manifold->basis * force;
    total[0] = std::min(0.0f, total[0]);
    float bound = -total[0] * manifold->friction;
    float tangent = length(float2{total[1], total[2]});
    if (tangent > bound && tangent > 0) { total[1] *= bound / tangent; total[2] *= bound / tangent; }
    change = total - base;
#endif
    editContactReactions(experiment, manifold)[reference.point] = change;
}

void transferLongRangeForces(Experiment* experiment)
{
    RESEARCH_TIME(phaseTime, ForceTransfer);
    auto& data = experiment->longRange;
    std::fill(data.supportLoads.begin(), data.supportLoads.end(), 0.0);
    if (researchStep->currentIteration >= researchStep->iteration)
        for (auto* contact : experiment->reactionContacts) contact->reactions.fill(float3{0, 0, 0});
    for (int index = data.jointRangeCount; index < (int)data.ranges.size(); ++index)
    {
        const auto& range = data.ranges[index];
        for (int i = range.loadStart; i < range.loadStart + range.loadCount; ++i)
            data.supportLoads[data.loads[i].contact] += data.rangeForces[index].x * data.loads[i].weight;
    }
    for (int i = (int)data.supports.size() - 1; i >= 0; --i)
    {
        const auto& node = data.supports[i];
        double force = data.supportLoads[i];
        addContactLoad(experiment, node.contact, readUpDirection() * (float)(force * node.direction));
        for (int j = node.belowStart; j < node.belowStart + node.belowCount; ++j)
            data.supportLoads[data.loads[j].contact] += force * data.loads[j].weight;
    }
}

void researchUpdateLongRangeForces()
{
    Experiment* experiment = researchStep->experiment;
    auto& data = experiment->longRange;
    float alpha = readLrcAlpha(experiment);
    for (int index = 0; index < data.jointRangeCount; ++index)
    {
        JointBoundGeometry geometry;
        if (!activeJointBound(data, index, alpha, geometry))
        {
            data.rangeForces[index] = {0, 0};
            continue;
        }
        float3 direction;
        float distance, penalty;
        evaluateJointBound(data, index, alpha, geometry, direction, distance, penalty, &data.rangeForces[index]);
    }
    if (researchStep->currentIteration == experiment->solver.iterations - 1) researchUpdateSupports();
    transferLongRangeForces(experiment);
}

// A stationary island whose active COM bounds have complete load paths has
// a feasible zero-motion KKT solution. Recover its gravity reactions directly
// through the LRC graph instead of iterating round-off into the velocities.
void researchSolveRestingSupports()
{
    Experiment* experiment = researchStep->experiment;
    auto& data = experiment->longRange;
    if (!data.enabled || data.supports.empty() || experiment->solver.gravity >= 0) return;
    auto& islands = experiment->islands;
    std::fill(islands.ready.begin(), islands.ready.end(), 1);
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (body->mass <= 0) continue;
        bool eligible = researchBodySolves(body) && experiment->supportGraph.bodySupported[index];
#ifdef DEMO_2D
        eligible &= lengthSq(body->velocity) == 0;
        float height = body->initial.y;
#else
        eligible &= lengthSq(body->velocityLin) == 0 && lengthSq(body->velocityAng) == 0;
        float height = body->initialLin.z;
#endif
        if (eligible)
        {
            const auto& range = data.ranges[data.bodySupports[index][0]];
            eligible = range.pointB[0] == 0 && range.pointB[1] == 0 && range.pointB[2] == 0 && range.minimum == height;
        }
        if (!eligible) islands.ready[islands.roots[index]] = 0;
    }
    for (Joint* joint : experiment->joints)
        for (Rigid* body : {joint->bodyA, joint->bodyB})
            if (body && body->mass > 0) islands.ready[islands.roots[experiment->index(body)]] = 0;
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<Manifold*>(force))
            for (int point = 0; point < manifold->numContacts; ++point)
            {
                const auto& contact = manifold->contacts[point];
#ifdef DEMO_2D
                float2 a = manifold->bodyA->initial.xy() + rotate(manifold->bodyA->initial.z, contact.rA);
                float2 b = manifold->bodyB->initial.xy() + rotate(manifold->bodyB->initial.z, contact.rB);
                float gap = dot(contact.normal, a - b);
#else
                float3 a = transform(manifold->bodyA->initialLin, manifold->bodyA->initialAng, contact.rA);
                float3 b = transform(manifold->bodyB->initialLin, manifold->bodyB->initialAng, contact.rB);
                float gap = dot(manifold->basis[0], a - b);
#endif
                if (gap < 0)
                    for (Rigid* body : {manifold->bodyA, manifold->bodyB})
                        if (body->mass > 0) islands.ready[islands.roots[experiment->index(body)]] = 0;
            }
    bool any = false;
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (experiment->bodies[body]->mass > 0 && islands.ready[islands.roots[body]])
        {
            islands.directContacts[body] = 2;
            islands.contactVelocities[body] = {0, 0, 0};
            ++islands.contactSolves;
            any = true;
#ifdef DEMO_2D
            experiment->bodies[body]->position = experiment->bodies[body]->initial;
#else
            experiment->bodies[body]->positionLin = experiment->bodies[body]->initialLin;
            experiment->bodies[body]->positionAng = experiment->bodies[body]->initialAng;
#endif
        }
    if (!any) return;
    auto selected = [&](Manifold* contact)
    {
        int a = experiment->index(contact->bodyA), b = experiment->index(contact->bodyB);
        return islands.directContacts[a] == 2 || islands.directContacts[b] == 2;
    };
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<Manifold*>(force))
            if (selected(manifold))
            {
                for (int point = 0; point < manifold->numContacts; ++point)
                {
#ifdef DEMO_2D
                    manifold->lambda[point * 2] = manifold->lambda[point * 2 + 1] = 0;
#else
                    manifold->contacts[point].lambda = {0, 0, 0};
#endif
                }
                clearContactReactions(experiment, manifold);
                experiment->contactForces.erase(manifold);
            }
    std::fill(data.supportLoads.begin(), data.supportLoads.end(), 0.0);
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (islands.directContacts[body] == 2)
        {
            const auto& range = data.ranges[data.bodySupports[body][0]];
            double weight = -experiment->bodies[body]->mass * experiment->solver.gravity;
            for (int i = range.loadStart; i < range.loadStart + range.loadCount; ++i)
                data.supportLoads[data.loads[i].contact] += weight * data.loads[i].weight;
        }
    for (int i = (int)data.supports.size() - 1; i >= 0; --i)
    {
        double force = data.supportLoads[i];
        if (force == 0) continue;
        const auto& node = data.supports[i];
        addContactLoad(experiment, node.contact, readUpDirection() * (float)(force * node.direction));
        for (int j = node.belowStart; j < node.belowStart + node.belowCount; ++j)
            data.supportLoads[data.loads[j].contact] += force * data.loads[j].weight;
    }
    for (int reaction = 0; reaction < (int)experiment->reactionContacts.size();)
    {
        auto* manifold = experiment->reactionContacts[reaction];
        if (!selected(manifold)) { ++reaction; continue; }
        for (int point = 0; point < manifold->numContacts; ++point)
        {
#ifdef DEMO_2D
            manifold->lambda[point * 2] = manifold->reactions[point].x;
            manifold->lambda[point * 2 + 1] = manifold->reactions[point].y;
#else
            manifold->contacts[point].lambda = manifold->reactions[point];
#endif
        }
        clearContactReactions(experiment, manifold);
    }
}

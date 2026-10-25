#include "BridgeState.h"
#include "ResearchStep.h"

#define EXPORT extern "C" __declspec(dllexport)

extern "C" int demo_support_contacts(Experiment* experiment, SupportContact** output);
extern "C" void demo_lrc_supports(Experiment* experiment, const DistanceRange* ranges, int count,
    const SupportNode* supports, int supportCount, const ContactLoad* loads, int loadCount);

static constexpr double distanceTolerance = contactGeometryAccuracy;

static float3 readPoint(const double* value)
{
    return {(float)value[0], (float)value[1], (float)value[2]};
}

#ifdef DEMO_2D
float readBoxSeparation(Rigid* first, Rigid* second, float3 firstPose, float3 secondPose)
{
    float2 firstHalfSize = first->size * 0.5f, secondHalfSize = second->size * 0.5f;
    float2x2 firstRotation = rotation(firstPose.z), secondRotation = rotation(secondPose.z);
    float2 centerDifference = secondPose.xy() - firstPose.xy();
    float2x2 axisProjections = abs(transpose(firstRotation) * secondRotation);
    float2 firstAxisGaps = abs(transpose(firstRotation) * centerDifference) - firstHalfSize - axisProjections * secondHalfSize;
    float2 secondAxisGaps = abs(transpose(secondRotation) * centerDifference) - secondHalfSize - transpose(axisProjections) * firstHalfSize;
    return std::max(std::max(firstAxisGaps.x, firstAxisGaps.y), std::max(secondAxisGaps.x, secondAxisGaps.y));
}
#endif

static float readHeight(float3 point)
{
#ifdef DEMO_2D
    return point.y;
#else
    return point.z;
#endif
}

static float2 project(float3 point)
{
#ifdef DEMO_2D
    return {point.x, 0};
#else
    return {point.x, point.y};
#endif
}

static float3 readCenter(Rigid* body)
{
#ifdef DEMO_2D
    return {body->position.x, body->position.y, 0};
#else
    return body->positionLin;
#endif
}

static void readSupportPoses(Experiment* experiment)
{
    auto& poses = experiment->supportGraph.poses;
    poses.resize(experiment->bodies.size());
    for (int index = 0; index < (int)poses.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        poses[index].center = readCenter(body);
#ifdef DEMO_2D
        poses[index].angle = body->position.z;
#else
        poses[index].rotation = body->positionAng;
#endif
    }
}

static void addContactMultiplier(Manifold* manifold, int point, float3 extra)
{
#ifdef DEMO_2D
    manifold->lambda[point * 2] += extra.x;
    manifold->lambda[point * 2 + 1] += extra.y;
#else
    manifold->contacts[point].lambda += extra;
#endif
    if (extra.x != 0 || extra.y != 0 || extra.z != 0) researchTransferContactPressure(manifold, point);
}

static void transferContactMultipliers(Experiment* experiment, Manifold* manifold)
{
    auto* changes = findContactReactions(manifold);
    if (changes == nullptr) return;
    for (int point = 0; point < manifold->numContacts; ++point)
        addContactMultiplier(manifold, point, (*changes)[point]);
    clearContactReactions(experiment, manifold);
}

static void transferUnsupportedMultipliers(Experiment* experiment)
{
    const auto& retained = experiment->supportGraph.pointSupported;
    for (int index = 0; index < (int)experiment->contactReferences.size(); ++index)
    {
        if (retained[index]) continue;
        const auto& reference = experiment->contactReferences[index];
        auto* changes = findContactReactions(reference.manifold);
        if (changes == nullptr) continue;
        auto& extra = (*changes)[reference.point];
        addContactMultiplier(reference.manifold, reference.point, extra);
        extra = {0, 0, 0};
    }
}

#ifdef DEMO_2D
// Rotation and sliding change the clipped contact vertices within a step.
// Reuse the manifold's feature matching and the original initial-pose errors.
void researchRefreshContacts()
{
    Experiment* experiment = researchStep->experiment;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceAwake(force)) continue;
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) continue;
        Rigid* a = manifold->bodyA;
        Rigid* b = manifold->bodyB;
        if (manifold->numContacts > 0 && fabsf(a->position.z - a->initial.z) * a->radius <= distanceTolerance &&
            fabsf(b->position.z - b->initial.z) * b->radius <= distanceTolerance)
        {
            const auto& contact = manifold->contacts[0];
            float2 tangent{contact.normal.y, -contact.normal.x};
            float2 gap = a->position.xy() + rotate(a->position.z, contact.rA)
                - b->position.xy() - rotate(b->position.z, contact.rB);
            if (fabsf(dot(tangent, gap) - contact.C0.y) <= distanceTolerance) continue;
        }
        if (!experiment->researchExtensions)
        {
            initializeNewtonContact(manifold, true);
            experiment->supportGraph.manifoldCount = -1;
            continue;
        }
        if (experiment->lastStabilizationIterations > 0) researchSaveStabilizationForces(experiment);
        transferContactMultipliers(experiment, manifold);
        for (int point = 0; point < manifold->numContacts; ++point) manifold->contacts[point].stick = false;
        researchInitializeCurrentContacts(manifold);
        float initialSeparation = experiment->researchExtensions ? 0 : readBoxSeparation(a, b, a->initial, b->initial);
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            auto& contact = manifold->contacts[point];
            float2 normal = contact.normal, tangent{normal.y, -normal.x};
            float2 gap = a->initial.xy() - b->initial.xy()
                + researchRotateContactPoint(a->initial.z, contact.rA)
                - researchRotateContactPoint(b->initial.z, contact.rB);
            contact.C0 = {min(0.0f, max(dot(normal, gap), initialSeparation)), dot(tangent, gap)};
            for (int axis = 0; axis < 2; ++axis)
                manifold->penalty[point * 2 + axis] =
                    clamp(manifold->penalty[point * 2 + axis], PENALTY_MIN, PENALTY_MAX);
        }
        researchSaveNewStabilizationContacts(manifold);
    }
}
#endif

static float3 readInitialCenter(Rigid* body)
{
#ifdef DEMO_2D
    return {body->initial.x, body->initial.y, 0};
#else
    return body->initialLin;
#endif
}

static bool activeContact(const SupportContact& contact)
{
    float3 normal = readPoint(contact.normal);
    float gap = dot(normal, readPoint(contact.pointA) - readPoint(contact.pointB));
    return gap <= distanceTolerance || dot(normal, readPoint(contact.force)) < 0;
}

// A moving contact releases propagated height supports for its connected island.
// Direct horizontal world-plane bounds remain valid normal contact inequalities.
static bool updateMovingBodies(Experiment* experiment)
{
#ifndef DEMO_2D
    if (!experiment->newton.compliant && !experiment->researchExtensions) return false;
#endif
    if (experiment->supportContacts.empty()) return false;
    auto& graph = experiment->supportGraph;
    bool changed = false;
    graph.bodyMotion.resize(experiment->bodies.size());
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (body->mass <= 0 || graph.movingBodies[index]) continue;
        auto& motion = graph.bodyMotion[index];
        motion.travel = readCenter(body) - readInitialCenter(body);
#ifdef DEMO_2D
        float rotationDistance = fabsf(body->position.z - body->initial.z) * body->radius;
#else
        float rotationDistance = length(body->positionAng - body->initialAng) * body->radius;
#endif
        float2 horizontal = project(motion.travel);
        motion.moving = dot(horizontal, horizontal) > distanceTolerance * distanceTolerance || rotationDistance > distanceTolerance;
    }
    auto mark = [&](int index)
    {
        if (experiment->bodies[index]->mass > 0 && !graph.movingBodies[index])
        {
            graph.movingBodies[index] = 1;
            changed = true;
        }
    };
    for (const auto& contact : experiment->supportContacts)
    {
#ifndef DEMO_2D
        // SCA 2017 section 3.6 uses static relative contact and the support
        // polygon; absolute body travel does not classify a rigid contact.
        if (!experiment->newton.compliant && !experiment->researchExtensions) continue;
#endif
        int a = contact.bodyA, b = contact.bodyB;
        if (graph.movingBodies[a] && graph.movingBodies[b]) continue;
        if (!activeContact(contact)) continue;
        if (!graph.movingBodies[a] && graph.bodyMotion[a].moving) mark(a);
        if (!graph.movingBodies[b] && graph.bodyMotion[b].moving) mark(b);
        if (!contact.sticks)
        {
            float3 normal = readPoint(contact.normal);
            for (int index : {a, b})
            {
                if (graph.movingBodies[index]) continue;
                float3 travel = graph.bodyMotion[index].travel;
                float3 tangent = travel - normal * dot(normal, travel);
                if (dot(tangent, tangent) > distanceTolerance * distanceTolerance) mark(index);
            }
        }
    }
    if (changed)
    {
        if (!experiment->newton.compliant)
        {
            const auto& roots = experiment->islands.roots;
            for (int body = 0; body < (int)roots.size(); ++body)
                if (graph.movingBodies[body]) mark(roots[body]);
            for (int body = 0; body < (int)roots.size(); ++body)
                if (graph.movingBodies[roots[body]]) mark(body);
        }
        graph.dynamicContacts = true;
    }
    return changed;
}

static void updateDynamicContacts(Experiment* experiment)
{
    auto& graph = experiment->supportGraph;
    if (graph.dynamicContacts) return;
    for (const auto& contact : experiment->supportContacts)
    {
        if (!activeContact(contact)) continue;
        if ((experiment->bodies[contact.bodyA]->mass > 0 && !graph.bodySupported[contact.bodyA]) ||
            (experiment->bodies[contact.bodyB]->mass > 0 && !graph.bodySupported[contact.bodyB]))
        {
            graph.dynamicContacts = true;
            return;
        }
    }
}

static double orientation(float2 a, float2 b, float2 c)
{
    double bx = (double)b.x - a.x, by = (double)b.y - a.y;
    double cx = (double)c.x - a.x, cy = (double)c.y - a.y;
    return bx * cy - by * cx;
}

static bool pointLess(const SupportGraphPoint& a, const SupportGraphPoint& b)
{
    if (a.position.x != b.position.x) return a.position.x < b.position.x;
    if (a.position.y != b.position.y) return a.position.y < b.position.y;
    return a.node < b.node;
}

static void buildHull(SupportGraphPolygon& polygon)
{
    if (polygon.hullPointCount == (int)polygon.points.size()) return;
    polygon.hullPointCount = (int)polygon.points.size();
    auto& hull = polygon.hull;
    hull.clear();
#ifdef DEMO_2D
    SupportGraphPoint first = polygon.points.front(), last = first;
    for (const auto& point : polygon.points)
    {
        if (pointLess(point, first)) first = point;
        if (point.position.x > last.position.x ||
            (point.position.x == last.position.x && point.node < last.node)) last = point;
    }
    hull.push_back(first);
    if (first.position.x != last.position.x) hull.push_back(last);
#else
    auto& sorted = polygon.sorted;
    sorted.assign(polygon.points.begin(), polygon.points.end());
    std::sort(sorted.begin(), sorted.end(), pointLess);
    int unique = 0;
    for (int i = 0; i < (int)sorted.size(); ++i)
        if (unique == 0 || sorted[i].position.x != sorted[unique - 1].position.x ||
            sorted[i].position.y != sorted[unique - 1].position.y) sorted[unique++] = sorted[i];
    sorted.resize(unique);
    if (sorted.size() <= 2)
    {
        hull.assign(sorted.begin(), sorted.end());
        return;
    }
    for (const auto& point : sorted)
    {
        while (hull.size() >= 2 && orientation(hull[hull.size() - 2].position, hull.back().position,
            point.position) <= 0) hull.pop_back();
        hull.push_back(point);
    }
    int lowerCount = (int)hull.size();
    for (int i = (int)sorted.size() - 2; i >= 0; --i)
    {
        const auto& point = sorted[i];
        while ((int)hull.size() > lowerCount && orientation(hull[hull.size() - 2].position, hull.back().position,
            point.position) <= 0) hull.pop_back();
        hull.push_back(point);
    }
    hull.pop_back();
#endif
}

static bool lineWeights(SupportGraph& graph, const SupportGraphPoint& first,
    const SupportGraphPoint& last, float2 position)
{
    float2 line = last.position - first.position;
    double lengthSquared = dot(line, line);
    if (lengthSquared == 0)
    {
        float2 difference = first.position - position;
        if (dot(difference, difference) > distanceTolerance * distanceTolerance) return false;
        graph.weights[0] = {first.node, 1}; graph.weightCount = 1;
        return true;
    }
    double fraction = dot(position - first.position, line) / lengthSquared;
    if (fraction < -distanceTolerance || fraction > 1 + distanceTolerance) return false;
    fraction = std::clamp(fraction, 0.0, 1.0);
    float2 difference = first.position + line * (float)fraction - position;
    if (dot(difference, difference) > distanceTolerance * distanceTolerance) return false;
    graph.weights[0] = {first.node, 1 - fraction}; graph.weights[1] = {last.node, fraction};
    graph.weightCount = 2;
    return true;
}

static bool tryWeights(SupportGraph& graph, SupportGraphPolygon& polygon, float2 position)
{
    graph.weightCount = 0;
    if (polygon.points.empty()) return false;
    buildHull(polygon);
    const auto& hull = polygon.hull;
    if (hull.size() <= 2) return lineWeights(graph, hull.front(), hull.back(), position);
    for (int i = 1; i < (int)hull.size() - 1; ++i)
    {
        float2 a = hull[0].position, b = hull[i].position, c = hull[i + 1].position;
        double area = orientation(a, b, c);
        double wb = orientation(a, position, c) / area;
        double wc = orientation(a, b, position) / area;
        double wa = 1 - wb - wc;
        if (std::min(wa, std::min(wb, wc)) < -distanceTolerance) continue;
        wa = std::max(0.0, wa); wb = std::max(0.0, wb); wc = std::max(0.0, wc);
        double total = wa + wb + wc;
        graph.weights[0] = {hull[0].node, wa / total};
        graph.weights[1] = {hull[i].node, wb / total};
        graph.weights[2] = {hull[i + 1].node, wc / total};
        graph.weightCount = 3;
        return true;
    }
    return false;
}

static DistanceRange createRange(int body, float3 local, double minimum,
    int loadStart, int loadCount)
{
    DistanceRange range{};
    range.bodyA = -1; range.bodyB = body; range.kind = 1;
    range.pointB[0] = local.x; range.pointB[1] = local.y; range.pointB[2] = local.z;
    range.minimum = minimum; range.maximum = INFINITY;
    range.loadStart = loadStart; range.loadCount = loadCount;
    return range;
}

static float3 readUpperAnchor(Experiment* experiment, const SupportGraphCandidate& candidate)
{
    const auto& contact = experiment->supportContacts[candidate.contact];
    const auto& reference = experiment->contactReferences[contact.source];
    const auto& point = reference.manifold->contacts[reference.point];
#ifdef DEMO_2D
    float2 local = candidate.direction < 0 ? point.rA : point.rB;
    return {local.x, local.y, 0};
#else
    return candidate.direction < 0 ? point.rA : point.rB;
#endif
}

// SCA 2017 section 3.6: supporting contacts and centers of mass must project
// inside the support polygon below. Ranges and the load graph share this pass.
static void buildSupports(Experiment* experiment)
{
    RESEARCH_COUNT(GraphBuilds, 1);
    const auto* contacts = experiment->supportContacts.data();
    int contactCount = (int)experiment->supportContacts.size();
    auto& graph = experiment->supportGraph;
    auto& data = experiment->longRange;
    data.ranges.resize(data.jointRangeCount); data.supports.clear(); data.loads.clear();
    graph.shifts.clear(); graph.candidates.clear();
    graph.bodySupported.assign(experiment->bodies.size(), 0);
    graph.pointSupported.assign(contactCount, 0);
    graph.contactIslandBodies.assign(experiment->bodies.size(), 0);
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<ContactManifold*>(force))
        {
            manifold->supported = false;
            if (manifold->numContacts == 0) continue;
            graph.contactIslandBodies[experiment->islands.roots[manifold->indexA]] = 1;
            graph.contactIslandBodies[experiment->islands.roots[manifold->indexB]] = 1;
        }
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        graph.contactIslandBodies[body] = graph.contactIslandBodies[experiment->islands.roots[body]];
    if (contactCount == 0)
    {
        demo_lrc_supports(experiment, nullptr, 0, nullptr, 0, nullptr, 0);
        graph.manifoldCount = 0;
        return;
    }
    bool rooted = false;
    for (int index = 0; index < contactCount; ++index)
    {
        const auto& contact = contacts[index];
        double vertical = readHeight(readPoint(contact.normal));
        double normalSquared = vertical * vertical;
        if (vertical == 0 || 1 - normalSquared > contact.friction * contact.friction * normalSquared) continue;
        int lower = vertical > 0 ? contact.bodyB : contact.bodyA;
        int upper = vertical > 0 ? contact.bodyA : contact.bodyB;
        if (experiment->bodies[lower]->mass == 0 && experiment->bodies[upper]->mass > 0)
        { rooted = true; break; }
    }
    if (!rooted)
    {
        // SCA 2017 section 3.6 propagates only from fixed contact supports.
        // Fixed endpoints and normals stay constant until contact re-detection.
        transferUnsupportedMultipliers(experiment);
        demo_lrc_supports(experiment, nullptr, 0, nullptr, 0, nullptr, 0);
        updateDynamicContacts(experiment);
        graph.manifoldCount = 0;
        return;
    }
    readSupportPoses(experiment);
    graph.polygons.resize(experiment->bodies.size());
    for (auto& polygon : graph.polygons)
    {
        polygon.points.clear(); polygon.sorted.clear(); polygon.hull.clear(); polygon.hullPointCount = -1;
    }
    for (int index = 0; index < contactCount; ++index)
    {
        const auto& contact = contacts[index];
        double vertical = readHeight(readPoint(contact.normal));
        if (vertical == 0) continue;
        double normalSquared = vertical * vertical;
        if (1 - normalSquared > contact.friction * contact.friction * normalSquared) continue;
        bool upperIsA = vertical > 0;
        int lower = upperIsA ? contact.bodyB : contact.bodyA;
        int upper = upperIsA ? contact.bodyA : contact.bodyB;
        bool supporting = !graph.movingBodies[contact.bodyA] && !graph.movingBodies[contact.bodyB] && contact.sticks;
        bool groundPlane = false;
#ifdef DEMO_2D
        // SCA 2017 section 3.6 separates normal exclusion during sliding from
        // static-friction support propagation. This bound enters the body KKT.
        groundPlane = !experiment->newton.compliant && !experiment->researchExtensions
            && experiment->bodies[lower]->mass == 0 && contact.normal[0] == 0;
#else
        groundPlane = !experiment->newton.compliant && !experiment->researchExtensions
            && experiment->bodies[lower]->mass == 0 && contact.normal[0] == 0 && contact.normal[1] == 0;
#endif
        if (!supporting && !groundPlane) continue;
        if (experiment->bodies[upper]->mass == 0) continue;
        graph.candidates.push_back({index, lower, upper, upperIsA ? -1 : 1,
            readPoint(upperIsA ? contact.pointB : contact.pointA), readPoint(upperIsA ? contact.pointA : contact.pointB)});
    }
    if (graph.candidates.empty())
    {
        transferUnsupportedMultipliers(experiment);
        demo_lrc_supports(experiment, nullptr, 0, nullptr, 0, nullptr, 0);
        updateDynamicContacts(experiment);
        return;
    }
    std::sort(graph.candidates.begin(), graph.candidates.end(), [](const auto& a, const auto& b)
    {
        return readHeight(a.upperPoint) < readHeight(b.upperPoint);
    });
    for (const auto& candidate : graph.candidates)
    {
        graph.weightCount = 0;
        if (experiment->bodies[candidate.lower]->mass > 0 &&
            !tryWeights(graph, graph.polygons[candidate.lower], project(candidate.lowerPoint))) continue;
        double lowerShift = 0;
        for (int i = 0; i < graph.weightCount; ++i)
            lowerShift += graph.weights[i].weight * graph.shifts[graph.weights[i].contact];
        const auto& contact = contacts[candidate.contact];
        int index = (int)data.supports.size();
        data.supports.push_back({contact.source, candidate.direction, (int)data.loads.size(), graph.weightCount});
        graph.pointSupported[contact.source] = 1;
        data.loads.insert(data.loads.end(), graph.weights.begin(), graph.weights.begin() + graph.weightCount);
        graph.shifts.push_back(lowerShift + readHeight(candidate.lowerPoint - candidate.upperPoint));
        // Higher support polygons receive static support points only.
        if (!graph.movingBodies[contact.bodyA] && !graph.movingBodies[contact.bodyB] && contact.sticks)
            graph.polygons[candidate.upper].points.push_back({project(candidate.upperPoint), index});
        data.ranges.push_back(createRange(candidate.upper, readUpperAnchor(experiment, candidate),
            readHeight(candidate.upperPoint) + graph.shifts[index], (int)data.loads.size(), 1));
        data.loads.push_back({index, 1});
    }
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
    {
        Rigid* rigid = experiment->bodies[body];
        float3 center = readCenter(rigid);
        if (rigid->mass == 0 || graph.movingBodies[body] ||
            !tryWeights(graph, graph.polygons[body], project(center))) continue;
        double height = readHeight(center);
        for (int i = 0; i < graph.weightCount; ++i)
            height += graph.weights[i].weight * graph.shifts[graph.weights[i].contact];
        data.ranges.push_back(createRange(body, {0, 0, 0}, height,
            (int)data.loads.size(), graph.weightCount));
        data.loads.insert(data.loads.end(), graph.weights.begin(), graph.weights.begin() + graph.weightCount);
        graph.bodySupported[body] = 1;
    }
    for (const auto& node : data.supports)
    {
        const auto& contact = contacts[node.contact];
        int a = contact.bodyA, b = contact.bodyB;
        if ((experiment->bodies[a]->mass > 0 && !graph.bodySupported[a]) ||
            (experiment->bodies[b]->mass > 0 && !graph.bodySupported[b])) continue;
        static_cast<ContactManifold*>(experiment->contactReferences[node.contact].manifold)->supported = true;
    }
    transferUnsupportedMultipliers(experiment);
    researchFinishSupportRanges(experiment);
    std::fill(data.rangeForces.begin() + data.jointRangeCount, data.rangeForces.end(), float2{0, 0});
    updateDynamicContacts(experiment);
    RESEARCH_MAX(SupportNodes, data.supports.size());
    graph.contactVersion = experiment->contactVersion;
    graph.geometryVersion = experiment->contactGeometryVersion;
    graph.jointVersion = experiment->jointVersion;
}

bool researchKeepSupportGraph()
{
#ifdef DEMO_2D
    return false;
#else
    auto* experiment = researchStep->experiment;
    auto& graph = experiment->supportGraph;
    if (experiment->newton.compliant || experiment->researchExtensions
        || graph.jointVersion != experiment->jointVersion
        || experiment->longRange.supports.empty() || graph.manifoldCount <= 0
        || graph.contactVersion != experiment->contactVersion || graph.geometryVersion != experiment->contactGeometryVersion) return false;
    for (int index = 0; index < researchStep->bodyCount; ++index)
    {
        if (!graph.contactIslandBodies[index]) continue;
        Rigid* body = experiment->bodies[index];
        const auto& pose = graph.poses[index];
        float motion = length(body->positionLin - pose.center) + 2 * length(body->positionAng - pose.rotation) * body->radius;
        if (motion > contactGeometryAccuracy) return false;
    }
    return true;
#endif
}

EXPORT void demo_build_supports(Experiment* experiment)
{
    RESEARCH_TIME(phaseTime, SupportBuild);
    if (experiment->solver.gravity == 0)
    {
        experiment->supportGraph.manifoldCount = 0;
        demo_lrc_supports(experiment, nullptr, 0, nullptr, 0, nullptr, 0);
        return;
    }
    if (researchKeepSupportGraph())
    {
        RESEARCH_COUNT(GraphReuses, 1);
        researchBeginSupportRanges(experiment);
        return;
    }
    SupportContact* contacts;
    demo_support_contacts(experiment, &contacts);
    auto& graph = experiment->supportGraph;
    if (researchStep->currentIteration == 0)
    {
        graph.dynamicContacts = false;
        graph.movingBodies.assign(experiment->bodies.size(), 0);
    }
    updateMovingBodies(experiment);
    buildSupports(experiment);
}

// A moving component returns released support reactions to contact multipliers.
// Direct ground normal rows stay in the same AVBD body solve.
void researchUpdateSupports()
{
    RESEARCH_TIME(phaseTime, SupportRefresh);
    Experiment* experiment = researchStep->experiment;
    if (!experiment->longRange.enabled || experiment->solver.gravity == 0) return;
    auto& graph = experiment->supportGraph;
    if (graph.manifoldCount == 0) return;
    if (!experiment->newton.compliant && !experiment->researchExtensions && graph.manifoldCount > 0)
    {
        // Project cache: apply the existing horizontal/rotation invalidation
        // before exporting contacts. Step.cpp invalidates new contact candidates.
        bool changed = false;
        for (int index = 0; index < (int)experiment->bodies.size(); ++index)
        {
            Rigid* body = experiment->bodies[index];
            if (body->mass <= 0 || !graph.contactIslandBodies[index] || graph.movingBodies[index]) continue;
            const auto& pose = graph.poses[index];
#ifdef DEMO_2D
            float x = body->position.x;
            changed = fabsf(x - body->initial.x) > distanceTolerance || fabsf(x - pose.center.x) > distanceTolerance
                || fabsf(body->position.z - body->initial.z) * body->radius > distanceTolerance
                || fabsf(body->position.z - pose.angle) * body->radius > distanceTolerance;
#else
            float2 travel = project(body->positionLin - pose.center);
            changed = dot(travel, travel) > distanceTolerance * distanceTolerance
                || length(body->positionAng - pose.rotation) * body->radius > distanceTolerance;
#endif
            if (changed) break;
        }
        if (!changed) return;
    }
    if (graph.dynamicContacts && experiment->longRange.ranges.size() == experiment->longRange.jointRangeCount)
    {
        bool movingOnly = true;
        for (int index = 0; index < (int)experiment->bodies.size(); ++index)
            if (researchBodyAwake(experiment->bodies[index]) && !graph.movingBodies[index])
            {
                movingOnly = false;
                break;
            }
        if (movingOnly) return;
    }
    bool changed = graph.manifoldCount < 0;
    auto previousCount = experiment->contactReferences.size();
    SupportContact* contacts;
    demo_support_contacts(experiment, &contacts);
    changed |= updateMovingBodies(experiment);
    changed |= previousCount != experiment->contactReferences.size();
    if (!changed && experiment->longRange.supports.empty())
    {
        if (!graph.bodySupported.empty()) updateDynamicContacts(experiment);
        return;
    }
    for (int index = 0; !changed && index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (body->mass <= 0 || graph.movingBodies[index]) continue;
        const auto& pose = graph.poses[index];
        float2 horizontal = project(readCenter(body) - pose.center);
#ifdef DEMO_2D
        float rotationDistance = fabsf(body->position.z - pose.angle) * body->radius;
#else
        float rotationDistance = length(body->positionAng - pose.rotation) * body->radius;
#endif
        if (dot(horizontal, horizontal) > distanceTolerance * distanceTolerance || rotationDistance > distanceTolerance)
        {
            changed = true;
            break;
        }
    }
    if (changed)
    {
        buildSupports(experiment);
    }
    else updateDynamicContacts(experiment);
}

bool researchSupportedContact(Manifold* manifold)
{
    return !researchStep->experiment->longRange.supports.empty()
        && static_cast<ContactManifold*>(manifold)->supported;
}

void researchSelectIterations()
{
    auto* step = researchStep;
    if (!step->experiment->researchExtensions) return;
    researchClassifyJoints();
    if (step->experiment->jointTree.localContacts &&
        (step->experiment->supportGraph.dynamicContacts || !step->experiment->jointTree.contactBodies.empty()))
        step->experiment->solver.iterations = std::max(step->experiment->solver.iterations,
            std::max(step->configuredIterations, 8));
}

#ifdef DEMO_2D
void researchRefreshPlanarContact(Manifold* manifold, float3 normal, float gap)
{
    float witnessGap = INFINITY;
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        const auto& contact = manifold->contacts[point];
        float2 separation = manifold->bodyA->position.xy() - manifold->bodyB->position.xy()
            + researchRotateContactPoint(manifold->bodyA->position.z, contact.rA)
            - researchRotateContactPoint(manifold->bodyB->position.z, contact.rB);
        witnessGap = min(witnessGap, dot(contact.normal, separation));
    }
    bool changedNormal = manifold->numContacts == 0
        || dot(normal.xy(), manifold->contacts[0].normal) < .995f;
    if (!changedNormal && witnessGap <= gap + constraintAccuracy) return;
    initializeNewtonContact(manifold, true);
    researchStep->experiment->supportGraph.manifoldCount = -1;
}
#endif

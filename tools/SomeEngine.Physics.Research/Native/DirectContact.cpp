#include "BridgeState.h"
#include "ResearchStep.h"

bool researchDistributePressure(const double points[8][2], int count, const double target[2], double* weights)
{
#ifdef DEMO_2D
    int left = 0, right = 0;
    for (int point = 1; point < count; ++point)
    {
        if (points[point][0] < points[left][0]) left = point;
        if (points[point][0] > points[right][0]) right = point;
    }
    double width = points[right][0] - points[left][0];
    if (width == 0) return false;
    double fraction = (target[0] - points[left][0]) / width;
    if (fraction * width < -contactGeometryAccuracy || (fraction - 1) * width > contactGeometryAccuracy) return false;
    fraction = std::clamp(fraction, 0.0, 1.0);
    weights[left] = 1 - fraction; weights[right] = fraction;
    return true;
#else
    for (int a = 0; a < count; ++a)
        for (int b = a + 1; b < count; ++b)
            for (int c = b + 1; c < count; ++c)
            {
                double bx = points[b][0] - points[a][0], by = points[b][1] - points[a][1];
                double cx = points[c][0] - points[a][0], cy = points[c][1] - points[a][1];
                double tx = target[0] - points[a][0], ty = target[1] - points[a][1];
                double area = bx * cy - by * cx;
                if (area == 0) continue;
                double wb = (tx * cy - ty * cx) / area, wc = (bx * ty - by * tx) / area;
                double wa = 1 - wb - wc;
                double magnitude = fabs(area);
                if (wa < 0 && -wa * magnitude > contactGeometryAccuracy * sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy))) continue;
                if (wb < 0 && -wb * magnitude > contactGeometryAccuracy * sqrt(cx * cx + cy * cy)) continue;
                if (wc < 0 && -wc * magnitude > contactGeometryAccuracy * sqrt(bx * bx + by * by)) continue;
                wa = std::max(0.0, wa); wb = std::max(0.0, wb); wc = std::max(0.0, wc);
                double total = wa + wb + wc;
                weights[a] = wa / total; weights[b] = wb / total; weights[c] = wc / total;
                return true;
            }
    return false;
#endif
}

#ifndef DEMO_2D
static int readPlaneAxis(Rigid* body, float3 normal)
{
    int selected = -1;
    float deviation = INFINITY;
    for (int axis = 0; axis < 3; ++axis)
    {
        float3 direction{0, 0, 0}; direction[axis] = 1;
        float error = lengthSq(cross(normal, rotate(body->initialAng, direction)));
        if (error < deviation) { deviation = error; selected = axis; }
    }
    return deviation * body->radius * body->radius <= contactGeometryAccuracy * contactGeometryAccuracy ? selected : -1;
}

// The translation branch of a single rigid body's planar Coulomb contact:
// maximum dissipation fixes its tangent impulse, and a feasible center of
// pressure supplies zero angular impulse. Other islands keep the AVBD path.
static bool solvePlane(Experiment* experiment, int index)
{
    Rigid* body = experiment->bodies[index];
    if (lengthSq(body->velocityAng) != 0) return false;
    Manifold* plane = nullptr;
    for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
    {
        auto* manifold = dynamic_cast<Manifold*>(force);
        if (!manifold) return false;
        if (manifold->numContacts == 0) continue;
        if (plane || (manifold->bodyA->mass > 0 && manifold->bodyB->mass > 0)) return false;
        plane = manifold;
    }
    if (!plane || plane->numContacts < 3) return false;
    float side = plane->bodyA == body ? 1.0f : -1.0f;
    float3 normal = plane->basis[0] * side;
    Rigid* floor = plane->bodyA == body ? plane->bodyB : plane->bodyA;
    int bodyAxis = readPlaneAxis(body, normal), floorAxis = readPlaneAxis(floor, normal);
    if (bodyAxis < 0 || floorAxis < 0) return false;
    float3 tangents[2] = {plane->basis[1], plane->basis[2]};
    float3 offsets[8];
    double points[8][2];
    double separation = -(body->size[bodyAxis] + floor->size[floorAxis]) * 0.5;
    double normalLength = sqrt((double)normal.x * normal.x + (double)normal.y * normal.y + (double)normal.z * normal.z);
    for (int axis = 0; axis < 3; ++axis)
        separation += (normal[axis] / normalLength) * ((double)body->initialLin[axis] - floor->initialLin[axis]);
    float gap = (float)separation, depth = -body->size[bodyAxis] * 0.5f;
    for (int i = 0; i < plane->numContacts; ++i)
    {
        const auto& contact = plane->contacts[i];
        offsets[i] = rotate(body->initialAng, side > 0 ? contact.rA : contact.rB);
        for (int axis = 0; axis < 2; ++axis) points[i][axis] = dot(offsets[i], tangents[axis]);
    }
    float dt = experiment->solver.dt;
    float3 freeVelocity = body->velocityLin + float3{0, 0, experiment->solver.gravity * dt};
    float freeNormal = dot(freeVelocity, normal);
    float impulse = body->mass * (-gap / dt - freeNormal);
    if (impulse <= 0) return false;
    float3 tangentVelocity = freeVelocity - normal * freeNormal;
    float speed = length(tangentVelocity);
    float3 tangentImpulse = speed > 0 ? tangentVelocity * (-std::min(body->mass, plane->friction * impulse / speed)) : float3{0, 0, 0};
    float3 center = tangentImpulse * (depth / impulse);
    double target[2] = {dot(center, tangents[0]), dot(center, tangents[1])};
    double weights[8]{};
    if (!researchDistributePressure(points, plane->numContacts, target, weights)) return false;
    float3 velocity = tangentVelocity + tangentImpulse / body->mass - normal * (gap / dt);
    body->positionLin = body->initialLin + velocity * dt;
    body->positionAng = body->initialAng;
    experiment->islands.contactVelocities[index] = velocity;
    float3 force = (normal * impulse + tangentImpulse) / dt;
    for (int i = 0; i < plane->numContacts; ++i)
    {
        plane->contacts[i].lambda = plane->basis * (force * (float)(-side * weights[i]));
        plane->contacts[i].stick = lengthSq(tangentVelocity + tangentImpulse / body->mass) == 0;
    }
    clearContactReactions(experiment, plane);
    experiment->contactForces.erase(plane);
    experiment->islands.directContacts[index] = 1;
    ++experiment->islands.contactSolves;
    return true;
}
#endif

void researchSolveContactBlocks()
{
    Experiment* experiment = researchStep->experiment;
    if (!experiment->longRange.enabled) return;
    researchSolveContactColumns();
#ifndef DEMO_2D
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (researchBodySolves(experiment->bodies[body]) && experiment->islands.sizes[experiment->islands.roots[body]] == 1)
            solvePlane(experiment, body);
#endif
}

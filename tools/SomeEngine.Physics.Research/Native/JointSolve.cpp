#include "BridgeState.h"
#include "ResearchStep.h"

#ifdef DEMO_2D
static constexpr int coordinates = 3;
static constexpr int translations = 2;
#else
static constexpr int coordinates = 6;
static constexpr int translations = 3;
#endif

static float readContactAlpha(Experiment* experiment)
{
    if (experiment->lastStabilizationIterations > 0) return 0;
#ifdef DEMO_2D
    if (experiment->solver.postStabilize) return 1;
#endif
    return 1;
}

static bool hardJoint(Joint* joint)
{
#ifdef DEMO_2D
    return isinf(joint->stiffness[0]) && isinf(joint->stiffness[1]);
#else
    return isinf(joint->stiffnessLin) && !joint->broken;
#endif
}

static float3 position(Rigid* body)
{
#ifdef DEMO_2D
    return {body->position.x, body->position.y, 0};
#else
    return body->positionLin;
#endif
}

static float3 offset(Rigid* body, float3 local)
{
    if (!body) return local;
#ifdef DEMO_2D
    float2 result = rotate(body->position.z, {local.x, local.y});
    return {result.x, result.y, 0};
#else
    return rotate(body->positionAng, local);
#endif
}

static void buildTree(Experiment* experiment)
{
    auto& tree = experiment->jointTree;
    int count = (int)experiment->bodies.size();
    tree.version = experiment->jointVersion;
    tree.bodies.assign(count + 1, {});
    tree.adjacent.assign(count + 1, {});
    tree.visited.assign(count + 1, 0);
    tree.order.clear();
    for (int index = 0; index < (int)experiment->joints.size(); ++index)
    {
        Joint* joint = experiment->joints[index];
        if (!hardJoint(joint)) continue;
        int a = joint->bodyA ? experiment->index(joint->bodyA) : count;
        int b = experiment->index(joint->bodyB);
        tree.adjacent[a].push_back({b, index});
        tree.adjacent[b].push_back({a, index});
    }
    auto visit = [&](int root)
    {
        if (tree.visited[root] || tree.adjacent[root].empty()) return;
        size_t first = tree.order.size();
        tree.visited[root] = 1; tree.order.push_back(root);
        for (size_t next = first; next < tree.order.size(); ++next)
        {
            int parent = tree.order[next];
            for (auto [body, joint] : tree.adjacent[parent])
            {
                if (tree.visited[body]) continue;
                tree.visited[body] = 1;
                tree.bodies[body].parent = parent;
                tree.bodies[body].joint = joint;
                tree.order.push_back(body);
            }
        }
    };
    visit(count);
    for (int body = 0; body < count; ++body) if (experiment->bodies[body]->mass == 0) visit(body);
    for (int body = 0; body < count; ++body) visit(body);
}

void researchClassifyJoints()
{
    Experiment* experiment = researchStep->experiment;
    if (!experiment->researchExtensions) return;
    auto& tree = experiment->jointTree;
    if (tree.version != experiment->jointVersion) buildTree(experiment);
    tree.activeBodies = tree.visited;
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (experiment->islands.sleeping[body]) tree.activeBodies[body] = 0;
    tree.contactBodies.clear();
    if (experiment->joints.empty())
    {
        tree.localContacts = true;
        return;
    }
    auto contact = [&](Rigid* body)
    {
        int index = experiment->index(body);
        if (body->mass > 0 && tree.activeBodies[index])
        {
            tree.activeBodies[index] = 0;
            tree.contactBodies.push_back(index);
        }
    };
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            if (!researchContactActive(manifold)) continue;
            contact(manifold->bodyA); contact(manifold->bodyB);
        }
    for (int next = 0; next < (int)tree.contactBodies.size(); ++next)
        for (auto [neighbor, joint] : tree.adjacent[tree.contactBodies[next]])
            if (neighbor < (int)experiment->bodies.size()) contact(experiment->bodies[neighbor]);
    // A height range and a joint-tree correction share a body only through
    // the local AVBD path; every other mechanical component can propagate
    // its original contact forces through the joint equations.
    for (int body : tree.contactBodies) tree.activeBodies[body] = 1;
    tree.expansion.clear();
    auto support = [&](int body)
    {
        if (body < (int)experiment->bodies.size() && experiment->bodies[body]->mass > 0 && tree.activeBodies[body])
        {
            tree.activeBodies[body] = 0;
            tree.expansion.push_back(body);
        }
    };
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (!experiment->longRange.bodySupports[body].empty()) support(body);
    for (int next = 0; next < (int)tree.expansion.size(); ++next)
        for (auto [neighbor, joint] : tree.adjacent[tree.expansion[next]]) support(neighbor);
    tree.localContacts = false;
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            if (!researchContactActive(manifold)) continue;
            for (Rigid* body : {manifold->bodyA, manifold->bodyB})
                if (body->mass > 0 && !tree.activeBodies[experiment->index(body)]) tree.localContacts = true;
        }
}

bool researchJointBody(int body)
{
    return body < 0 || (researchStep->experiment->researchExtensions && researchStep->experiment->jointTree.activeBodies[body] != 0);
}

static void factorize(const double* matrix, double* factor, int size)
{
    for (int column = 0; column < size; ++column)
    {
        double diagonal = matrix[column * 6 + column];
        for (int k = 0; k < column; ++k)
            diagonal -= factor[column * 6 + k] * factor[column * 6 + k] * factor[k * 6 + k];
        factor[column * 6 + column] = diagonal;
        for (int row = column + 1; row < size; ++row)
        {
            double value = matrix[row * 6 + column];
            for (int k = 0; k < column; ++k)
                value -= factor[row * 6 + k] * factor[column * 6 + k] * factor[k * 6 + k];
            factor[row * 6 + column] = value / diagonal;
        }
    }
}

static void substitute(const double* factor, double* value, int size)
{
    for (int row = 0; row < size; ++row)
        for (int column = 0; column < row; ++column) value[row] -= factor[row * 6 + column] * value[column];
    for (int row = 0; row < size; ++row) value[row] /= factor[row * 6 + row];
    for (int row = size - 1; row >= 0; --row)
        for (int column = row + 1; column < size; ++column) value[row] -= factor[column * 6 + row] * value[column];
}

static void addRow(JointTreeBody& body, const double* jacobian, double force, double penalty)
{
    for (int row = 0; row < coordinates; ++row)
    {
        body.gradient[row] += jacobian[row] * force;
        for (int column = 0; column < coordinates; ++column)
            body.hessian[row * 6 + column] += jacobian[row] * jacobian[column] * penalty;
    }
}

static bool selectedBody(Experiment* experiment, Rigid* body)
{
    return body->mass > 0 && experiment->jointTree.activeBodies[experiment->index(body)];
}

static void addContactSide(Experiment* experiment, Manifold* manifold, Rigid* rigid, float alpha, double weight)
{
    auto& body = experiment->jointTree.bodies[experiment->index(rigid)];
#ifdef DEMO_2D
    manifold->computeConstraint(alpha);
    manifold->computeDerivatives(rigid);
    for (int row = 0; row < manifold->rows(); ++row)
    {
        float force = clamp(manifold->penalty[row] * manifold->C[row] + manifold->lambda[row],
            manifold->fmin[row], manifold->fmax[row]);
        double jacobian[6] = {manifold->J[row].x, manifold->J[row].y, manifold->J[row].z};
        float penalty = row % 2 != 0 && manifold->fmin[row] != manifold->fmax[row]
            ? manifold->penalty[row] : researchContactPenalty(manifold, row, force);
        addRow(body, jacobian, force, weight * penalty);
        for (int axis = 0; axis < 3; ++axis)
            body.hessian[axis * 6 + axis] += weight * length(manifold->H[row].col(axis)) * fabsf(force);
    }
#else
    float3x3 linear{}, angular{}, cross{};
    float3 gradientLin{}, gradientAng{};
    researchAddJointContact(manifold, rigid, alpha, linear, angular, cross, gradientLin, gradientAng);
    for (int row = 0; row < 3; ++row)
    {
        body.gradient[row] += gradientLin[row];
        body.gradient[row + 3] += gradientAng[row];
        for (int column = 0; column < 3; ++column)
        {
            body.hessian[row * 6 + column] += weight * linear[row][column];
            body.hessian[(row + 3) * 6 + column + 3] += weight * angular[row][column];
            body.hessian[(row + 3) * 6 + column] += weight * cross[row][column];
            body.hessian[column * 6 + row + 3] += weight * cross[row][column];
        }
    }
#endif
}

static void pointJacobian(double* matrix, float3 r, double sign)
{
    for (int axis = 0; axis < translations; ++axis)
    {
        matrix[axis * 6 + axis] = sign;
#ifdef DEMO_2D
        matrix[axis * 6 + 2] = (axis == 0 ? -r.y : r.x) * sign;
#else
        float3 direction{}; direction[axis] = 1;
        float3 angular = cross(r, direction);
        for (int rotation = 0; rotation < 3; ++rotation) matrix[axis * 6 + rotation + 3] = angular[rotation] * sign;
#endif
    }
}

static void addJointSide(Experiment* experiment, Rigid* rigid, float3 r, float3 force, double sign)
{
    if (!rigid || rigid->mass <= 0) return;
    auto& body = experiment->jointTree.bodies[experiment->index(rigid)];
    double jacobian[36]{};
    pointJacobian(jacobian, r, sign);
    for (int axis = 0; axis < translations; ++axis) addRow(body, jacobian + axis * 6, force[axis], 0);
#ifdef DEMO_2D
    body.angularCurvature[0] -= sign * dot(r, force);
#else
    double radial = dot(r, force);
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column < 3; ++column)
            body.angularCurvature[row * 3 + column] += sign *
                (0.5 * (r[row] * force[column] + force[row] * r[column]) - (row == column ? radial : 0));
#endif
}

#ifndef DEMO_2D
static void rotateCurvature(double* matrix, double* vectors, int p, int q)
{
    double offDiagonal = matrix[p * 3 + q];
    if (offDiagonal == 0) return;
    double ratio = (matrix[q * 3 + q] - matrix[p * 3 + p]) / (2 * offDiagonal);
    double tangent = std::copysign(1.0, ratio) / (fabs(ratio) + sqrt(1 + ratio * ratio));
    double cosine = 1 / sqrt(1 + tangent * tangent), sine = tangent * cosine;
    for (int row = 0; row < 3; ++row)
    {
        double x = matrix[row * 3 + p], y = matrix[row * 3 + q];
        matrix[row * 3 + p] = cosine * x - sine * y;
        matrix[row * 3 + q] = sine * x + cosine * y;
        x = vectors[row * 3 + p]; y = vectors[row * 3 + q];
        vectors[row * 3 + p] = cosine * x - sine * y;
        vectors[row * 3 + q] = sine * x + cosine * y;
    }
    for (int column = 0; column < 3; ++column)
    {
        double x = matrix[p * 3 + column], y = matrix[q * 3 + column];
        matrix[p * 3 + column] = cosine * x - sine * y;
        matrix[q * 3 + column] = sine * x + cosine * y;
    }
}

static double determinant(const double* matrix)
{
    return matrix[0] * (matrix[4] * matrix[8] - matrix[5] * matrix[7])
        - matrix[1] * (matrix[3] * matrix[8] - matrix[5] * matrix[6])
        + matrix[2] * (matrix[3] * matrix[7] - matrix[4] * matrix[6]);
}
#endif

static void addCurvature(JointTreeBody& body)
{
#ifdef DEMO_2D
    double curvature = body.angularCurvature[0];
    if (body.hessian[14] + curvature <= 0) curvature = std::max(0.0, curvature);
    body.hessian[14] += curvature;
#else
    double total[9];
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column < 3; ++column)
            total[row * 3 + column] = body.hessian[(row + 3) * 6 + column + 3] + body.angularCurvature[row * 3 + column];
    if (total[0] > 0 && total[0] * total[4] > total[1] * total[1] && determinant(total) > 0)
    {
        for (int row = 0; row < 3; ++row)
            for (int column = 0; column < 3; ++column)
                body.hessian[(row + 3) * 6 + column + 3] = total[row * 3 + column];
        return;
    }
    double values[9], vectors[9]{};
    std::copy(body.angularCurvature, body.angularCurvature + 9, values);
    vectors[0] = vectors[4] = vectors[8] = 1;
    for (int sweep = 0; sweep < 4; ++sweep)
    {
        rotateCurvature(values, vectors, 0, 1); rotateCurvature(values, vectors, 0, 2); rotateCurvature(values, vectors, 1, 2);
    }
    for (int axis = 0; axis < 3; ++axis)
        for (int row = 0; row < 3; ++row)
            for (int column = 0; column < 3; ++column)
                body.hessian[(row + 3) * 6 + column + 3] += vectors[row * 3 + axis]
                    * std::max(0.0, values[axis * 3 + axis]) * vectors[column * 3 + axis];
#endif
}

static bool initializeBodies(Experiment* experiment)
{
    auto& tree = experiment->jointTree;
    const auto& selected = tree.activeBodies;
    double inverseTimeSquared = 1.0 / (experiment->solver.dt * experiment->solver.dt);
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        auto& data = tree.bodies[index];
        std::fill(data.hessian, data.hessian + 36, 0);
        std::fill(data.gradient, data.gradient + 6, 0);
        std::fill(data.angularCurvature, data.angularCurvature + 9, 0);
        std::fill(data.change, data.change + 6, 0);
        Rigid* body = experiment->bodies[index];
        if (!selected[index] || body->mass <= 0) continue;
#ifdef DEMO_2D
        float3 difference = body->position - body->inertial;
        for (int axis = 0; axis < coordinates; ++axis)
        {
            double inertia = axis < 2 ? body->mass : body->moment;
            data.hessian[axis * 6 + axis] = inertia * inverseTimeSquared;
            data.gradient[axis] = inertia * inverseTimeSquared * difference[axis];
        }
#else
        float3 difference = body->positionLin - body->inertialLin;
        for (int axis = 0; axis < 3; ++axis)
        {
            data.hessian[axis * 6 + axis] = body->mass * inverseTimeSquared;
            data.gradient[axis] = body->mass * inverseTimeSquared * difference[axis];
        }
        AngularInertia angular = researchAngularInertia(body);
        for (int row = 0; row < 3; ++row)
        {
            data.gradient[row + 3] = angular.gradient[row] * inverseTimeSquared;
            for (int column = 0; column < 3; ++column)
                data.hessian[(row + 3) * 6 + column + 3] = angular.hessian[row * 3 + column] * inverseTimeSquared;
        }
#endif
    }
    for (Joint* joint : experiment->joints)
    {
        if (!hardJoint(joint) || !selected[experiment->index(joint->bodyB)]) continue;
#ifdef DEMO_2D
        float3 rA = offset(joint->bodyA, {joint->rA.x, joint->rA.y, 0});
        float3 rB = offset(joint->bodyB, {joint->rB.x, joint->rB.y, 0});
        float3 force{joint->lambda[0], joint->lambda[1], 0};
#else
        float3 rA = offset(joint->bodyA, joint->rA), rB = offset(joint->bodyB, joint->rB);
        float3 force = joint->lambdaLin;
#endif
        addJointSide(experiment, joint->bodyA, rA, force, 1);
        addJointSide(experiment, joint->bodyB, rB, force, -1);
#ifdef DEMO_2D
        if (isinf(joint->stiffness[2]))
        {
            double torque = joint->lambda[2] * joint->torqueArm;
            if (joint->bodyA && joint->bodyA->mass > 0) tree.bodies[experiment->index(joint->bodyA)].gradient[2] += torque;
            if (joint->bodyB->mass > 0) tree.bodies[experiment->index(joint->bodyB)].gradient[2] -= torque;
        }
#else
        if (isinf(joint->stiffnessAng))
            for (int axis = 0; axis < 3; ++axis)
            {
                double torque = joint->lambdaAng[axis] * joint->torqueArm;
                if (joint->bodyA && joint->bodyA->mass > 0) tree.bodies[experiment->index(joint->bodyA)].gradient[axis + 3] += torque;
                if (joint->bodyB->mass > 0) tree.bodies[experiment->index(joint->bodyB)].gradient[axis + 3] -= torque;
            }
#endif
    }
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        if (selected[body] && experiment->bodies[body]->mass > 0)
        {
            addCurvature(tree.bodies[body]);
            std::fill(tree.bodies[body].angularCurvature, tree.bodies[body].angularCurvature + 9, 0);
        }
    float alpha = readContactAlpha(experiment);
    bool contactCandidates = false;
    for (Force* force = experiment->solver.forces; force; force = force->next)
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            bool a = selectedBody(experiment, manifold->bodyA), b = selectedBody(experiment, manifold->bodyB);
            contactCandidates |= a || b;
            double weight = a && b ? 2 : 1;
            if (a) addContactSide(experiment, manifold, manifold->bodyA, alpha, weight);
            if (b) addContactSide(experiment, manifold, manifold->bodyB, alpha, weight);
        }
    return contactCandidates;
}

static int jointRows(Joint* joint, Rigid* child, double* childJacobian, double* parentJacobian, double* error)
{
#ifdef DEMO_2D
    float3 rA = offset(joint->bodyA, {joint->rA.x, joint->rA.y, 0});
    float3 rB = offset(joint->bodyB, {joint->rB.x, joint->rB.y, 0});
#else
    float3 rA = offset(joint->bodyA, joint->rA), rB = offset(joint->bodyB, joint->rB);
#endif
    float3 delta = (joint->bodyA ? position(joint->bodyA) + rA : rA) - position(joint->bodyB) - rB;
    bool childIsA = child == joint->bodyA;
    pointJacobian(childJacobian, childIsA ? rA : rB, childIsA ? 1 : -1);
    pointJacobian(parentJacobian, childIsA ? rB : rA, childIsA ? -1 : 1);
    for (int axis = 0; axis < translations; ++axis) error[axis] = delta[axis];
#ifdef DEMO_2D
    if (!isinf(joint->stiffness[2])) return 2;
    childJacobian[2 * 6 + 2] = (childIsA ? 1 : -1) * joint->torqueArm;
    parentJacobian[2 * 6 + 2] = -childJacobian[2 * 6 + 2];
    error[2] = ((joint->bodyA ? joint->bodyA->position.z : 0) - joint->bodyB->position.z - joint->restAngle) * joint->torqueArm;
    return 3;
#else
    if (!isinf(joint->stiffnessAng)) return 3;
    float3 angular = ((joint->bodyA ? joint->bodyA->positionAng : quat{0, 0, 0, 1}) - joint->bodyB->positionAng) * joint->torqueArm;
    for (int axis = 0; axis < 3; ++axis)
    {
        childJacobian[(axis + 3) * 6 + axis + 3] = (childIsA ? 1 : -1) * joint->torqueArm;
        parentJacobian[(axis + 3) * 6 + axis + 3] = -childJacobian[(axis + 3) * 6 + axis + 3];
        error[axis + 3] = angular[axis];
    }
    return 6;
#endif
}

static void eliminateBodies(Experiment* experiment)
{
    auto& tree = experiment->jointTree;
    int count = (int)experiment->bodies.size();
    for (auto iterator = tree.order.rbegin(); iterator != tree.order.rend(); ++iterator)
    {
        int index = *iterator;
        if (index == count || !tree.activeBodies[index] || experiment->bodies[index]->mass <= 0) continue;
        auto& body = tree.bodies[index];
        factorize(body.hessian, body.factor, coordinates);
        for (int axis = 0; axis < coordinates; ++axis) body.freeChange[axis] = -body.gradient[axis];
        substitute(body.factor, body.freeChange, coordinates);
        if (body.joint < 0) continue;
        double childJacobian[36]{}, constraint[6]{};
        std::fill(body.parentJacobian, body.parentJacobian + 36, 0);
        body.rows = jointRows(experiment->joints[body.joint], experiment->bodies[index], childJacobian, body.parentJacobian, constraint);
        double matrix[36]{}, factor[36]{};
        for (int row = 0; row < body.rows; ++row)
        {
            double response[6]{};
            for (int axis = 0; axis < coordinates; ++axis) response[axis] = childJacobian[row * 6 + axis];
            substitute(body.factor, response, coordinates);
            for (int axis = 0; axis < coordinates; ++axis) body.response[axis * 6 + row] = response[axis];
            body.right[row] = constraint[row];
            for (int axis = 0; axis < coordinates; ++axis) body.right[row] += childJacobian[row * 6 + axis] * body.freeChange[axis];
            for (int column = 0; column < body.rows; ++column)
                for (int axis = 0; axis < coordinates; ++axis)
                    matrix[column * 6 + row] += childJacobian[column * 6 + axis] * response[axis];
        }
        factorize(matrix, factor, body.rows);
        for (int column = 0; column < body.rows; ++column)
        {
            double inverse[6]{}; inverse[column] = 1;
            substitute(factor, inverse, body.rows);
            for (int row = 0; row < body.rows; ++row) body.inverse[row * 6 + column] = inverse[row];
        }
        if (body.parent == count || experiment->bodies[body.parent]->mass <= 0) continue;
        auto& parent = tree.bodies[body.parent];
        double weighted[36]{}, reaction[6]{};
        for (int row = 0; row < body.rows; ++row)
            for (int k = 0; k < body.rows; ++k)
            {
                reaction[row] += body.inverse[row * 6 + k] * body.right[k];
                for (int axis = 0; axis < coordinates; ++axis)
                    weighted[row * 6 + axis] += body.inverse[row * 6 + k] * body.parentJacobian[k * 6 + axis];
            }
        for (int row = 0; row < coordinates; ++row)
            for (int k = 0; k < body.rows; ++k)
            {
                parent.gradient[row] += body.parentJacobian[k * 6 + row] * reaction[k];
                for (int column = 0; column < coordinates; ++column)
                    parent.hessian[row * 6 + column] += body.parentJacobian[k * 6 + row] * weighted[k * 6 + column];
            }
    }
}

static void applyChanges(Experiment* experiment)
{
    auto& tree = experiment->jointTree;
    int count = (int)experiment->bodies.size();
    for (int index : tree.order)
    {
        if (index == count || !tree.activeBodies[index] || experiment->bodies[index]->mass <= 0) continue;
        auto& body = tree.bodies[index];
        std::copy(body.freeChange, body.freeChange + coordinates, body.change);
        if (body.joint >= 0)
        {
            const auto& parent = tree.bodies[body.parent];
            double right[6], reaction[6]{};
            for (int row = 0; row < body.rows; ++row)
            {
                right[row] = body.right[row];
                for (int axis = 0; axis < coordinates; ++axis) right[row] += body.parentJacobian[row * 6 + axis] * parent.change[axis];
            }
            for (int row = 0; row < body.rows; ++row)
            {
                for (int column = 0; column < body.rows; ++column) reaction[row] += body.inverse[row * 6 + column] * right[column];
                for (int axis = 0; axis < coordinates; ++axis) body.change[axis] -= body.response[axis * 6 + row] * reaction[row];
            }
            Joint* joint = experiment->joints[body.joint];
#ifdef DEMO_2D
            for (int row = 0; row < body.rows; ++row) joint->lambda[row] += (float)reaction[row];
#else
            for (int row = 0; row < body.rows; ++row)
                if (row < 3) joint->lambdaLin[row] += (float)reaction[row];
                else joint->lambdaAng[row - 3] += (float)reaction[row];
#endif
        }
    }
    for (int index : tree.order)
    {
        if (index == count || !tree.activeBodies[index] || experiment->bodies[index]->mass <= 0) continue;
        Rigid* rigid = experiment->bodies[index];
        const auto& body = tree.bodies[index];
#ifdef DEMO_2D
        rigid->position += float3{(float)body.change[0], (float)body.change[1], (float)body.change[2]};
#else
        rigid->positionLin += float3{(float)body.change[0], (float)body.change[1], (float)body.change[2]};
        rigid->positionAng = researchAdvanceRotation(rigid->positionAng, float3{(float)body.change[3], (float)body.change[4], (float)body.change[5]});
#endif
        researchBodyChanged(index);
        tree.solvedBodies[index] = 1;
    }
}

// Original joint equations use body-tree elimination and their own multipliers.
// Contact self blocks majorize their coupled motion in the same tree step.
void researchSolveConstraints(int sweeps)
{
    Experiment* experiment = researchStep->experiment;
    if (!experiment->researchExtensions) return;
    researchClassifyJoints();
    auto& tree = experiment->jointTree;
    bool active = false;
    for (int body = 0; body < (int)experiment->bodies.size(); ++body)
        active |= tree.activeBodies[body] && experiment->bodies[body]->mass > 0;
    if (!active) return;
    tree.solvedBodies.assign(experiment->bodies.size(), 0);
    bool addedContacts = false;
    for (int sweep = 0; sweep < sweeps || addedContacts; ++sweep)
    {
        ++experiment->lastTreeIterations;
        bool contactCandidates = initializeBodies(experiment);
        eliminateBodies(experiment);
        applyChanges(experiment);
        addedContacts = researchAddMovedContacts();
        contactCandidates |= addedContacts;
        if (contactCandidates)
        {
            researchRefreshContacts();
            researchUpdateSupports();
            float alpha = readContactAlpha(experiment);
            for (Force* force = experiment->solver.forces; force; force = force->next)
                if (auto* manifold = dynamic_cast<Manifold*>(force))
                {
                    if (!selectedBody(experiment, manifold->bodyA) && !selectedBody(experiment, manifold->bodyB)) continue;
#ifdef DEMO_2D
                    manifold->computeConstraint(alpha);
                    for (int row = 0; row < manifold->rows(); ++row)
                    {
                        manifold->lambda[row] = clamp(manifold->penalty[row] * manifold->C[row] + manifold->lambda[row],
                            manifold->fmin[row], manifold->fmax[row]);
                        if (manifold->lambda[row] > manifold->fmin[row] && manifold->lambda[row] < manifold->fmax[row])
                            manifold->penalty[row] = min(manifold->penalty[row] + experiment->solver.beta * fabsf(manifold->C[row]), PENALTY_MAX);
                    }
#else
                    manifold->updateDual(alpha);
#endif
                }
            researchClassifyJoints();
        }
    }
    auto& data = experiment->longRange;
    auto solved = [&](int body)
    { return body < 0 || experiment->bodies[body]->mass <= 0 || tree.solvedBodies[body]; };
    for (int index = 0; index < data.jointRangeCount; ++index)
    {
        const auto& bound = data.jointBounds[index];
        if (solved(data.jointAnchors[bound.anchorA].body)
            && solved(data.jointAnchors[bound.anchorB].body)) data.rangeForces[index] = {0, 0};
    }
}

// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: Copyright (c) 2025 The Newton Developers
// Rigid-body equations ported from Newton v1.7.0.
// Sources and stage ownership: References/Newton.md; References/Newton-LICENSE.md.
// Adaptive initial guess: Chris Giles, copyright (c) 2025/2026.
// Permission to use, copy, modify, distribute and sell this software
// and its documentation for any purpose is hereby granted without fee,
// provided that the above copyright notice appear in all copies.
// Chris Giles makes no representations about the suitability of this
// software for any purpose. It is provided "as is" without express or
// implied warranty.
#include "BridgeState.h"
#include "ResearchStep.h"
#include <cmath>

namespace
{
float3 vectorCross(float3 a, float3 b)
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}

float3x3 matrixTranspose(float3x3 value)
{
    return {value[0][0], value[1][0], value[2][0], value[0][1], value[1][1], value[2][1], value[0][2], value[1][2], value[2][2]};
}

struct MaterialTerms { float scale, stiffness, complement; };

// rigid_vbd_kernels._compliant_alm_coefficients / _alm_relaxed_ascent.
MaterialTerms materialTerms(float material, float rho)
{
    if (material <= 0 || rho <= 0) return {0, 0, 1};
    if (material >= rho)
    {
        float ratio = rho / material, scale = 1 / (1 + ratio);
        return {scale, rho * scale, ratio * scale};
    }
    float ratio = material / rho, complement = 1 / (1 + ratio);
    return {ratio * complement, material * complement, complement};
}

float3 materialForce(float3 residual, float3 multiplier, float material, float rho)
{
    auto terms = materialTerms(material, rho);
    return material >= rho ? multiplier + residual * terms.stiffness - multiplier * terms.complement
                           : multiplier * terms.scale + residual * terms.stiffness;
}

float3 center(Rigid* body)
{
    if (!body) return {0, 0, 0};
#ifdef DEMO_2D
    return {body->position.x, body->position.y, 0};
#else
    return body->positionLin;
#endif
}

float3 offset(Rigid* body, float3 local)
{
    if (!body) return local;
#ifdef DEMO_2D
    float2 value = rotate(body->position.z, local.xy());
    return {value.x, value.y, 0};
#else
    return rotate(body->positionAng, local);
#endif
}

float inverseMass(Rigid* body) { return body && body->mass > 0 ? 1 / body->mass : 0; }

#ifndef DEMO_2D
float3 rotationVector(quat value)
{
    if (value.w < 0) value = {-value.x, -value.y, -value.z, -value.w};
    float magnitude = length(value.vec());
    return magnitude == 0 ? float3{0, 0, 0} : value.vec() * (2 * atan2f(magnitude, value.w) / magnitude);
}

float3x3 rotationMatrix(quat value)
{
    return transpose(float3x3{rotate(value, {1, 0, 0}), rotate(value, {0, 1, 0}), rotate(value, {0, 0, 1})});
}

float3 jointRotation(Joint* joint, const NewtonJointData& data, float3x3* jacobian = nullptr)
{
    quat parent = joint->bodyA ? joint->bodyA->positionAng : quat{0, 0, 0, 1};
    quat alignment = inverse(parent) * joint->bodyB->positionAng * inverse(data.restRelative);
    float3 value = rotationVector(alignment);
    if (jacobian)
    {
        float3x3 cross = skew(value);
        float3x3 right = diagonal(1, 1, 1) + cross * 0.5f + (cross * cross) * (1.0f / 12.0f);
        *jacobian = rotationMatrix(parent) * rotationMatrix(alignment) * transpose(right);
    }
    return value;
}
#endif

float3 jointLinear(Joint* joint)
{
#ifdef DEMO_2D
    float3 localA{joint->rA.x, joint->rA.y, 0}, localB{joint->rB.x, joint->rB.y, 0};
#else
    float3 localA = joint->rA, localB = joint->rB;
#endif
    return center(joint->bodyA) + offset(joint->bodyA, localA)
        - center(joint->bodyB) - offset(joint->bodyB, localB);
}

void initializeJoints(Experiment* experiment)
{
    auto& data = experiment->newton;
    data.bodyJointStiffness.assign(experiment->bodies.size(), 0);
    float inverseStepSquared = 1 / (experiment->solver.dt * experiment->solver.dt);
    for (Joint* joint : experiment->joints)
    {
        auto [entry, inserted] = data.joints.try_emplace(joint);
        auto& state = entry->second;
#ifdef DEMO_2D
        state.linearMaterial = isinf(joint->stiffness[0]) ? data.jointLinearStiffness : joint->stiffness[0];
        state.angularMaterial = isinf(joint->stiffness[2]) ? data.jointAngularStiffness
            : joint->stiffness[2] * joint->torqueArm * joint->torqueArm;
#else
        if (inserted)
        {
            quat parent = joint->bodyA ? joint->bodyA->positionAng : quat{0, 0, 0, 1};
            state.restRelative = inverse(parent) * joint->bodyB->positionAng;
        }
        state.linearMaterial = isinf(joint->stiffnessLin) ? data.jointLinearStiffness : joint->stiffnessLin;
        state.angularMaterial = isinf(joint->stiffnessAng) ? data.jointAngularStiffness : joint->stiffnessAng;
#endif
        float mobility = inverseMass(joint->bodyA) + inverseMass(joint->bodyB);
        float angularMobility = 0;
        for (Rigid* body : {joint->bodyA, joint->bodyB})
        {
            if (inverseMass(body) == 0) continue;
#ifdef DEMO_2D
            angularMobility += 1 / body->moment;
#else
            angularMobility += (1 / body->moment.x + 1 / body->moment.y + 1 / body->moment.z) / 3;
#endif
            int index = experiment->index(body);
            data.bodyJointStiffness[index] = max(data.bodyJointStiffness[index], state.linearMaterial);
        }
        // step_joint_C0_lambda_rho: bilateral k_eff is at least 0.9 K.
        state.linearRho = state.linearMaterial > 0 ? max(inverseStepSquared / mobility, 9 * state.linearMaterial) : 0;
        state.angularRho = state.angularMaterial > 0 ? max(inverseStepSquared / angularMobility, 9 * state.angularMaterial) : 0;
#ifdef DEMO_2D
        for (int axis = 0; axis < 3; ++axis)
        {
            joint->lambda[axis] *= experiment->solver.gamma;
            joint->penalty[axis] = axis < 2 ? state.linearRho : state.angularRho;
        }
        joint->C0 = jointLinear(joint);
        joint->C0.z = newtonPlanarDifference((joint->bodyA ? joint->bodyA->position.z : 0) - joint->bodyB->position.z, joint->restAngle);
#else
        joint->lambdaLin = joint->lambdaLin * experiment->solver.gamma;
        joint->lambdaAng = joint->lambdaAng * experiment->solver.gamma;
        joint->penaltyLin = {state.linearRho, state.linearRho, state.linearRho};
        joint->penaltyAng = {state.angularRho, state.angularRho, state.angularRho};
        joint->C0Lin = jointLinear(joint);
        joint->C0Ang = jointRotation(joint, state);
#endif
    }
}

float3x3 contactBasis(Manifold* manifold, int point)
{
#ifdef DEMO_2D
    float2 n = manifold->contacts[point].normal;
    return {n.x, n.y, 0, n.y, -n.x, 0, 0, 0, 1};
#else
    return manifold->basis;
#endif
}

float3 localContactPoint(const Manifold::Contact& contact, bool first)
{
#ifdef DEMO_2D
    float2 value = first ? contact.rA : contact.rB;
    return {value.x, value.y, 0};
#else
    return first ? contact.rA : contact.rB;
#endif
}

float3 contactMultiplier(Manifold* manifold, int point)
{
#ifdef DEMO_2D
    return {manifold->lambda[point * 2], manifold->lambda[point * 2 + 1], 0};
#else
    return manifold->contacts[point].lambda;
#endif
}

float3 contactRho(Manifold* manifold, int point)
{
#ifdef DEMO_2D
    return {manifold->penalty[point * 2], manifold->penalty[point * 2 + 1], 0};
#else
    return manifold->contacts[point].penalty;
#endif
}

void setContactMultiplier(Manifold* manifold, int point, float3 value)
{
#ifdef DEMO_2D
    manifold->lambda[point * 2] = value.x;
    manifold->lambda[point * 2 + 1] = value.y;
#else
    manifold->contacts[point].lambda = value;
#endif
}

float3 contactInitial(Manifold* manifold, int point)
{
#ifdef DEMO_2D
    float2 value = manifold->contacts[point].C0;
    return {value.x, value.y, 0};
#else
    return manifold->contacts[point].C0;
#endif
}

// _contact_conditioning_scale and _contact_tangent_conditioning_scale.
void initializeContactMetric(Manifold* manifold, int point)
{
    Experiment* experiment = researchStep->experiment;
    auto& contact = manifold->contacts[point];
    float3x3 basis = contactBasis(manifold, point);
    float structural[2]{};
    float3 offsets[2]{};
    double first = 0, second = 0, mixed = 0;
    float dt = experiment->solver.dt;
    int endpoint = 0;
    for (Rigid* body : {manifold->bodyA, manifold->bodyB})
    {
        if (body->mass > 0)
        {
            float3 r = offset(body, localContactPoint(contact, endpoint == 0));
            offsets[endpoint] = r;
            structural[endpoint] = experiment->newton.bodyJointStiffness[experiment->index(body)];
            float3 a = vectorCross(r, basis[1]), b = vectorCross(r, basis[2]);
            first += 1 / body->mass + dot(a, newtonInverseInertia(body, a));
            second += 1 / body->mass + dot(b, newtonInverseInertia(body, b));
            mixed += dot(a, newtonInverseInertia(body, b));
        }
        ++endpoint;
    }
    auto series = [](float a, float b) { float low = min(a, b), high = max(a, b); return low / (1 + low / high); };
    float normal = newtonPairConditioning(manifold->bodyA, offsets[0], manifold->bodyB, offsets[1], basis[0]);
    float shared = inverseMass(manifold->bodyA) > 0 && inverseMass(manifold->bodyB) > 0
        ? (structural[0] > 0 && structural[1] > 0 ? series(structural[0], structural[1]) : 0)
        : max(structural[0], structural[1]);
#ifdef DEMO_2D
    float tangent = (float)(1 / (dt * dt * first));
    manifold->penalty[point * 2] = normal;
    manifold->penalty[point * 2 + 1] = max(tangent, min(normal, shared));
#else
    double eigenvalue = 0.5 * (first + second + sqrt((first - second) * (first - second) + 4 * mixed * mixed));
    float tangent = max((float)(1 / (dt * dt * eigenvalue)), min(normal, shared));
    contact.penalty = {normal, tangent, tangent};
#endif
}

struct ContactEquation
{
    float3 force, residual, rA, rB;
    float3x3 stiffness;
    float normalDual = 0;
};

// evaluate_rigid_contact_from_collision and _compliant_contact_dual_step.
template<bool solveBlock = true>
ContactEquation contactEquation(Experiment* experiment, Manifold* manifold, int point, float alpha)
{
    ContactEquation result{};
    float3x3 basis = contactBasis(manifold, point);
    result.rA = offset(manifold->bodyA, localContactPoint(manifold->contacts[point], true));
    result.rB = offset(manifold->bodyB, localContactPoint(manifold->contacts[point], false));
    float3 raw = basis * (center(manifold->bodyA) - center(manifold->bodyB) + result.rA - result.rB);
    float3 frame = static_cast<ContactManifold*>(manifold)->frames[point];
    float3 initial = contactInitial(manifold, point);
    result.residual = {raw.x - alpha * frame.x, raw.y - initial.y + (1 - alpha) * frame.y,
        raw.z - initial.z + (1 - alpha) * frame.z};
    if (raw.x > 0) result.residual.x = raw.x;
    float3 rho = contactRho(manifold, point), lambda = contactMultiplier(manifold, point);
    auto terms = materialTerms(experiment->newton.contactStiffness, rho.x);
    result.force = {min(0.0f, terms.stiffness * result.residual.x + terms.scale * lambda.x),
        rho.y * result.residual.y + lambda.y, rho.z * result.residual.z + lambda.z};
    result.normalDual = result.force.x;
    auto* stored = findContactReactions(manifold);
    float3 extra = stored == nullptr ? float3{0, 0, 0} : (*stored)[point];
    float bound = -min(0.0f, result.force.x + extra.x) * manifold->friction;
    if (result.force.x == 0 && extra.x == 0) { result.force = {0, 0, 0}; return result; }
    if constexpr (solveBlock) result.stiffness[0][0] = result.force.x < 0 ? terms.stiffness : 0;
    float2 trial{result.force.y + extra.y, result.force.z + extra.z};
    float magnitude = length(trial);
    if constexpr (solveBlock) { result.stiffness[1][1] = rho.y; result.stiffness[2][2] = rho.z; }
    if (bound == 0)
    {
        result.force.y = -extra.y; result.force.z = -extra.z;
        if constexpr (solveBlock) result.stiffness[1][1] = result.stiffness[2][2] = 0;
    }
    else if (magnitude > bound)
    {
        result.force.y = trial.x * bound / magnitude - extra.y;
        result.force.z = trial.y * bound / magnitude - extra.z;
        if constexpr (solveBlock)
        {
            float x = trial.x / magnitude, y = trial.y / magnitude;
            float2 slip{raw.y - contactInitial(manifold, point).y, raw.z - contactInitial(manifold, point).z};
            float travel = length(slip), epsilon = 0.01f * manifold->solver->dt;
            float reciprocal = travel > epsilon ? 1 / travel : (2 - travel / epsilon) / epsilon;
            float along = bound * reciprocal, across = rho.y * bound / magnitude;
            result.stiffness[1][1] = across * y * y + along * x * x;
            result.stiffness[2][2] = across * x * x + along * y * y;
            result.stiffness[1][2] = result.stiffness[2][1] = (along - across) * x * y;
        }
    }
    // evaluate_rigid_contact_from_collision: approach damping is a material
    // force and block term; the ALM normal dual retains the elastic component.
    float damping = experiment->newton.contactDamping;
    float approach = (raw.x - initial.x) / manifold->solver->dt;
    if (damping > 0 && approach < 0 && result.normalDual < 0)
    {
        result.force.x += damping * approach;
        if constexpr (solveBlock) result.stiffness[0][0] += damping / manifold->solver->dt;
    }
    return result;
}

#ifdef DEMO_2D
void addContact(Manifold* manifold, Rigid* body, float alpha, float3x3& hessian, float3& gradient)
{
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto equation = contactEquation(researchStep->experiment, manifold, point, alpha);
        if (equation.stiffness[0][0] == 0 && equation.stiffness[1][1] == 0) continue;
        float3x3 basis = contactBasis(manifold, point);
        float3 r = body == manifold->bodyA ? equation.rA : equation.rB;
        float sign = body == manifold->bodyA ? 1.0f : -1.0f;
        float3 rows[2] = {{basis[0].x * sign, basis[0].y * sign, vectorCross(r, basis[0]).z * sign},
                         {basis[1].x * sign, basis[1].y * sign, vectorCross(r, basis[1]).z * sign}};
        for (int axis = 0; axis < 2; ++axis)
        {
            gradient += rows[axis] * equation.force[axis];
            for (int column = 0; column < 2; ++column)
                hessian += outer(rows[axis], rows[column]) * equation.stiffness[axis][column];
        }
    }
}

void addJoint(Joint* joint, Rigid* body, float alpha, float3x3& hessian, float3& gradient)
{
    const auto& state = researchStep->experiment->newton.joints.at(joint);
    float3 residual = jointLinear(joint) - joint->C0 * alpha;
    float3 lambda{joint->lambda[0], joint->lambda[1], 0};
    float3 force = materialForce(residual, lambda, state.linearMaterial, state.linearRho);
    float3 r = offset(body, body == joint->bodyA ? float3{joint->rA.x, joint->rA.y, 0} : float3{joint->rB.x, joint->rB.y, 0});
    float sign = body == joint->bodyA ? 1.0f : -1.0f;
    float3 rows[2] = {{sign, 0, -r.y * sign}, {0, sign, r.x * sign}};
    float stiffness = materialTerms(state.linearMaterial, state.linearRho).stiffness;
    for (int axis = 0; axis < 2; ++axis)
    {
        gradient += rows[axis] * force[axis];
        hessian += outer(rows[axis], rows[axis]) * stiffness;
    }
    if (state.angularMaterial > 0)
    {
        float angle = newtonPlanarDifference((joint->bodyA ? joint->bodyA->position.z : 0) - joint->bodyB->position.z, joint->restAngle) - joint->C0.z * alpha;
        auto terms = materialTerms(state.angularMaterial, state.angularRho);
        gradient.z += sign * (terms.stiffness * angle + terms.scale * joint->lambda[2]);
        hessian[2][2] += terms.stiffness;
    }
}
#else
void addContact(Manifold* manifold, Rigid* body, float alpha, float3x3& linear, float3x3& angular,
    float3x3& crossBlock, float3& gradientLin, float3& gradientAng)
{
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto equation = contactEquation(researchStep->experiment, manifold, point, alpha);
        if (equation.stiffness[0][0] == 0 && equation.stiffness[1][1] == 0 && equation.stiffness[2][2] == 0) continue;
        float3x3 jacobianLin = contactBasis(manifold, point) * (body == manifold->bodyA ? 1.0f : -1.0f);
        float3 r = body == manifold->bodyA ? equation.rA : equation.rB;
        float3x3 jacobianAng{cross(r, jacobianLin[0]), cross(r, jacobianLin[1]), cross(r, jacobianLin[2])};
        float3x3 angularWeighted = transpose(jacobianAng) * equation.stiffness;
        linear += transpose(jacobianLin) * equation.stiffness * jacobianLin;
        angular += angularWeighted * jacobianAng;
        crossBlock += angularWeighted * jacobianLin;
        gradientLin += transpose(jacobianLin) * equation.force;
        gradientAng += transpose(jacobianAng) * equation.force;
    }
}

void addJoint(Joint* joint, Rigid* body, float alpha, float3x3& linear, float3x3& angular,
    float3x3& crossBlock, float3& gradientLin, float3& gradientAng)
{
    const auto& state = researchStep->experiment->newton.joints.at(joint);
    float sign = body == joint->bodyA ? 1.0f : -1.0f;
    float3 force = materialForce(jointLinear(joint) - joint->C0Lin * alpha, joint->lambdaLin, state.linearMaterial, state.linearRho) * sign;
    float3 r = offset(body, body == joint->bodyA ? joint->rA : joint->rB);
    float3x3 rCross = skew(r);
    float stiffness = materialTerms(state.linearMaterial, state.linearRho).stiffness;
    linear += diagonal(stiffness, stiffness, stiffness);
    angular += transpose(rCross) * rCross * stiffness;
    crossBlock += rCross * stiffness;
    gradientLin += force;
    gradientAng += cross(r, force);
    if (state.angularMaterial > 0)
    {
        float3x3 jacobian;
        float3 residual = jointRotation(joint, state, &jacobian) - joint->C0Ang * alpha;
        auto terms = materialTerms(state.angularMaterial, state.angularRho);
        float3 angularForce = materialForce(residual, joint->lambdaAng, state.angularMaterial, state.angularRho);
        gradientAng += jacobian * angularForce * -sign;
        angular += jacobian * transpose(jacobian) * terms.stiffness;
    }
}
#endif

void updateDuals(float alpha)
{
    Experiment* experiment = researchStep->experiment;
    for (Force* force = experiment->solver.forces; force; force = force->next)
    {
        if (!researchForceSolves(force)) continue;
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            for (int point = 0; point < manifold->numContacts; ++point)
            {
                auto equation = contactEquation<false>(experiment, manifold, point, alpha);
                equation.force.x = equation.normalDual;
                setContactMultiplier(manifold, point, equation.force);
                manifold->contacts[point].stick = false;
            }
        }
        else if (auto* joint = dynamic_cast<Joint*>(force))
        {
            const auto& state = experiment->newton.joints.at(joint);
#ifdef DEMO_2D
            float3 lambda = materialForce(jointLinear(joint) - joint->C0 * alpha, {joint->lambda[0], joint->lambda[1], 0}, state.linearMaterial, state.linearRho);
            joint->lambda[0] = lambda.x; joint->lambda[1] = lambda.y;
            float angle = newtonPlanarDifference((joint->bodyA ? joint->bodyA->position.z : 0) - joint->bodyB->position.z, joint->restAngle) - joint->C0.z * alpha;
            auto terms = materialTerms(state.angularMaterial, state.angularRho);
            joint->lambda[2] = terms.scale * joint->lambda[2] + terms.stiffness * angle;
#else
            joint->lambdaLin = materialForce(jointLinear(joint) - joint->C0Lin * alpha, joint->lambdaLin, state.linearMaterial, state.linearRho);
            joint->lambdaAng = materialForce(jointRotation(joint, state) - joint->C0Ang * alpha, joint->lambdaAng, state.angularMaterial, state.angularRho);
#endif
        }
#ifdef DEMO_2D
        else
        {
            force->computeConstraint(0);
            for (int row = 0; row < force->rows(); ++row)
            {
                float material = isinf(force->stiffness[row]) ? experiment->newton.jointAngularStiffness : force->stiffness[row];
                auto terms = materialTerms(material, force->penalty[row]);
                force->lambda[row] = clamp(terms.scale * force->lambda[row] + terms.stiffness * force->C[row], force->fmin[row], force->fmax[row]);
            }
        }
#endif
    }
}

void primal(float alpha)
{
    Experiment* experiment = researchStep->experiment;
    float inverseStepSquared = 1 / (experiment->solver.dt * experiment->solver.dt);
    for (int index = 0; index < (int)experiment->bodies.size(); ++index)
    {
        Rigid* body = experiment->bodies[index];
        if (!researchBodySolves(body)) continue;
        ++experiment->islands.bodyUpdates;
#ifdef DEMO_2D
        float3x3 hessian = diagonal(body->mass, body->mass, body->moment) * inverseStepSquared;
        float3 gradient = hessian * (body->position - body->inertial);
        gradient.z = hessian[2][2] * newtonPlanarDifference(body->position.z, body->inertial.z);
        for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
        {
            if (auto* manifold = dynamic_cast<Manifold*>(force)) addContact(manifold, body, alpha, hessian, gradient);
            else if (auto* joint = dynamic_cast<Joint*>(force)) addJoint(joint, body, alpha, hessian, gradient);
            else
            {
                force->computeConstraint(0); force->computeDerivatives(body);
                for (int row = 0; row < force->rows(); ++row)
                {
                    float material = isinf(force->stiffness[row]) ? experiment->newton.jointAngularStiffness : force->stiffness[row];
                    auto terms = materialTerms(material, force->penalty[row]);
                    float value = clamp(terms.stiffness * force->C[row] + terms.scale * force->lambda[row], force->fmin[row], force->fmax[row]);
                    gradient += force->J[row] * value;
                    hessian += outer(force->J[row], force->J[row]) * terms.stiffness;
                }
            }
        }
        researchPrimal(body, hessian, gradient);
        BodySolve factor(hessian);
        float3 change = -factor.solve(gradient);
        if (researchSupports()) researchSupport(body, factor, change);
        body->position.x += change.x; body->position.y += change.y;
        body->position.z = newtonPlanarAdvance(body->position.z, change.z);
        researchBodyChanged(body);
#else
        auto inertia = newtonAngularInertia(body);
        float3x3 linear = diagonal(body->mass, body->mass, body->mass) * inverseStepSquared;
        float3x3 angular{}, crossBlock{};
        float3 gradientLin = linear * (body->positionLin - body->inertialLin), gradientAng{};
        for (int row = 0; row < 3; ++row)
        {
            gradientAng[row] = (float)inertia.gradient[row] * inverseStepSquared;
            for (int column = 0; column < 3; ++column) angular[row][column] = (float)inertia.hessian[row * 3 + column] * inverseStepSquared;
        }
        for (Force* force = body->forces; force; force = force->bodyA == body ? force->nextA : force->nextB)
        {
            if (auto* manifold = dynamic_cast<Manifold*>(force)) addContact(manifold, body, alpha, linear, angular, crossBlock, gradientLin, gradientAng);
            else if (auto* joint = dynamic_cast<Joint*>(force)) addJoint(joint, body, alpha, linear, angular, crossBlock, gradientLin, gradientAng);
            else force->updatePrimal(body, 0, linear, angular, crossBlock, gradientLin, gradientAng);
        }
        researchPrimal(index, linear, angular, crossBlock, gradientLin, gradientAng);
        BodySolve factor(linear, angular, crossBlock);
        float3 translation, rotation;
        factor.solve(-gradientLin, -gradientAng, translation, rotation);
        if (researchSupports()) researchSupport(index, factor, translation, rotation);
        body->positionLin += translation;
        body->positionAng = researchAdvanceRotation(body->positionAng, rotation);
        researchBodyChanged(index);
#endif
    }
}
}

#ifdef DEMO_2D
float newtonPlanarDifference(float current, float previous)
{
    float difference = current - previous;
    return atan2f(sinf(difference), cosf(difference));
}

float newtonPlanarAdvance(float angle, float change)
{
    // Planar restriction of normalize(q + 0.5 * quat(change,0) * q).
    return angle + 2 * atanf(change * .5f);
}
#endif

float3 newtonContactForce(Experiment* experiment, Manifold* manifold, int point)
{
    auto equation = contactEquation<false>(experiment, manifold, point, 0);
    auto* stored = findContactReactions(manifold);
    if (stored != nullptr) equation.force += (*stored)[point];
    return matrixTranspose(contactBasis(manifold, point)) * equation.force;
}

float3 newtonInverseInertia(Rigid* body, float3 value)
{
    if (!body || body->mass <= 0) return {0, 0, 0};
#ifdef DEMO_2D
    return {0, 0, value.z / body->moment};
#else
    float3 local = rotate(conjugate(body->positionAng), value);
    return rotate(body->positionAng, {local.x / body->moment.x, local.y / body->moment.y, local.z / body->moment.z});
#endif
}

// Newton _contact_body_conditioning_scale / _contact_conditioning_scale.
// Contact-normal rows use this automatic conditioning metric.
float newtonPairConditioning(Rigid* bodyA, float3 offsetA, Rigid* bodyB, float3 offsetB, float3 direction)
{
    Experiment* experiment = researchStep->experiment;
    float support[2]{};
    Rigid* bodies[2]{bodyA, bodyB};
    float3 offsets[2]{offsetA, offsetB};
    float dt = experiment->solver.dt;
    for (int endpoint = 0; endpoint < 2; ++endpoint)
    {
        Rigid* body = bodies[endpoint];
        if (inverseMass(body) == 0) continue;
        float3 torque = vectorCross(offsets[endpoint], direction);
        float mobility = 1 / body->mass + dot(torque, newtonInverseInertia(body, torque));
        support[endpoint] = 1 / (dt * dt * mobility) + experiment->newton.bodyJointStiffness[experiment->index(body)];
    }
    if (support[0] == 0 || support[1] == 0) return max(support[0], support[1]);
    float low = min(support[0], support[1]), high = max(support[0], support[1]);
    return low / (1 + low / high);
}

#ifndef DEMO_2D
float3 newtonRotationDifference(quat current, quat previous)
{
    return rotationVector(current * inverse(previous));
}

float3 newtonAngularPrediction(Rigid* body)
{
    if (body->mass == 0) return body->velocityAng;
    float3 local = rotate(conjugate(body->positionAng), body->velocityAng);
    float3 torque = -cross(local, {local.x * body->moment.x, local.y * body->moment.y, local.z * body->moment.z});
    return rotate(body->positionAng, local + float3{torque.x / body->moment.x, torque.y / body->moment.y, torque.z / body->moment.z} * body->solver->dt);
}

AngularInertia newtonAngularInertia(Rigid* body)
{
    float3 theta = rotationVector(inverse(body->positionAng) * body->inertialAng);
    float3 gradient = -rotate(body->positionAng, {body->moment.x * theta.x, body->moment.y * theta.y, body->moment.z * theta.z});
    // Newton world inertia R I R^T: isotropic inertia is rotation invariant.
    float3x3 matrix = diagonal(body->moment.x, body->moment.y, body->moment.z);
    if (body->moment.x != body->moment.y || body->moment.x != body->moment.z)
    {
        float3x3 rotation = rotationMatrix(body->positionAng);
        matrix = rotation * matrix * transpose(rotation);
    }
    AngularInertia result;
    for (int row = 0; row < 3; ++row)
    {
        result.gradient[row] = gradient[row];
        for (int column = 0; column < 3; ++column) result.hessian[row * 3 + column] = matrix[row][column];
    }
    return result;
}
#endif

#ifndef DEMO_2D
// Persistent local witnesses follow Bullet's contact-cache approach.
// The project reuses clipping only within the common 10 um pose budget.
static bool reuseContactGeometry(Manifold* manifold)
{
    auto& data = *static_cast<ContactManifold*>(manifold);
    auto* experiment = researchStep->experiment;
    if (experiment->newton.compliant || !researchNewtonContact(manifold)
        || !data.geometryValid || manifold->numContacts == 0) return false;
    Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
    float motionA = length(a->positionLin - data.geometryCenterA)
        + 2 * length(a->positionAng - data.geometryRotationA) * a->radius;
    float motionB = length(b->positionLin - data.geometryCenterB)
        + 2 * length(b->positionAng - data.geometryRotationB) * b->radius;
    if (max(motionA, motionB) > contactGeometryAccuracy) return false;
    RESEARCH_COUNT(GeometryReuses, 1);
    const auto& offsets = readContactOffsets(manifold);
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto& contact = manifold->contacts[point];
        float3 raw = manifold->basis * (a->positionLin - b->positionLin + offsets.offsetsA[point] - offsets.offsetsB[point]);
        data.frames[point] = raw;
        data.matches[point] = {(a->positionLin + offsets.offsetsA[point] + b->positionLin + offsets.offsetsB[point]) * .5f, manifold->basis[0]};
        contact.C0 = raw;
        contact.C0.x = min(contact.C0.x, 0.0f);
        if (contact.C0.x < -.001f) contact.C0.x *= clamp((.005f + contact.C0.x) / .004f, 0.0f, 1.0f);
        contact.lambda = contact.lambda * (manifold->solver->gamma * rigidContactAlpha);
        contact.lambda.x = min(0.0f, contact.lambda.x);
        float bound = -contact.lambda.x * manifold->friction;
        float tangent = length(float2{contact.lambda.y, contact.lambda.z});
        if (tangent > bound) {contact.lambda.y *= bound / tangent; contact.lambda.z *= bound / tangent;}
        contact.penalty = contact.penalty * manifold->solver->gamma;
        contact.stick = false;
        researchInitializeContact(manifold, point);
        researchTransferContactPressure(manifold, point);
    }
    return true;
}
#endif

// Latest feature matching: fresh geometric/surface anchors at zero margin,
// world-space multiplier transport, gamma retention and automatic rho refresh.
bool initializeNewtonContact(Manifold* manifold, bool refresh)
{
    RESEARCH_TIME(phaseTime, ContactGeometry);
#ifndef DEMO_2D
    if (!refresh && reuseContactGeometry(manifold)) return true;
    static_cast<ContactManifold*>(manifold)->offsetsValid = false;
#endif
    Experiment* experiment = researchStep->experiment;
    constexpr int capacity = sizeof(manifold->contacts) / sizeof(manifold->contacts[0]);
    std::array<float3, capacity> worldForces{};
    std::array<float3, capacity> worldExtras{};
    std::array<float3, capacity> oldPenalties{};
    std::array<float3, capacity> oldPointsA{}, oldPointsB{};
    auto previousMatches = static_cast<ContactManifold*>(manifold)->matches;
    auto* changes = findContactReactions(manifold);
    int oldCount = manifold->numContacts;
    float retention = manifold->solver->gamma;
#ifdef DEMO_2D
    if (!experiment->newton.compliant) retention *= rigidContactAlpha;
    float oldPenalty = 0;
    for (int i = 0; i < oldCount; ++i) oldPenalty = max(oldPenalty, manifold->penalty[i * 2]);
#else
    if (!experiment->newton.compliant) retention *= researchNewtonContact(manifold) ? rigidContactAlpha : manifold->solver->alpha;
    float3 oldNormal = manifold->basis[0];
    float3 patchPenalty{0, 0, 0};
#endif
    for (int point = 0; point < oldCount; ++point)
    {
        oldPointsA[point] = localContactPoint(manifold->contacts[point], true);
        oldPointsB[point] = localContactPoint(manifold->contacts[point], false);
        worldForces[point] = matrixTranspose(contactBasis(manifold, point)) * contactMultiplier(manifold, point);
        oldPenalties[point] = contactRho(manifold, point);
#ifndef DEMO_2D
        for (int axis = 0; axis < 3; ++axis) patchPenalty[axis] = max(patchPenalty[axis], oldPenalties[point][axis]);
#endif
        if (changes != nullptr) worldExtras[point] = matrixTranspose(contactBasis(manifold, point)) * (*changes)[point];
    }
    if (changes != nullptr) (*changes).fill(float3{0, 0, 0});
    researchStep->contactQueryDistance = 2 * newtonShapeGap;
#ifndef DEMO_2D
    if (!researchNewtonContact(manifold)) researchStep->contactQueryDistance = .02f;
#endif
#ifdef DEMO_2D
    auto& detected = static_cast<ContactManifold*>(manifold)->detectedCount;
    if (detected >= 0)
        manifold->numContacts = researchReadContacts(manifold, manifold->contacts);
    else
    {
        RESEARCH_COUNT(NarrowQueries, 1);
        manifold->numContacts = refresh ? Manifold::collide(manifold->bodyA, manifold->bodyB, manifold->contacts)
            : readPredictedContacts(manifold->bodyA, manifold->bodyB, manifold->contacts);
    }
#else
    auto& detected = static_cast<ContactManifold*>(manifold)->detectedCount;
    if (detected >= 0)
    {
        manifold->numContacts = detected;
        detected = -1;
    }
    else
    {
        RESEARCH_COUNT(NarrowQueries, 1);
        manifold->numContacts = refresh ? Manifold::collide(manifold->bodyA, manifold->bodyB, manifold->contacts, manifold->basis)
            : readPredictedContacts(manifold->bodyA, manifold->bodyB, manifold->contacts, manifold->basis);
    }
#endif
#ifndef DEMO_2D
    if (!experiment->newton.compliant) manifold->numContacts = researchContactPatch(manifold, manifold->contacts, manifold->numContacts);
#endif
    researchStep->contactQueryDistance = 0;
    if (manifold->numContacts == 0)
    {
        if (oldCount > 0) ++experiment->contactVersion;
        return false;
    }
    std::array<float3, capacity> offsetsA, offsetsB, midpoints;
    // Newton sim/collide.py::write_contact constructs geometric points from
    // one midpoint and +/- half the signed normal distance. The box clipper's
    // fallback support vertices can have a large tangential separation.
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto& contact = manifold->contacts[point];
        Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
        float3 pA = center(a) + offset(a, localContactPoint(contact, true));
        float3 pB = center(b) + offset(b, localContactPoint(contact, false));
        float3 normal = contactBasis(manifold, point)[0];
        float3 halfNormalGap = normal * (dot(pA - pB, normal) * .5f);
        float3 midpoint = (pA + pB) * .5f;
#ifdef DEMO_2D
        contact.rA = rotate(-a->position.z, (midpoint + halfNormalGap - center(a)).xy());
        contact.rB = rotate(-b->position.z, (midpoint - halfNormalGap - center(b)).xy());
#else
        contact.rA = rotate(conjugate(a->positionAng), midpoint + halfNormalGap - center(a));
        contact.rB = rotate(conjugate(b->positionAng), midpoint - halfNormalGap - center(b));
#endif
        offsetsA[point] = offset(a, localContactPoint(contact, true));
        offsetsB[point] = offset(b, localContactPoint(contact, false));
        midpoints[point] = (center(a) + offsetsA[point] + center(b) + offsetsB[point]) * .5f;
    }
    auto& frames = static_cast<ContactManifold*>(manifold)->frames;
    auto& matches = static_cast<ContactManifold*>(manifold)->matches;
    std::array<int, capacity> oldMatches;
    std::array<float, capacity> distances;
    oldMatches.fill(-1);
    float matchDistance = .0005f;
    distances.fill(matchDistance * matchDistance);
    // Newton geometry/contact_match.py: nearest world midpoint within the
    // shape pair, normal gate, then one-to-one claims. Clip slot IDs rotate.
    if (refresh || !experiment->newton.compliant || experiment->newton.contactHistory || changes != nullptr)
    {
        for (int point = 0; point < manifold->numContacts; ++point)
        {
            const auto& contact = manifold->contacts[point];
            float3 midpoint = midpoints[point];
            float3 normal = contactBasis(manifold, point)[0];
            for (int old = 0; old < oldCount; ++old)
            {
                float distance = lengthSq(midpoint - previousMatches[old].midpoint);
#ifdef DEMO_2D
                // Project planar persistence uses body-local witnesses, as in
                // Bullet's persistent-manifold cache, with symmetric endpoints.
                if (!experiment->newton.compliant)
                    distance = min(lengthSq(localContactPoint(contact, true) - oldPointsA[old]),
                        lengthSq(localContactPoint(contact, false) - oldPointsB[old]));
#endif
                if (distance <= distances[point] && dot(normal, previousMatches[old].normal) >= .995f)
                { oldMatches[point] = old; distances[point] = distance; }
            }
        }
        for (int point = 0; point < manifold->numContacts; ++point)
            for (int other = 0; other < point; ++other)
                if (oldMatches[point] >= 0 && oldMatches[point] == oldMatches[other])
                {
                    if (distances[point] < distances[other]) oldMatches[other] = -1;
                    else oldMatches[point] = -1;
                }
    }
#ifndef DEMO_2D
    if (!experiment->newton.compliant && !experiment->researchExtensions)
    {
        bool changed = oldCount != manifold->numContacts;
        for (int point = 0; !changed && point < manifold->numContacts; ++point)
            changed = oldMatches[point] != point
                || lengthSq(localContactPoint(manifold->contacts[point], true) - oldPointsA[point]) > contactGeometryAccuracy * contactGeometryAccuracy
                || lengthSq(localContactPoint(manifold->contacts[point], false) - oldPointsB[point]) > contactGeometryAccuracy * contactGeometryAccuracy
                || length(manifold->basis[0] - oldNormal) * max(manifold->bodyA->radius, manifold->bodyB->radius) > contactGeometryAccuracy;
        if (changed) ++experiment->contactGeometryVersion;
    }
#endif
    manifold->friction = sqrtf(manifold->bodyA->friction * manifold->bodyB->friction);
#ifdef DEMO_2D
    Rigid* firstBody = manifold->bodyA; Rigid* secondBody = manifold->bodyB;
    float initialGap = experiment->newton.compliant ? -INFINITY : readBoxSeparation(firstBody, secondBody,
        refresh ? firstBody->initial : firstBody->position, refresh ? secondBody->initial : secondBody->position);
#else
    Rigid* firstBody = manifold->bodyA;
    Rigid* secondBody = manifold->bodyB;
    float initialGap = experiment->newton.compliant ? -INFINITY : readBoxSeparation(firstBody, secondBody,
        refresh ? firstBody->initialLin : firstBody->positionLin, refresh ? firstBody->initialAng : firstBody->positionAng,
        refresh ? secondBody->initialLin : secondBody->positionLin, refresh ? secondBody->initialAng : secondBody->positionAng);
#endif
    for (int point = 0; point < manifold->numContacts; ++point)
    {
        auto& contact = manifold->contacts[point];
        float3x3 basis = contactBasis(manifold, point);
        float3 lambda{0, 0, 0};
        int old = oldMatches[point];
        if (old >= 0)
        {
            if (refresh || !experiment->newton.compliant || experiment->newton.contactHistory)
                lambda = basis * worldForces[old] * (refresh ? 1.0f : retention);
            if (changes != nullptr) (*changes)[point] = basis * worldExtras[old];
        }
        lambda.x = min(0.0f, lambda.x);
        float bound = -lambda.x * manifold->friction;
        float tangent = length(float2{lambda.y, lambda.z});
        if (tangent > bound) { lambda.y *= bound / tangent; lambda.z *= bound / tangent; }
        setContactMultiplier(manifold, point, lambda);
        float3 gap = basis * (center(manifold->bodyA) - center(manifold->bodyB)
            + offsetsA[point] - offsetsB[point]);
        frames[point] = gap;
        matches[point] = {midpoints[point], basis[0]};
        if (refresh)
        {
            Rigid* a = manifold->bodyA; Rigid* b = manifold->bodyB;
#ifdef DEMO_2D
            float2 rA = rotate(a->initial.z, contact.rA), rB = rotate(b->initial.z, contact.rB);
            gap = basis * float3{a->initial.x - b->initial.x + rA.x - rB.x, a->initial.y - b->initial.y + rA.y - rB.y, 0};
#else
            gap = basis * (a->initialLin - b->initialLin + rotate(a->initialAng, contact.rA) - rotate(b->initialAng, contact.rB));
#endif
        }
#ifdef DEMO_2D
        contact.C0 = {gap.x, gap.y};
        if (!experiment->newton.compliant) contact.C0.x = min(0.0f, max(contact.C0.x, initialGap));
        float3 a = offsetsA[point], b = offsetsB[point];
        contact.JAn = {basis[0].x, basis[0].y, vectorCross(a, basis[0]).z};
        contact.JBn = {-basis[0].x, -basis[0].y, -vectorCross(b, basis[0]).z};
        contact.JAt = {basis[1].x, basis[1].y, vectorCross(a, basis[1]).z};
        contact.JBt = {-basis[1].x, -basis[1].y, -vectorCross(b, basis[1]).z};
#else
        contact.C0 = gap;
        if (!experiment->newton.compliant) contact.C0.x = researchNewtonContact(manifold) && !refresh ? (initialGap == 0 ? 0 : min(contact.C0.x, 0.0f)) : min(0.0f, max(contact.C0.x, initialGap));
#endif
#ifndef DEMO_2D
        // PhysX_AVBD b46e2ee avbd_solver.cpp::computeC0 depth-adaptive bias.
        // Copyright (c) 2008-2025 NVIDIA Corporation, BSD-3-Clause.
        // License: References/PhysX-AVBD-LICENSE.md.
        // Project contact tolerances: fade stabilization from 1 mm to 5 mm.
        if (!experiment->newton.compliant && researchNewtonContact(manifold) && contact.C0.x < -.001f)
            contact.C0.x *= clamp((.005f + contact.C0.x) / .004f, 0.0f, 1.0f);
#endif
        contact.stick = false;
        if (experiment->newton.compliant) initializeContactMetric(manifold, point);
#ifdef DEMO_2D
        else
        {
            manifold->penalty[point * 2] = oldPenalty * (refresh ? 1.0f : manifold->solver->gamma);
            researchInitializeContact(manifold, point);
            researchTransferContactPressure(manifold, point);
        }
#else
        else
        {
            contact.penalty = old >= 0 ? oldPenalties[old] * (refresh ? 1.0f : manifold->solver->gamma) : float3{0, 0, 0};
            if (!researchNewtonContact(manifold) && oldCount && dot(oldNormal, manifold->basis[0]) >= .995f)
                for (int axis = 0; axis < 3; ++axis) contact.penalty[axis] = max(contact.penalty[axis], patchPenalty[axis] * (refresh ? 1.0f : manifold->solver->gamma));
            researchInitializeContact(manifold, point);
            researchTransferContactPressure(manifold, point);
        }
#endif
    }
#ifndef DEMO_2D
    auto& geometry = *static_cast<ContactManifold*>(manifold);
    geometry.geometryValid = manifold->numContacts > 0;
    geometry.geometryCenterA = manifold->bodyA->positionLin;
    geometry.geometryCenterB = manifold->bodyB->positionLin;
    geometry.geometryRotationA = manifold->bodyA->positionAng;
    geometry.geometryRotationB = manifold->bodyB->positionAng;
#endif
    if ((oldCount > 0) != (manifold->numContacts > 0)) ++experiment->contactVersion;
    return manifold->numContacts > 0;
}

void stepNewtonRigid()
{
    Experiment* experiment = researchStep->experiment;
    auto& solver = experiment->solver;
    initializeJoints(experiment);
    experiment->newton.collisionPasses = 1;
    researchDetectCollisions();
    for (Force* force = solver.forces; force;)
    {
        Force* next = force->next;
        if (auto* manifold = dynamic_cast<Manifold*>(force))
        {
            if (!initializeNewtonContact(manifold)) { researchRemoveForce(force); delete force; }
        }
        else if (!dynamic_cast<Joint*>(force))
        {
            force->initialize();
#ifdef DEMO_2D
            float mobility[MAX_ROWS]{};
            for (Rigid* body : {force->bodyA, force->bodyB})
            {
                if (inverseMass(body) == 0) continue;
                force->computeDerivatives(body);
                for (int row = 0; row < force->rows(); ++row)
                    mobility[row] += (force->J[row].x * force->J[row].x + force->J[row].y * force->J[row].y) / body->mass
                        + force->J[row].z * force->J[row].z / body->moment;
            }
            for (int row = 0; row < force->rows(); ++row)
            {
                float material = isinf(force->stiffness[row]) ? experiment->newton.jointAngularStiffness : force->stiffness[row];
                force->penalty[row] = material > 0 ? max(9 * material, 1 / (solver.dt * solver.dt * mobility[row])) : 0;
                force->lambda[row] *= solver.gamma;
            }
#endif
        }
        force = next;
    }
    researchBuildIslands();
    for (Rigid* body : experiment->bodies)
    {
        if (body->mass > 0 && !researchBodyAwake(body)) continue;
#ifdef DEMO_2D
        body->initial = body->position;
        body->inertial = body->position + body->velocity * solver.dt;
        body->inertial.z = newtonPlanarAdvance(body->position.z, body->velocity.z * solver.dt);
        if (body->mass > 0) body->inertial.y += solver.gravity * solver.dt * solver.dt;
        if (body->mass > 0)
        {
            float acceleration = (body->velocity.y - body->prevVelocity.y) / solver.dt;
            float weight = clamp(acceleration / solver.gravity, 0.0f, 1.0f);
            if (!isfinite(weight)) weight = 0;
            body->position = body->inertial;
            body->position.y -= solver.gravity * solver.dt * solver.dt * (1 - weight);
        }
#else
        body->initialLin = body->positionLin; body->initialAng = body->positionAng;
        body->inertialLin = body->positionLin + body->velocityLin * solver.dt;
        float3 omega = body->mass > 0 ? newtonAngularPrediction(body) : body->velocityAng;
        if (body->mass > 0)
        {
            body->inertialLin.z += solver.gravity * solver.dt * solver.dt;
        }
        body->inertialAng = researchAdvanceRotation(body->positionAng, omega * solver.dt);
        if (body->mass > 0)
        {
            float acceleration = (body->velocityLin.z - body->prevVelocityLin.z) / solver.dt;
            float weight = clamp(acceleration / solver.gravity, 0.0f, 1.0f);
            if (!isfinite(weight)) weight = 0;
            body->positionLin = body->inertialLin;
            body->positionLin.z -= solver.gravity * solver.dt * solver.dt * (1 - weight);
            body->positionAng = body->inertialAng;
        }
#endif
    }
    bool stabilize = researchPostStabilize();
    float alpha = stabilize ? 1.0f : 0.0f;
    for (int iteration = 0; iteration < solver.iterations; ++iteration)
    {
        // SolverVBD._rigid_collision_is_due / _mid_step_detection:
        // retain in-flight multipliers; detection consumes the current iterate.
        int interval = experiment->newton.contactDetectionInterval;
        if (interval > 0 && (iteration + 1) % interval == 0)
        {
            researchDetectCollisions();
            ++experiment->newton.collisionPasses;
            for (Force* force = solver.forces; force;)
            {
                Force* next = force->next;
                if (auto* manifold = dynamic_cast<Manifold*>(force))
                    if (!initializeNewtonContact(manifold, true)) { researchRemoveForce(force); delete force; }
                force = next;
            }
            researchBuildIslands();
            if (iteration > 0) researchInitializeJointBounds(experiment);
            if (experiment->longRange.enabled)
            {
                clearSupportRanges(experiment);
                experiment->supportGraph.manifoldCount = -1;
            }
        }
        researchBeforeIteration(iteration);
        primal(alpha);
        updateDuals(alpha);
        researchUpdateLongRangeForces();
        // Keep collision candidates current after coupled body/LRC motion.
        // The existing project's incremental broadphase appends new pairs;
        // Newton initializes their geometry and ALM state for the next sweep.
        if (researchAddMovedContacts() && experiment->longRange.enabled) researchUpdateSupports();
    }
    researchCommitVelocities();
    if (stabilize)
    {
        researchStep->currentIteration = solver.iterations;
        ++experiment->lastStabilizationIterations;
        primal(0);
    }
}

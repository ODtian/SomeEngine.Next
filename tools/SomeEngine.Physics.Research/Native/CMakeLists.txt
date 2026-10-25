cmake_minimum_required(VERSION 3.20)
project(AvbdResearch LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 17)
option(AVBD_PROFILE "Measure main-thread solver phases and scene work counts" OFF)
foreach(dimension IN ITEMS 2d 3d)
    set(reference "${CMAKE_CURRENT_SOURCE_DIR}/../References/${dimension}/source")
    file(GLOB sources "${reference}/*.cpp")
    list(FILTER sources EXCLUDE REGEX "/(main|solver)\\.cpp$")
    list(FILTER sources EXCLUDE REGEX "/(manifold|collide)\\.cpp$")
    if(dimension STREQUAL "3d")
        list(FILTER sources EXCLUDE REGEX "/joint\\.cpp$")
        file(READ "${reference}/joint.cpp" joint)
        string(REPLACE "#include \"solver.h\"" "#include \"solver.h\"\n#include \"ResearchStep.h\"" joint "${joint}")
        string(REPLACE "bool Joint::initialize()\n{" "bool Joint::initialize()\n{\n    researchInitializeJoint(this);" joint "${joint}")
        string(REPLACE "((bodyA ? bodyA->positionAng : quat{ 0, 0, 0, 1 }) - bodyB->positionAng)" "researchJointRotationError(this)" joint "${joint}")
        file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/joint-${dimension}.cpp" CONTENT "${joint}" @ONLY)
        list(APPEND sources "${CMAKE_CURRENT_BINARY_DIR}/joint-${dimension}.cpp")
    endif()
    file(READ "${reference}/collide.cpp" collide)
    string(REPLACE "#include \"solver.h\"" "#include \"solver.h\"\n#include \"ResearchStep.h\"" collide "${collide}")
    file(READ "${reference}/manifold.cpp" manifold)
    string(REPLACE "#include \"solver.h\"" "#include \"solver.h\"\n#include \"ResearchStep.h\"\n#include \"BridgeState.h\"" manifold "${manifold}")
    file(READ "${reference}/solver.cpp" solver)
    string(REPLACE "#include \"solver.h\"" "#include \"solver.h\"\n#include \"ResearchStep.h\"" solver "${solver}")
    string(REPLACE "// Primal update" "researchBeforeIteration(it);\n        // Primal update" solver "${solver}")
    string(REPLACE "delete force;" "researchRemoveForce(force);\n            delete force;" solver "${solver}")
    string(REPLACE "float r = bodyA->radius + bodyB->radius;" "float r = bodyA->radius + bodyB->radius + readBodyTravel(bodyA) + readBodyTravel(bodyB);" solver "${solver}")
    string(REPLACE "!bodyA->constrainedTo(bodyB)" "!researchCollisionExcluded(bodyA, bodyB) && !researchContactExists(bodyA, bodyB)" solver "${solver}")
    if(dimension STREQUAL "2d")
        string(REPLACE "researchBeforeIteration(it);\n        // Primal update" "// Primal update" solver "${solver}")
        string(REPLACE "    int totalIterations = iterations + (postStabilize ? 1 : 0);\n\n" "" solver "${solver}")
        string(REPLACE "for (int it = 0; it < totalIterations; it++)\n    {" "for (int it = 0; it < iterations + (postStabilize ? researchRecoveryLimit() : 0); it++)\n    {\n        if (it == 0 || it == iterations || researchStep->experiment->researchExtensions) researchRefreshContacts();\n        researchBeforeIteration(it);\n        if (it == iterations && !researchInitializeStabilization()) break;" solver "${solver}")
        set(originalVelocity [=[        // If we are are the final iteration before post stabilization, compute velocities (BDF1)
        if (it == iterations - 1)
        {
            for (Rigid* body = bodies; body != 0; body = body->next)
            {
                body->prevVelocity = body->velocity;
                if (body->mass > 0)
                    body->velocity = (body->position - body->initial) / dt;
            }
        }
]=])
        string(REPLACE "${originalVelocity}" "" solver "${solver}")
        string(REPLACE "float2 hA = bodyA->size * 0.5f;"
        [=[float dt = bodyA->solver->dt;
    float relativeGravity = ((bodyA->mass > 0) != (bodyB->mass > 0)) ? fabsf(bodyA->solver->gravity) : 0.0f;
    float contactDistance = researchContactDistance() + dt * (length(bodyA->velocity.xy() - bodyB->velocity.xy())
        + fabsf(bodyA->velocity.z) * bodyA->radius + fabsf(bodyB->velocity.z) * bodyB->radius
        + relativeGravity * dt);
    float2 hA = bodyA->size * 0.5f;]=] collide "${collide}")
        string(REPLACE "faceA.x > 0.0f || faceA.y > 0.0f" "faceA.x > contactDistance || faceA.y > contactDistance" collide "${collide}")
        string(REPLACE "faceB.x > 0.0f || faceB.y > 0.0f" "faceB.x > contactDistance || faceB.y > contactDistance" collide "${collide}")
        string(REPLACE "if (separation <= 0)" "if (separation <= contactDistance)" collide "${collide}")
        string(REPLACE " + float2{ COLLISION_MARGIN, 0 }" "" manifold "${manifold}")
        string(REPLACE "collide(bodyA, bodyB, contacts)" "readPredictedContacts(bodyA, bodyB, contacts)" manifold "${manifold}")
        string(REPLACE "contacts[i].C0 = basis * (bodyA->position.xy() + rAW - bodyB->position.xy() - rBW);"
            "contacts[i].C0 = basis * (bodyA->position.xy() + rAW - bodyB->position.xy() - rBW);\n        researchInitializeContact(this, i);" manifold "${manifold}")
        string(REPLACE "        C[i * 2 + 0] = contacts[i].C0.x * (1 - alpha) + dot(contacts[i].JAn, dpA) + dot(contacts[i].JBn, dpB);\n        C[i * 2 + 1] = contacts[i].C0.y * (1 - alpha) + dot(contacts[i].JAt, dpA) + dot(contacts[i].JBt, dpB);"
        [=[        float2 normal = contacts[i].normal, tangent{normal.y, -normal.x};
        float2 rA = researchRotateContactPoint(bodyA->position.z, contacts[i].rA);
        float2 rB = researchRotateContactPoint(bodyB->position.z, contacts[i].rB);
        float2 gap = bodyA->position.xy() - bodyB->position.xy() + rA - rB;
        contacts[i].JAn = {normal.x, normal.y, cross(rA, normal)};
        contacts[i].JBn = {-normal.x, -normal.y, -cross(rB, normal)};
        contacts[i].JAt = {tangent.x, tangent.y, cross(rA, tangent)};
        contacts[i].JBt = {-tangent.x, -tangent.y, -cross(rB, tangent)};
        C[i * 2 + 0] = dot(normal, gap) - (researchStep->experiment->researchExtensions ? 0 : contacts[i].C0.x * alpha);
        C[i * 2 + 1] = dot(tangent, gap) - contacts[i].C0.y * (researchStep->experiment->researchExtensions ? 1.0f : alpha);]=] manifold "${manifold}")
        string(REPLACE "        float3 dpA = bodyA->position - bodyA->initial;\n        float3 dpB = bodyB->position - bodyB->initial;" "" manifold "${manifold}")
        string(REPLACE "rotate(bodyA->position.z, contacts[i].rA)" "researchRotateContactPoint(bodyA->position.z, contacts[i].rA)" manifold "${manifold}")
        string(REPLACE "rotate(bodyB->position.z, contacts[i].rB)" "researchRotateContactPoint(bodyB->position.z, contacts[i].rB)" manifold "${manifold}")
        string(REPLACE "J[i * 2 + 1] = contacts[i].JAt;"
            "J[i * 2 + 1] = contacts[i].JAt;\n            H[i * 2 + 0] = diagonal(0, 0, contacts[i].JAt.z);\n            H[i * 2 + 1] = diagonal(0, 0, -contacts[i].JAn.z);" manifold "${manifold}")
        string(REPLACE "J[i * 2 + 1] = contacts[i].JBt;"
            "J[i * 2 + 1] = contacts[i].JBt;\n            H[i * 2 + 0] = diagonal(0, 0, contacts[i].JBt.z);\n            H[i * 2 + 1] = diagonal(0, 0, -contacts[i].JBn.z);" manifold "${manifold}")
        string(REPLACE "body->position -= solve(lhs, rhs);" "researchPrimal(body, lhs, rhs);\n            float3 change;\n            if (researchSupports())\n            {\n                BodySolve factor(lhs);\n                change = -factor.solve(rhs);\n                researchSupport(body, factor, change);\n            }\n            else change = -solve(lhs, rhs);\n            body->position += change;\n            if (researchStep->bodyCount >= 128) researchStep->maximumBodyChangeSquared = max(researchStep->maximumBodyChangeSquared, 2 * (lengthSq(change.xy()) + body->radius * body->radius * change.z * change.z));\n            researchBodyChanged(body);" solver "${solver}")
        string(REPLACE "\n    }\n}\n\nvoid Solver::draw()" "\n        if (it < iterations)\n        {\n            researchAfterIteration();\n            if (it == iterations - 1) researchCommitVelocities();\n        }\n        else if (!researchMoreStabilization()) break;\n    }\n}\n\nvoid Solver::draw()" solver "${solver}")
        string(REPLACE "float frictionBound = abs(lambda[i * 2 + 0]) * friction;" "float3 extra = researchContactChange(this, i);\n        float normalForce = min(0.0f, penalty[i * 2] * C[i * 2] + lambda[i * 2]);\n        float frictionBound = abs(normalForce + extra.x) * (researchStep->experiment->researchExtensions && researchStep->experiment->lastStabilizationIterations > 0 ? 0.0f : friction);" manifold "${manifold}")
        string(REPLACE "fmax[i * 2 + 1] = frictionBound;" "fmax[i * 2 + 1] = frictionBound - extra.y;" manifold "${manifold}")
        string(REPLACE "fmin[i * 2 + 1] = -frictionBound;" "fmin[i * 2 + 1] = -frictionBound - extra.y;" manifold "${manifold}")
        string(REPLACE "contacts[i].stick = abs(lambda[i * 2 + 1]) < frictionBound"
            "float tangentForce = clamp(penalty[i * 2 + 1] * C[i * 2 + 1] + lambda[i * 2 + 1], fmin[i * 2 + 1], fmax[i * 2 + 1]);\n        contacts[i].stick = abs(tangentForce + extra.y) < frictionBound" manifold "${manifold}")
        string(REPLACE "abs(contacts[i].C0.y) < STICK_THRESH" "abs(C[i * 2 + 1]) < STICK_THRESH" manifold "${manifold}")
    else()
        string(REPLACE "researchBeforeIteration(it);" "researchBeforeIteration(it);\n        if (it == iterations && !researchInitializeStabilization()) break;" solver "${solver}")
        string(REPLACE "body->positionAng + body->velocityAng * dt" "researchAdvanceRotation(body->positionAng, (researchStep->experiment->researchExtensions || researchStructuralBody(body) ? body->velocityAng : newtonAngularPrediction(body)) * dt)" solver "${solver}")
        string(REPLACE "body->positionAng = researchAdvanceRotation(body->positionAng, (researchStep->experiment->researchExtensions || researchStructuralBody(body) ? body->velocityAng : newtonAngularPrediction(body)) * dt);" "body->positionAng = body->inertialAng;" solver "${solver}")
        string(REPLACE "body->positionAng - body->inertialAng" "researchRotationDifference(body->positionAng, body->inertialAng)" solver "${solver}")
        string(REPLACE "bodyA->positionAng - bodyA->initialAng" "researchRotationDifference(bodyA->positionAng, bodyA->initialAng)" manifold "${manifold}")
        string(REPLACE "bodyB->positionAng - bodyB->initialAng" "researchRotationDifference(bodyB->positionAng, bodyB->initialAng)" manifold "${manifold}")
        string(APPEND collide [=[

float readBoxSeparation(Rigid* bodyA, Rigid* bodyB, float3 centerA, quat rotationA, float3 centerB, quat rotationB)
{
    OBB a = makeOBB(bodyA), b = makeOBB(bodyB);
    a.center = centerA; b.center = centerB;
    a.axis[0] = rotate(rotationA, {1, 0, 0}); a.axis[1] = rotate(rotationA, {0, 1, 0}); a.axis[2] = rotate(rotationA, {0, 0, 1});
    b.axis[0] = rotate(rotationB, {1, 0, 0}); b.axis[1] = rotate(rotationB, {0, 1, 0}); b.axis[2] = rotate(rotationB, {0, 0, 1});
    float3 delta = b.center - a.center;
    SatAxis best{};
    best.separation = -FLT_MAX;
    for (int axis = 0; axis < 3; ++axis)
    {
        if (!testAxis(a, b, delta, a.axis[axis], AXIS_FACE_A, axis, -1, best)) return 0;
        if (!testAxis(a, b, delta, b.axis[axis], AXIS_FACE_B, -1, axis, best)) return 0;
    }
    for (int first = 0; first < 3; ++first)
        for (int second = 0; second < 3; ++second)
            if (!testAxis(a, b, delta, cross(a.axis[first], b.axis[second]), AXIS_EDGE, first, second, best)) return 0;
    return min(0.0f, best.separation);
}
int researchClipContactFace(Manifold* manifold, Manifold::Contact* contacts, float3x3& basis)
{
    const auto& first = manifold->contacts[0];
    int type = (first.feature.key >> 24) & 0xFF;
    if (type == AXIS_EDGE) return Manifold::collide(manifold->bodyA, manifold->bodyB, contacts, basis);
    OBB a = makeOBB(manifold->bodyA), b = makeOBB(manifold->bodyB);
    bool referenceIsA = type == AXIS_FACE_A;
    int axis = (first.feature.key >> 16) & 0xFF;
    const OBB& reference = referenceIsA ? a : b;
    float3 local = referenceIsA ? first.rA : first.rB;
    float3 outward = reference.axis[axis] * (local[axis] >= 0 ? 1.0f : -1.0f);
    float3 normalAB = referenceIsA ? outward : -outward;
    basis = orthonormal(-normalAB);
    return buildFaceManifold(manifold->bodyA, manifold->bodyB, a, b,
        referenceIsA, axis, normalAB, contacts);
}
]=])
        string(REPLACE "if (separation > 0.0f)" "if (separation > researchContactDistance())" collide "${collide}")
        string(REPLACE "if (distance > PLANE_EPSILON)" "if (distance > researchContactDistance())" collide "${collide}")
        string(REPLACE " + float3{COLLISION_MARGIN, 0, 0}" "" manifold "${manifold}")
        string(REPLACE "collide(bodyA, bodyB, newContacts, basis)" "readPredictedContacts(bodyA, bodyB, newContacts, basis)" manifold "${manifold}")
        string(REPLACE "contacts[i].C0 = basis * (xA - xB);"
            "contacts[i].C0 = basis * (xA - xB);\n        researchInitializeContact(this, i);" manifold "${manifold}")
        string(REPLACE "float3 C = contacts[i].C0 * (1 - alpha) + jALin * dqALin + jBLin * dqBLin + jAAng * dqAAng + jBAng * dqBAng;"
            "float3 C = basis * (bodyA->positionLin + rAWorld - bodyB->positionLin - rBWorld)\n            - float3{researchStep->experiment->researchExtensions ? 0 : researchContactNormalStart(contacts[i].C0.x), contacts[i].C0.y, contacts[i].C0.z} * (researchStep->experiment->researchExtensions ? 1.0f : alpha);" manifold "${manifold}")
        string(REPLACE "for (int it = 0; it < iterations; it++)" "for (int it = 0; it < iterations + researchRecoveryLimit(); it++)" solver "${solver}")
        string(REPLACE "for (Rigid *body = bodies; body != 0; body = body->next)\n        {\n            // Skip"
            "for (int bodyIndex = 0; bodyIndex < researchStep->bodyCount; ++bodyIndex)\n        {\n            int index = it > iterations && ((it - iterations) % 2) ? bodyIndex : researchStep->bodyCount - 1 - bodyIndex;\n            Rigid* body = researchStep->bodies[index];\n            // Skip" solver "${solver}")
        string(REPLACE "force->updatePrimal(body, alpha," "force->updatePrimal(body, it < iterations ? alpha : 0.0f," solver "${solver}")
        string(REPLACE "for (Force *force = forces; force != 0; force = force->next)" "if (it < iterations)\n        for (Force *force = forces; force != 0; force = force->next)" solver "${solver}")
        string(REPLACE "float3 dxLin, dxAng;" "researchPrimal(index, lhsLin, lhsAng, lhsCross, rhsLin, rhsAng);\n            float3 dxLin, dxAng;" solver "${solver}")
        string(REPLACE "solve(lhsLin, lhsAng, lhsCross, -rhsLin, -rhsAng, dxLin, dxAng);" "if (researchSupports())\n            {\n                BodySolve factor(lhsLin, lhsAng, lhsCross);\n                factor.solve(-rhsLin, -rhsAng, dxLin, dxAng);\n                researchSupport(index, factor, dxLin, dxAng);\n            }\n            else solve(lhsLin, lhsAng, lhsCross, -rhsLin, -rhsAng, dxLin, dxAng);" solver "${solver}")
        string(REPLACE "body->positionAng = body->positionAng + dxAng;" "body->positionAng = researchAdvanceRotation(body->positionAng, dxAng);\n            researchBodyChanged(index);" solver "${solver}")
        string(REPLACE "\n    }\n\n    // Compute velocities" "\n        if (it < iterations)\n        {\n            researchAfterIteration();\n            if (it == iterations - 1) researchCommitVelocities();\n        }\n        else if (!researchMoreStabilization()) break;\n    }\n\n    // Compute velocities" solver "${solver}")
        string(FIND "${solver}" "    // Compute velocities (BDF1)" velocityOffset)
        string(SUBSTRING "${solver}" 0 ${velocityOffset} solver)
        string(APPEND solver "}\n")
        string(REPLACE "float bounds = fabsf(F[0]) * friction;" "float3 extra = researchContactChange(this, i);\n        float bounds = fabsf(F[0] + extra.x) * (researchStep->experiment->researchExtensions && researchStep->experiment->lastStabilizationIterations > 0 ? 0.0f : friction);" manifold "${manifold}")
        string(REPLACE "length(float2{F[1], F[2]})" "length(float2{F[1] + extra.y, F[2] + extra.z})" manifold "${manifold}")
        string(REPLACE "F[1] *= bounds / frictionScale;" "F[1] = (F[1] + extra.y) * bounds / frictionScale - extra.y;" manifold "${manifold}")
        string(REPLACE "F[2] *= bounds / frictionScale;" "F[2] = (F[2] + extra.z) * bounds / frictionScale - extra.z;" manifold "${manifold}")
        string(REPLACE "            contacts[i].stick = length(float2{C[1], C[2]}) < STICK_THRESH;" "" manifold "${manifold}")
        string(REPLACE "        if (frictionScale <= bounds)" "        contacts[i].stick = frictionScale <= bounds && length(float2{C[1], C[2]}) < STICK_THRESH;\n        if (frictionScale <= bounds)" manifold "${manifold}")
        string(REPLACE "contacts[i].penalty[1] = min(contacts[i].penalty[1] + solver->betaLin * fabsf(C[1]), PENALTY_MAX);\n            contacts[i].penalty[2] = min(contacts[i].penalty[2] + solver->betaLin * fabsf(C[2]), PENALTY_MAX);"
            "researchIncreaseFrictionPenalty(this, i, {C[1], C[2]});" manifold "${manifold}")
        string(REPLACE "float3x3 jLinT = transpose(jLin);"
            "float3 contactOffset = body == bodyA ? rAWorld : rBWorld;\n        float angularCurvature = length(F + extra) * length(contactOffset);\n        if (researchStep->experiment->researchExtensions)\n        {\n            float3 worldForce = transpose(basis) * (F + extra) * (body == bodyA ? 1.0f : -1.0f);\n            angularCurvature = 0.5f * (angularCurvature - dot(contactOffset, worldForce));\n        }\n        lhsAng[0][0] += angularCurvature;\n        lhsAng[1][1] += angularCurvature;\n        lhsAng[2][2] += angularCurvature;\n        if (researchSupportedContact(manifold) && F[0] == 0) K[0][0] = 0;\n        researchAdjustFriction(K, F, extra, bounds, frictionScale);\n        float3x3 jLinT = transpose(jLin);" manifold "${manifold}")
        string(FIND "${manifold}" "void Manifold::updatePrimal" primalStart)
        string(FIND "${manifold}" "void Manifold::updateDual" dualStart)
        math(EXPR primalLength "${dualStart} - ${primalStart}")
        string(SUBSTRING "${manifold}" 0 ${primalStart} manifoldPrefix)
        string(SUBSTRING "${manifold}" ${primalStart} ${primalLength} contactTerms)
        string(SUBSTRING "${manifold}" ${dualStart} -1 manifoldSuffix)
        string(REPLACE "void Manifold::updatePrimal(" "template<bool projectedFriction>\nstatic void addContactTerms(Manifold* manifold, " contactTerms "${contactTerms}")
        string(REPLACE "{\n    float3 dqALin" "{\n    Rigid* bodyA = manifold->bodyA;\n    Rigid* bodyB = manifold->bodyB;\n    auto& contacts = manifold->contacts;\n    const auto& basis = manifold->basis;\n    int numContacts = manifold->numContacts;\n    float friction = manifold->friction;\n    float3 dqALin" contactTerms "${contactTerms}")
        string(REPLACE "researchContactChange(this, i)" "researchContactChange(manifold, i)" contactTerms "${contactTerms}")
        string(REPLACE "researchAdjustFriction(K, F, extra, bounds, frictionScale);" "if constexpr (projectedFriction) researchAdjustFriction(K, F, extra, bounds, frictionScale);\n        else if (bounds == 0) K[1][1] = K[2][2] = 0;" contactTerms "${contactTerms}")
        set(contactEntries [=[
void Manifold::updatePrimal(Rigid* body, float alpha, float3x3& linear, float3x3& angular,
    float3x3& cross, float3& gradientLin, float3& gradientAng)
{
    addContactTerms<true>(this, body, alpha, linear, angular, cross, gradientLin, gradientAng);
}

void researchAddJointContact(Manifold* manifold, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& cross, float3& gradientLin, float3& gradientAng)
{
    addContactTerms<false>(manifold, body, alpha, linear, angular, cross, gradientLin, gradientAng);
}

]=])
        set(manifold "${manifoldPrefix}${contactTerms}${contactEntries}${manifoldSuffix}")
    endif()
    string(FIND "${manifold}" "bool Manifold::initialize()" initializeStart)
    if(dimension STREQUAL "2d")
        string(FIND "${manifold}" "void Manifold::computeConstraint" constraintStart)
    else()
        string(FIND "${manifold}" "template<bool projectedFriction>" constraintStart)
    endif()
    math(EXPR initializeLength "${constraintStart} - ${initializeStart}")
    string(SUBSTRING "${manifold}" 0 ${initializeStart} manifoldPrefix)
    string(SUBSTRING "${manifold}" ${initializeStart} ${initializeLength} contactInitialization)
    string(SUBSTRING "${manifold}" ${constraintStart} -1 manifoldSuffix)
    string(REPLACE "bool Manifold::initialize()" "template<bool predictContacts>\nstatic bool initializeContactPoints(Manifold* manifold)" contactInitialization "${contactInitialization}")
    string(REPLACE "{\n    // Compute friction" "{\n    using Contact = Manifold::Contact;\n    Rigid* bodyA = manifold->bodyA;\n    Rigid* bodyB = manifold->bodyB;\n    auto& contacts = manifold->contacts;\n#ifdef DEMO_2D\n    auto& penalty = manifold->penalty;\n    auto& lambda = manifold->lambda;\n#else\n    auto& basis = manifold->basis;\n    Solver* solver = manifold->solver;\n#endif\n    int& numContacts = manifold->numContacts;\n    float& friction = manifold->friction;\n    // Compute friction" contactInitialization "${contactInitialization}")
    string(REPLACE "numContacts = readPredictedContacts(bodyA, bodyB, contacts);" "if constexpr (predictContacts) numContacts = readPredictedContacts(bodyA, bodyB, contacts);\n    else numContacts = Manifold::collide(bodyA, bodyB, contacts);" contactInitialization "${contactInitialization}")
    if(dimension STREQUAL "2d")
        string(REPLACE "    for (int i = 0; i < numContacts; i++)\n    {\n        // Compute the contact basis"
            "    float initialSeparation = 0;\n    if constexpr (predictContacts)\n        if (!researchStep->experiment->researchExtensions) initialSeparation = readBoxSeparation(bodyA, bodyB, bodyA->position, bodyB->position);\n\n    for (int i = 0; i < numContacts; i++)\n    {\n        // Compute the contact basis" contactInitialization "${contactInitialization}")
        string(REPLACE "contacts[i].C0 = basis * (bodyA->position.xy() + rAW - bodyB->position.xy() - rBW);"
            "contacts[i].C0 = basis * (bodyA->position.xy() - bodyB->position.xy() + rAW - rBW);\n        if constexpr (predictContacts) contacts[i].C0.x = min(0.0f, max(contacts[i].C0.x, initialSeparation));" contactInitialization "${contactInitialization}")
    endif()
    string(REPLACE "int newNumContacts = readPredictedContacts(bodyA, bodyB, newContacts, basis);" "int newNumContacts;\n    if constexpr (predictContacts) newNumContacts = readPredictedContacts(bodyA, bodyB, newContacts, basis);\n    else newNumContacts = Manifold::collide(bodyA, bodyB, newContacts, basis);" contactInitialization "${contactInitialization}")
    string(REPLACE "researchInitializeContact(this, i)" "researchInitializeContact(manifold, i)" contactInitialization "${contactInitialization}")
    set(contactEntries [=[
bool Manifold::initialize()
{
    if (researchStep->experiment->newton.compliant) return initializeNewtonContact(this);
    return initializeContactPoints<true>(this);
}

bool researchInitializeCurrentContacts(Manifold* manifold)
{
    return initializeContactPoints<false>(manifold);
}

]=])
    set(manifold "${manifoldPrefix}${contactInitialization}${contactEntries}${manifoldSuffix}")
    if(dimension STREQUAL "2d" OR dimension STREQUAL "3d")
        string(REPLACE "if (researchStep->experiment->newton.compliant) return initializeNewtonContact(this);" "if (researchStep->experiment->newton.compliant || !researchStep->experiment->researchExtensions) return initializeNewtonContact(this);" manifold "${manifold}")
        string(REPLACE "    else newNumContacts = Manifold::collide(bodyA, bodyB, newContacts, basis);"
            "    else newNumContacts = Manifold::collide(bodyA, bodyB, newContacts, basis);\n    if (!researchStep->experiment->researchExtensions) newNumContacts = researchContactPatch(manifold, newContacts, newNumContacts);" manifold "${manifold}")
        string(REPLACE "    // Compute error at q- and update penalty and lambdas"
            "    float initialSeparation = readBoxSeparation(bodyA, bodyB, bodyA->positionLin, bodyA->positionAng, bodyB->positionLin, bodyB->positionAng);\n    // Compute error at q- and update penalty and lambdas" manifold "${manifold}")
        string(REPLACE "contacts[i].C0 = basis * (xA - xB);"
            "contacts[i].C0 = basis * (xA - xB);\n        if (!researchStep->experiment->researchExtensions) contacts[i].C0.x = min(0.0f, max(contacts[i].C0.x, initialSeparation));" manifold "${manifold}")
    endif()
    string(REPLACE "if (dot(dp, dp) <= r * r" "if ((researchBodyAwake(bodyA) || researchBodyAwake(bodyB)) && dot(dp, dp) <= r * r" solver "${solver}")
    string(REPLACE "!force->initialize()" "!researchInitializeForce(force)" solver "${solver}")
    string(REPLACE "// Initialize and warmstart bodies (ie primal variables)" "researchBuildIslands();\n    // Initialize and warmstart bodies (ie primal variables)" solver "${solver}")
    string(REPLACE "// Compute inertial position (Eq 2)" "if (body->mass > 0 && !researchBodyAwake(body)) continue;\n        // Compute inertial position (Eq 2)" solver "${solver}")
    string(REPLACE "if (body->mass <= 0)" "if (!researchBodySolves(body))" solver "${solver}")
    string(REPLACE "            // Skip static / kinematic bodies"
        "            if (it >= iterations && researchStep->experiment->researchExtensions\n                && researchJointBody(researchStep->experiment->index(body))) continue;\n            // Skip static / kinematic bodies" solver "${solver}")
    string(REPLACE "// Initialize left and right hand sides" "++researchStep->experiment->islands.bodyUpdates;\n            // Initialize left and right hand sides" solver "${solver}")
    string(FIND "${solver}" "            // Initialize left and right hand sides" bodyEquationStart)
    string(FIND "${solver}" "            // Solve the SPD linear system" bodyEquationEnd)
    string(SUBSTRING "${solver}" 0 ${bodyEquationStart} solverPrefix)
    string(SUBSTRING "${solver}" ${bodyEquationEnd} -1 solverSuffix)
    if(dimension STREQUAL "2d")
        set(bodyEquation "            float3x3 lhs{};\n            float3 rhs{};\n            researchBodyEquation(body, currentAlpha, lhs, rhs);\n\n")
        string(REPLACE "            researchPrimal(body, lhs, rhs);\n" "" solverSuffix "${solverSuffix}")
    else()
        set(bodyEquation "            float3x3 lhsLin{}, lhsAng{}, lhsCross{};\n            float3 rhsLin{}, rhsAng{};\n            researchBodyEquation(body, researchPostStabilize() ? (it < iterations ? 1.0f : 0.0f) : alpha, lhsLin, lhsAng, lhsCross, rhsLin, rhsAng);\n\n")
        string(REPLACE "            researchPrimal(index, lhsLin, lhsAng, lhsCross, rhsLin, rhsAng);\n" "" solverSuffix "${solverSuffix}")
    endif()
    set(solver "${solverPrefix}${bodyEquation}${solverSuffix}")
    if(dimension STREQUAL "3d")
        string(FIND "${solver}" "        // Primal update" colorStart)
        string(FIND "${solver}" "        // Dual update" colorEnd)
        math(EXPR colorLength "${colorEnd} - ${colorStart}")
        string(SUBSTRING "${solver}" ${colorStart} ${colorLength} originalPrimal)
        string(SUBSTRING "${solver}" 0 ${colorStart} colorPrefix)
        string(SUBSTRING "${solver}" ${colorEnd} -1 colorSuffix)
        set(solver "${colorPrefix}        if (!researchStep->experiment->researchExtensions) researchSolveRigidColors(researchPostStabilize() ? (it < iterations ? 1.0f : 0.0f) : alpha);\n        else\n        {\n${originalPrimal}        }\n\n${colorSuffix}")
    endif()
    string(REPLACE "#include \"ResearchStep.h\"" "#include \"ResearchStep.h\"\n#include \"BridgeState.h\"" solver "${solver}")
    string(REPLACE "force->updateDual(alpha);" "if (researchForceSolves(force))\n            {\n                if (auto* manifold = dynamic_cast<Manifold*>(force); manifold && researchNewtonContact(manifold)) researchUpdateContactPressure(manifold, researchPostStabilize() ? 1.0f : alpha);\n                else force->updateDual(researchPostStabilize() ? 1.0f : alpha);\n            }" solver "${solver}")
    string(REPLACE "// Compute constraint\n" "if (!researchForceSolves(force)) continue;\n                // Compute constraint\n" solver "${solver}")
    if(dimension STREQUAL "2d")
        string(REPLACE "                if (!researchForceSolves(force)) continue;\n                // Compute constraint"
            "                if (!researchForceSolves(force)) continue;\n                if (auto* manifold = dynamic_cast<Manifold*>(force); manifold && !researchStep->experiment->researchExtensions) { researchUpdateContactPressure(manifold, currentAlpha); continue; }\n                // Compute constraint" solver "${solver}")
    endif()
    string(REPLACE "for (int i = 0; i < force->rows(); i++)\n            {" "for (int i = 0; researchForceAwake(force) && (researchStep->experiment->researchExtensions || !dynamic_cast<Manifold*>(force)) && i < force->rows(); i++)\n            {" solver "${solver}")
    string(FIND "${solver}" "    // Perform broadphase collision detection" broadphaseStart)
    string(FIND "${solver}" "    // Initialize and warmstart forces" broadphaseEnd)
    string(SUBSTRING "${solver}" 0 ${broadphaseStart} solverPrefix)
    string(SUBSTRING "${solver}" ${broadphaseEnd} -1 solverSuffix)
    set(solver "${solverPrefix}    researchDetectCollisions();\n\n${solverSuffix}")
    if(dimension STREQUAL "3d")
        string(FIND "${solver}" "        // Dual update" dualStart)
        string(FIND "${solver}" "\n        if (it < iterations)\n        {\n            researchAfterIteration" dualEnd)
        math(EXPR dualLength "${dualEnd} - ${dualStart}")
        string(SUBSTRING "${solver}" ${dualStart} ${dualLength} originalDual)
        string(SUBSTRING "${solver}" 0 ${dualStart} dualPrefix)
        string(SUBSTRING "${solver}" ${dualEnd} -1 dualSuffix)
        set(solver "${dualPrefix}        if (!researchStep->experiment->researchExtensions)\n        {\n            if (it < iterations) researchUpdateRigidForces(researchPostStabilize() ? 1.0f : alpha);\n        }\n        else\n        {\n${originalDual}        }\n${dualSuffix}")
    endif()
    if(dimension STREQUAL "2d")
        string(REPLACE "void Manifold::computeConstraint(float alpha)\n{" "void Manifold::computeConstraint(float alpha)\n{\n    if (!researchStep->experiment->researchExtensions && !researchPostStabilize() && alpha != 0) alpha = rigidContactAlpha;" manifold "${manifold}")
        string(REPLACE "force->lambda[i] = force->lambda[i] * alpha * gamma;" "force->lambda[i] = force->lambda[i] * (!researchStep->experiment->researchExtensions && dynamic_cast<Manifold*>(force) ? rigidContactAlpha : alpha) * gamma;" solver "${solver}")
    endif()
    if(AVBD_PROFILE)
        string(REPLACE "    // Initialize and warmstart bodies" "    RESEARCH_TIME(predictionTime, Prediction);\n    // Initialize and warmstart bodies" solver "${solver}")
        string(REPLACE "    // Main solver loop" "    RESEARCH_TIME_END(predictionTime);\n    // Main solver loop" solver "${solver}")
    endif()
    if(AVBD_PROFILE AND dimension STREQUAL "2d")
        string(REPLACE "        // Primal update" "        RESEARCH_TIME(primalTime, Primal);\n        // Primal update" solver "${solver}")
        string(REPLACE "        // Dual update" "        RESEARCH_TIME_END(primalTime);\n        RESEARCH_TIME(dualTime, Dual);\n        // Dual update" solver "${solver}")
        string(REPLACE "\n        if (it < iterations)\n        {\n            researchAfterIteration" "\n        RESEARCH_TIME_END(dualTime);\n        if (it < iterations)\n        {\n            researchAfterIteration" solver "${solver}")
    endif()
    file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/collide-${dimension}.cpp" CONTENT "${collide}" @ONLY)
    list(APPEND sources "${CMAKE_CURRENT_BINARY_DIR}/collide-${dimension}.cpp")
    file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/manifold-${dimension}.cpp" CONTENT "${manifold}" @ONLY)
    list(APPEND sources "${CMAKE_CURRENT_BINARY_DIR}/manifold-${dimension}.cpp")
    file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/solver-${dimension}.cpp" CONTENT "${solver}" @ONLY)
    string(TOUPPER "${dimension}" suffix)
    add_library(Avbd${suffix} SHARED ${sources} "${CMAKE_CURRENT_BINARY_DIR}/solver-${dimension}.cpp" Bridge.cpp LongRange.cpp ContactSupports.cpp JointSolve.cpp StepVelocity.cpp Islands.cpp DirectContact.cpp ContactColumns.cpp ContactFriction.cpp Stabilization.cpp BodyEquation.cpp AvbdContact.cpp Step.cpp NewtonRigid.cpp)
    target_include_directories(Avbd${suffix} PRIVATE "${reference}" "${CMAKE_CURRENT_SOURCE_DIR}")
    target_compile_definitions(Avbd${suffix} PRIVATE DEMO_${suffix})
    if(AVBD_PROFILE)
        target_compile_definitions(Avbd${suffix} PRIVATE AVBD_PROFILE)
    endif()
    target_link_libraries(Avbd${suffix} PRIVATE opengl32)
    if(MSVC)
        target_compile_options(Avbd${suffix} PRIVATE /O2 /fp:precise /W3 "$<$<CONFIG:Release>:/Ob3>")
        set_property(TARGET Avbd${suffix} PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE TRUE)
    endif()
endforeach()

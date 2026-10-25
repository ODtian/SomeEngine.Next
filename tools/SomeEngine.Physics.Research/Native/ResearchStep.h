#pragma once
#include <vector>
#include "solver.h"
#include "StepMeasurements.h"

struct Experiment;
inline constexpr float constraintAccuracy = 0.001f;
inline constexpr int contactIterationMultiplier = 8;
inline constexpr int bodyRefinementLimit = 4;
inline constexpr double contactGeometryAccuracy = 0.00001;
inline constexpr float newtonShapeGap = 0.1f;
// Newton's hard-contact alpha also controls matched-force retention.
inline constexpr float rigidContactAlpha = .95f;
inline float researchContactNormalStart(float value)
{
    return clamp(value, -constraintAccuracy * .25f, 0.0f);
}

struct ResearchStep
{
    Experiment* experiment;
    void (*correct)(void*);
    void* context;
    int iteration;
    int currentIteration = 0;
    int configuredIterations = 0;
    int additionalContactIterations = 0;
    float maximumBodyChangeSquared = 0;
    Rigid** bodies = nullptr;
    int bodyCount = 0;
    bool stabilizationForcesSaved = false;
    float contactQueryDistance = 0;
    void (*runRanges)(void*, int, void (*)(void*, int), void*) = nullptr;
    void* jobContext = nullptr;
    int workerCount = 0;
#ifdef AVBD_PROFILE
    StepMeasurements* measurements = nullptr;
#endif
};

extern thread_local ResearchStep* researchStep;
void stepNewtonRigid();
bool researchContactPenetrates(Manifold* contact, float tolerance, float3& normal, float& gap);
inline bool researchContactPenetrates(Manifold* contact, float tolerance)
{
    float3 normal; float gap;
    return researchContactPenetrates(contact, tolerance, normal, gap);
}

bool initializeNewtonContact(Manifold* manifold, bool refresh = false);
float3 newtonInverseInertia(Rigid* body, float3 angular);
float newtonPairConditioning(Rigid* bodyA, float3 offsetA, Rigid* bodyB, float3 offsetB, float3 direction);
float3 newtonContactForce(Experiment* experiment, Manifold* manifold, int point);
#ifdef DEMO_2D
void researchRefreshPlanarContact(Manifold* contact, float3 normal, float gap);
float newtonPlanarDifference(float current, float previous);
float newtonPlanarAdvance(float angle, float change);
void researchUpdateContactPressure(Manifold* manifold, float alpha);
void researchContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& hessian, float3& gradient);
#endif
#ifndef DEMO_2D

// LDL reads the lower triangles of both symmetric diagonal blocks.
inline void researchAddBodyProduct(float3x3& matrix, float3 first, float3 second)
{
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column <= row; ++column) matrix[row][column] += first[row] * second[column];
}
inline void researchAddBodyMetric(float3x3& matrix, float3 direction, float stiffness)
{
    for (int row = 0; row < 3; ++row)
        for (int column = 0; column <= row; ++column)
            matrix[row][column] += (direction[row] * direction[column]) * stiffness;
}

struct AngularInertia;
bool researchStructuralBody(Rigid* body);
void researchInitializeJoint(Joint* joint);
float3 researchJointRotationError(Joint* joint);
bool researchNewtonContact(Manifold* manifold);
void researchSolveRigidColors(float alpha);
void researchUpdateRigidForces(float alpha);
void researchPairedContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& cross, float3& gradientLin, float3& gradientAng);
void researchUpdateContactPressure(Manifold* manifold, float alpha);
float readBoxSeparation(Rigid* bodyA, Rigid* bodyB, float3 centerA, quat rotationA, float3 centerB, quat rotationB);
void researchContactEquation(Manifold* manifold, Rigid* body, float alpha, float3x3& linear,
    float3x3& angular, float3x3& cross, float3& gradientLin, float3& gradientAng);
int researchContactPatch(Manifold* manifold, Manifold::Contact* contacts, int count);
AngularInertia newtonAngularInertia(Rigid* body);
float3 newtonRotationDifference(quat current, quat previous);
float3 newtonAngularPrediction(Rigid* body);
#endif
void researchBeforeIteration(int iteration);
void researchInitializeJointBounds(Experiment* experiment);
void researchRestoreContactForces(Experiment* experiment);
void researchUpdateLongRangeForces();
float researchContactResponse(Rigid* body, float3 point, float3 direction);
void researchBodyChanged(Rigid* body);
void researchBodyChanged(int body);
void researchVelocity(Rigid* body);
void researchCommitVelocities();
bool researchPostStabilize();
int researchRecoveryLimit();
void researchAfterIteration();
void researchRemoveForce(Force* force);
float3 researchContactChange(Manifold* manifold, int contact);
bool researchSupports();
bool researchSupportedContact(Manifold* manifold);
bool researchContactActive(Manifold* manifold);
bool researchContactTouches(Manifold* manifold);
float readBodyTravel(Rigid* body);
float researchContactDistance();
bool researchCollisionExcluded(Rigid* bodyA, Rigid* bodyB);
bool researchContactExists(Rigid* bodyA, Rigid* bodyB);
void researchSolveConstraints(int sweeps = 2);
void researchClassifyJoints();
bool researchJointBody(int body);
void researchSelectIterations();
void researchUpdateSupports();
void researchFinishSupportRanges(Experiment* experiment);
void researchTransferContactPressure(Manifold* manifold, int point);
void researchInitializeContact(Manifold* manifold, int point);
void researchRefreshContacts();
bool researchBodyAwake(Rigid* body);
bool researchForceAwake(Force* force);
bool researchInitializeForce(Force* force);
void researchBuildIslands();
void researchDetectCollisions();
bool researchAddContacts();
void researchBeginCollisionMotion();
bool researchAddMovedContacts();
void researchSolveContactBlocks();
void researchSolveContactColumns();
bool researchSolveColumnFriction(Experiment* experiment);
void researchCommitColumnFriction(Experiment* experiment);
void researchSaveStabilizationForces(Experiment* experiment);
void researchSaveNewStabilizationContacts(Manifold* manifold);
bool researchInitializeStabilization();
void researchExtendContactSolve();
bool researchMoreStabilization();
void restoreStabilizationForces(Experiment* experiment);
bool researchDistributePressure(const double points[8][2], int count, const double target[2], double* weights);
void researchSolveRestingSupports();
bool researchBodySolves(Rigid* body);
bool researchForceSolves(Force* force);
bool researchInitializeCurrentContacts(Manifold* manifold);
#ifdef DEMO_2D
int readPredictedContacts(Rigid* bodyA, Rigid* bodyB, Manifold::Contact* contacts);
int researchReadContacts(Manifold* manifold, Manifold::Contact* contacts, bool predict = true);
float readBoxSeparation(Rigid* first, Rigid* second, float3 firstPose, float3 secondPose);
inline float2 researchRotateContactPoint(float angle, float2 point)
{
    return angle == 0 ? point : rotate(angle, point);
}
float researchContactPenalty(Force* force, int row, float value);
#else
struct AngularInertia
{
    double gradient[3]{}, hessian[9]{};
};
AngularInertia researchAngularInertia(Rigid* body);
void researchIncreaseFrictionPenalty(Manifold* manifold, int point, float2 travel);
void researchAdjustFriction(float3x3& hessian, float3 force, float3 extra, float bound, float magnitude);
void researchAddJointContact(Manifold* manifold, Rigid* body, float alpha,
    float3x3& linear, float3x3& angular, float3x3& cross, float3& gradientLin, float3& gradientAng);

inline quat researchAdvanceRotation(quat rotation, float3 change)
{
    return lengthSq(change) == 0 ? rotation : rotation + change;
}

inline float3 researchRotationDifference(quat a, quat b)
{
    double scale = 2.0 / ((double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w);
    return {
        (float)((((double)a.x * b.w - (double)a.w * b.x) + ((double)a.z * b.y - (double)a.y * b.z)) * scale),
        (float)((((double)a.y * b.w - (double)a.w * b.y) + ((double)a.x * b.z - (double)a.z * b.x)) * scale),
        (float)((((double)a.z * b.w - (double)a.w * b.z) + ((double)a.y * b.x - (double)a.x * b.y)) * scale)};
}

int readPredictedContacts(Rigid* bodyA, Rigid* bodyB, Manifold::Contact* contacts, float3x3& basis);
int researchReadContacts(Manifold* manifold, Manifold::Contact* contacts, float3x3& basis, bool predict = true);
int researchClipContactFace(Manifold* manifold, Manifold::Contact* contacts, float3x3& basis);
#endif

// Each body uses one factorization for its AVBD update and support reactions.
struct BodySolve
{
#ifdef DEMO_2D
    float d1, d2, d3, l21, l31, l32;
    explicit BodySolve(const float3x3& lhs)
    {
        d1 = lhs[0][0];
        l21 = lhs[1][0] / d1;
        l31 = lhs[2][0] / d1;
        d2 = lhs[1][1] - l21 * l21 * d1;
        l32 = (lhs[2][1] - l21 * l31 * d1) / d2;
        d3 = lhs[2][2] - (l31 * l31 * d1 + l32 * l32 * d2);
    }

    float3 solve(float3 rhs) const
    {
        float y1 = rhs.x;
        float y2 = rhs.y - l21 * y1;
        float y3 = rhs.z - l31 * y1 - l32 * y2;
        float z1 = y1 / d1, z2 = y2 / d2, z3 = y3 / d3;
        float3 result;
        result.z = z3;
        result.y = z2 - l32 * result.z;
        result.x = z1 - l21 * result.y - l31 * result.z;
        return result;
    }
#else
    double d1, d2, d3, l21, l31, l32;
    double d4, d5, d6, l41, l42, l43, l51, l52, l53, l54, l61, l62, l63, l64, l65;

    BodySolve(const float3x3& linear, const float3x3& angular, const float3x3& cross)
    {
        d1 = linear[0][0];
        l21 = linear[1][0] / d1;
        l31 = linear[2][0] / d1;
        l41 = cross[0][0] / d1;
        l51 = cross[1][0] / d1;
        l61 = cross[2][0] / d1;
        d2 = linear[1][1] - l21 * l21 * d1;
        l32 = (linear[2][1] - l21 * l31 * d1) / d2;
        l42 = (cross[0][1] - l21 * l41 * d1) / d2;
        l52 = (cross[1][1] - l21 * l51 * d1) / d2;
        l62 = (cross[2][1] - l21 * l61 * d1) / d2;
        d3 = linear[2][2] - (l31 * l31 * d1 + l32 * l32 * d2);
        l43 = (cross[0][2] - l31 * l41 * d1 - l32 * l42 * d2) / d3;
        l53 = (cross[1][2] - l31 * l51 * d1 - l32 * l52 * d2) / d3;
        l63 = (cross[2][2] - l31 * l61 * d1 - l32 * l62 * d2) / d3;
        d4 = angular[0][0] - (l41 * l41 * d1 + l42 * l42 * d2 + l43 * l43 * d3);
        l54 = (angular[1][0] - l41 * l51 * d1 - l42 * l52 * d2 - l43 * l53 * d3) / d4;
        l64 = (angular[2][0] - l41 * l61 * d1 - l42 * l62 * d2 - l43 * l63 * d3) / d4;
        d5 = angular[1][1] - (l51 * l51 * d1 + l52 * l52 * d2 + l53 * l53 * d3 + l54 * l54 * d4);
        l65 = (angular[2][1] - l51 * l61 * d1 - l52 * l62 * d2 - l53 * l63 * d3 - l54 * l64 * d4) / d5;
        d6 = angular[2][2] - (l61 * l61 * d1 + l62 * l62 * d2 + l63 * l63 * d3 + l64 * l64 * d4 + l65 * l65 * d5);
    }

    void solve(float3 linear, float3 angular, float3& changeLin, float3& changeAng) const
    {
        double y1 = linear.x;
        double y2 = linear.y - l21 * y1;
        double y3 = linear.z - l31 * y1 - l32 * y2;
        double y4 = angular.x - l41 * y1 - l42 * y2 - l43 * y3;
        double y5 = angular.y - l51 * y1 - l52 * y2 - l53 * y3 - l54 * y4;
        double y6 = angular.z - l61 * y1 - l62 * y2 - l63 * y3 - l64 * y4 - l65 * y5;
        double z1 = y1 / d1, z2 = y2 / d2, z3 = y3 / d3;
        double z4 = y4 / d4, z5 = y5 / d5, z6 = y6 / d6;
        double x6 = z6;
        double x5 = z5 - l65 * x6;
        double x4 = z4 - l54 * x5 - l64 * x6;
        double x3 = z3 - l43 * x4 - l53 * x5 - l63 * x6;
        double x2 = z2 - l32 * x3 - l42 * x4 - l52 * x5 - l62 * x6;
        double x1 = z1 - l21 * x2 - l31 * x3 - l41 * x4 - l51 * x5 - l61 * x6;
        changeLin = {(float)x1, (float)x2, (float)x3};
        changeAng = {(float)x4, (float)x5, (float)x6};
    }

#endif
};

#ifdef DEMO_2D
void researchBodyEquation(Rigid* body, float alpha, float3x3& hessian, float3& gradient);
void researchPrimal(Rigid* body, float3x3& lhs, float3& rhs);
void researchSupport(Rigid* body, const BodySolve& factor, float3& change);
#else
void researchBodyEquation(Rigid* body, float alpha, float3x3& linear, float3x3& angular,
    float3x3& cross, float3& gradientLin, float3& gradientAng);
void researchPrimal(int body, float3x3& lhsLin, float3x3& lhsAng,
    float3x3& lhsCross, float3& rhsLin, float3& rhsAng);
void researchSupport(int body, const BodySolve& factor, float3& changeLin, float3& changeAng);
#endif

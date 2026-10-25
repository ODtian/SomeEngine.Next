#pragma once
#include <algorithm>
#include <array>
#include <vector>
#include <unordered_map>
#include "solver.h"
#include "StepMeasurements.h"

struct BodyData
{
    double inertia[6], difference[6], curvature[9];
};

struct ConstraintData
{
    int bodyA, bodyB, kind, normalRow;
    double error, force, friction, penalty;
    double jacobianA[6], jacobianB[6];
};

struct BodyFrame
{
    double position[3], rotation[4], size[3], mass;
};

struct JointData
{
    int bodyA, bodyB, angularFixed, source;
    double pointA[3], pointB[3];
};

struct SupportContact
{
    int bodyA, bodyB, source, sticks;
    double pointA[3], pointB[3], normal[3], force[3], friction;
};

struct ContactGeometryData
{
    int bodyA, bodyB;
    double pointA[3], pointB[3], normal[3], frameGap[3], friction;
};

struct DistanceRange
{
    int bodyA, bodyB, kind, loadStart, loadCount;
    double pointA[3], pointB[3], minimum, maximum;
};

struct ContactLoad
{
    int contact;
    double weight;
};

struct SupportNode
{
    int contact, direction, belowStart, belowCount;
};

struct ContactReference
{
    Manifold* manifold;
    int point;
};

// Every manifold is created by the research collision pipeline. Its endpoint
// indices and support classification share the manifold's lifetime.
struct NewtonContactMatch
{
    float3 midpoint, normal;
};

struct ContactManifold : Manifold
{
    int indexA, indexB;
    int reactionIndex = -1;
    std::array<float3, 8> reactions{}, frames{};
    std::array<NewtonContactMatch, 8> matches{};
    bool supported = false;
    int detectedCount = -1;
#ifdef DEMO_2D
    float penetration = 0;
#else
    bool geometryValid = false;
    float3 geometryCenterA{}, geometryCenterB{};
    quat geometryRotationA{}, geometryRotationB{};
    bool offsetsValid = false;
    float penetration = 0;
    quat rotationA{}, rotationB{};
    std::array<float3, 8> offsetsA, offsetsB;
#endif
    ContactManifold(Solver* solver, Rigid* first, Rigid* second, int a, int b)
        : Manifold(solver, first, second), indexA(a), indexB(b) {}
};

#ifndef DEMO_2D
inline ContactManifold& readContactOffsets(Manifold* manifold)
{
    auto& data = *static_cast<ContactManifold*>(manifold);
    quat a = manifold->bodyA->positionAng, b = manifold->bodyB->positionAng;
    if (!data.offsetsValid || a.x != data.rotationA.x || a.y != data.rotationA.y || a.z != data.rotationA.z || a.w != data.rotationA.w)
    {
        for (int point = 0; point < manifold->numContacts; ++point)
            data.offsetsA[point] = rotate(a, manifold->contacts[point].rA);
        data.rotationA = a;
    }
    if (!data.offsetsValid || b.x != data.rotationB.x || b.y != data.rotationB.y || b.z != data.rotationB.z || b.w != data.rotationB.w)
    {
        for (int point = 0; point < manifold->numContacts; ++point)
            data.offsetsB[point] = rotate(b, manifold->contacts[point].rB);
        data.rotationB = b;
    }
    data.offsetsValid = true;
    return data;
}
#endif

struct BodyForce
{
    Force* force;
    Manifold* contact;
    Joint* joint;
};

struct ContactForces
{
    int count = 0;
    std::array<int, 8> features;
    std::array<float3, 8> forces;
};

struct SupportEquation
{
    int range;
    float heightError, reaction;
#ifdef DEMO_2D
    float3 gradient, response;
#else
    float3 angularGradient, translationResponse, rotationResponse;
#endif
};

struct LongRangeData
{
    std::vector<DistanceRange> ranges;
    struct JointAnchor
    {
        int body;
        float3 local, offset, point, initialPoint;
    };
    struct JointBound
    {
        int anchorA, anchorB;
        float minimum, maximum, penalty;
    };
    std::vector<JointAnchor> jointAnchors;
    std::vector<JointBound> jointBounds;
    std::vector<std::vector<int>> bodyAnchors, bodyJointRanges;
    std::vector<std::vector<int>> bodySupports;
    std::vector<ContactLoad> loads;
    std::vector<SupportNode> supports;
    std::vector<double> supportLoads;
    std::vector<float2> startErrors;
    std::vector<float2> rangeForces;
    int jointRangeCount = 0;
    bool enabled = false;
};

struct JointTreeBody
{
    int parent = -1, joint = -1, rows = 0;
    double hessian[36]{}, gradient[6]{}, factor[36]{};
    double response[36]{}, inverse[36]{}, parentJacobian[36]{};
    double right[6]{}, freeChange[6]{}, change[6]{};
    double angularCurvature[9]{};
};

struct JointTree
{
    int version = -1;
    std::vector<JointTreeBody> bodies;
    std::vector<int> order;
    std::vector<std::vector<std::pair<int, int>>> adjacent;
    std::vector<unsigned char> visited, activeBodies, solvedBodies;
    std::vector<int> contactBodies, expansion;
    bool localContacts = false;
};

struct SupportGraphPoint
{
    float2 position;
    int node;
};

struct SupportGraphPolygon
{
    std::vector<SupportGraphPoint> points, sorted, hull;
    int hullPointCount = -1;
};

struct SupportGraphCandidate
{
    int contact, lower, upper, direction;
    float3 lowerPoint, upperPoint;
};

struct BodyPose
{
    float3 center;
#ifdef DEMO_2D
    float angle;
#else
    quat rotation;
#endif
};

struct SupportBodyMotion
{
    float3 travel;
    bool moving;
};

struct ContactColumnBody
{
    Manifold* below = nullptr;
    int lower = -1, upper = -1;
    double separation = 0, offset = 0, predicted = 0, position = 0;
    double force = 0, centerX = 0, centerY = 0;
    std::array<double, 8> weights{};
    float3 tangentVelocity{};
    int frictionPatch = -1;
};

struct ContactColumnFriction
{
    double speed, impulse, bound, inverseMass, diagonal, right, change;
    int boundDirection;
};

struct ContactColumnPatch
{
    int body, count;
    float3 force;
    std::array<Manifold::Contact, 8> contacts{};
#ifndef DEMO_2D
    float3x3 basis;
#endif
};

struct ContactColumnPool
{
    int first, last;
    double mass, total;
};

struct ContactColumns
{
    std::vector<ContactColumnBody> bodies;
    std::vector<int> order;
    std::vector<ContactColumnPool> pools;
    std::vector<BodyPose> poses;
    std::vector<ContactColumnFriction> friction;
    std::vector<ContactColumnPatch> patches;
};

struct IslandMomentum
{
    double mass = 0;
    double linearMomentum[3]{}, inertialMomentum[3]{};
    double positionDifference[3]{};
};

struct SupportGraph
{
    std::vector<SupportGraphPolygon> polygons;
    std::vector<SupportGraphCandidate> candidates;
    std::vector<double> shifts;
    std::vector<BodyPose> poses;
    std::vector<SupportBodyMotion> bodyMotion;
    std::vector<unsigned char> bodySupported, movingBodies;
    std::vector<unsigned char> pointSupported;
    std::array<ContactLoad, 3> weights;
    int weightCount = 0;
    int manifoldCount = 0;
    int contactVersion = -1, geometryVersion = -1, jointVersion = -1;
    std::vector<unsigned char> contactIslandBodies;
    bool dynamicContacts = false;
};

struct StabilizationContact
{
    Manifold* manifold;
    int feature;
    float3 worldForce, worldExtra, penalty;
    bool stick;
};

struct StabilizationJoint
{
    Joint* joint;
#ifdef DEMO_2D
    std::array<float, 3> lambda, penalty;
#else
    float3 lambdaLin, lambdaAng, penaltyLin, penaltyAng;
    bool broken;
#endif
};

struct NewtonJointData
{
    float linearMaterial = 0, angularMaterial = 0;
    float linearRho = 0, angularRho = 0;
#ifndef DEMO_2D
    quat restRelative{0, 0, 0, 1};
#endif
};


struct NewtonRigidData
{
    bool compliant = false;
    float contactStiffness = 10000000.0f;
    float contactDamping = 0;
    float jointLinearStiffness = 100000.0f;
    float jointAngularStiffness = 100000.0f;
    std::vector<float> bodyJointStiffness;
    std::unordered_map<Joint*, NewtonJointData> joints;
    int contactDetectionInterval = 0, collisionPasses = 0;
    bool contactHistory = false;
};

struct Experiment
{
#ifdef AVBD_PROFILE
    StepMeasurements measurements;
#endif
    Solver solver;
    bool researchExtensions = false;
#ifndef DEMO_2D
    bool postStabilize = false;
#endif
    int jointVersion = 0;
    int lastIterations = 0;
    int lastTreeIterations = 0;
    int lastStabilizationIterations = 0;
    std::vector<Rigid*> bodies;
    std::unordered_map<Rigid*, int> indices;
    std::vector<unsigned char> collisionExclusions;
    std::vector<Force*> forces;
    std::vector<float*> multipliers;
    std::vector<ConstraintData> constraints;
    std::vector<Joint*> joints;
    int jointBodyVersion = -1;
    bool jointBodiesOnly = false;
    int jointColorVersion = -1, contactVersion = 0, colorContactVersion = -1;
    std::vector<std::vector<int>> bodyColorGroups;
    std::vector<float3> bodyLinearChanges, bodyAngularChanges;
    std::vector<unsigned char> bodyChanges;
    int contactGeometryVersion = 0;
    int bodyForceContactVersion = -1, bodyForceJointVersion = -1;
    std::vector<std::vector<BodyForce>> bodyForces;
    std::vector<BodyForce> rigidForces;
    std::vector<unsigned char> jointBodies;
    std::vector<SupportContact> supportContacts;
    std::vector<ContactReference> contactReferences;
    std::vector<ContactManifold*> reactionContacts;
    std::unordered_map<Manifold*, ContactForces> contactForces;
    std::vector<StabilizationContact> stabilizationContacts;
    std::vector<StabilizationJoint> stabilizationJoints;
    LongRangeData longRange;
    JointTree jointTree;
    SupportGraph supportGraph;
    ContactColumns contactColumns;
    NewtonRigidData newton;
    std::vector<ContactGeometryData> contactGeometry;

    struct Islands
    {
        std::vector<int> roots, sizes;
        std::vector<std::vector<int>> bodyGroups;
        std::vector<std::vector<std::vector<int>>> colorGroups;
        std::vector<int> groupRoots, updatedBodies;
        std::vector<float> bodyChangeSquared;
        std::vector<std::vector<SupportEquation>> supportEquations;
        std::vector<unsigned char> sleeping, awake, ready;
        std::vector<unsigned char> directContacts;
        std::vector<float3> contactVelocities;
        std::vector<float> quietSeconds;
        std::vector<Force*> deferredForces;
        std::vector<IslandMomentum> momentum;
        bool sleepEnabled = true;
        int activeCount = 0, sleepingCount = 0, bodyUpdates = 0;
        int contactSolves = 0;
    } islands;

    struct CollisionExtent
    {
        bool valid = false;
        float3 extent{};
#ifdef DEMO_2D
        float angle = 0;
#else
        quat rotation{};
#endif
    };
    std::vector<CollisionExtent> collisionExtents;
    struct CollisionSweep
    {
        int body;
        float3 center;
        float radius;
        float3 extent{};
        bool hasExtent = false;
    };
    std::vector<CollisionSweep> collisionSweep;
    std::vector<std::pair<int, int>> collisionPairs;
    std::vector<std::vector<std::pair<int, int>>> collisionRanges;
    struct CollisionGeometry
    {
        Manifold::Contact contacts[8]{};
#ifndef DEMO_2D
        float3x3 basis;
#endif
        int count = 0;
    };
    std::vector<CollisionGeometry> collisionGeometry;
    std::vector<BodyPose> collisionPoses;

    int index(Rigid* body) const { return body ? indices.at(body) : -1; }
};

inline std::array<float3, 8>* findContactReactions(Manifold* manifold)
{
    auto* contact = static_cast<ContactManifold*>(manifold);
    return contact->reactionIndex >= 0 ? &contact->reactions : nullptr;
}

inline std::array<float3, 8>& editContactReactions(Experiment* experiment, Manifold* manifold)
{
    auto* contact = static_cast<ContactManifold*>(manifold);
    if (contact->reactionIndex < 0)
    {
        contact->reactionIndex = (int)experiment->reactionContacts.size();
        experiment->reactionContacts.push_back(contact);
    }
    return contact->reactions;
}

inline void clearContactReactions(Experiment* experiment, Manifold* manifold)
{
    auto* contact = static_cast<ContactManifold*>(manifold);
    if (contact->reactionIndex < 0) return;
    auto& contacts = experiment->reactionContacts;
    contacts[contact->reactionIndex] = contacts.back();
    contacts.back()->reactionIndex = contact->reactionIndex;
    contacts.pop_back();
    contact->reactionIndex = -1;
    contact->reactions.fill(float3{0, 0, 0});
}

inline void clearContactReactions(Experiment* experiment)
{
    for (auto* contact : experiment->reactionContacts)
    {
        contact->reactionIndex = -1;
        contact->reactions.fill(float3{0, 0, 0});
    }
    experiment->reactionContacts.clear();
}

void indexLongRange(Experiment* experiment);
void clearSupportRanges(Experiment* experiment);
bool researchKeepSupportGraph();
void researchBeginSupportRanges(Experiment* experiment);
void transferLongRangeForces(Experiment* experiment);
void beginLongRangeStep(Experiment* experiment);
void removeDemoJoint(Experiment* experiment, Joint* joint);
void initializeIslands(Experiment* experiment);
void wakeBody(Experiment* experiment, int body);
void wakeAllBodies(Experiment* experiment);
void finishIslandStep(Experiment* experiment);
void preserveIslandCenterMotion(Experiment* experiment);

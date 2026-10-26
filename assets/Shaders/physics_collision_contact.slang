#ifndef PHYSICS_COLLISION_CONTACT
#define PHYSICS_COLLISION_CONTACT
struct DeviceContactPrediction
{
    uint enabled;
    uint padding;
    uint2 sampleTime;
};
struct DeviceCollisionContact
{
    uint2 first;
    uint2 second;
    int firstFeature;
    int secondFeature;
    int pointIndex;
    float3 firstLocalPoint;
    float3 secondLocalPoint;
    float3 worldPoint;
    float3 normal;
    float separation;
    int firstBodyCount;
    uint predictionAlignment;
    DeviceContactPrediction prediction;
};
#endif

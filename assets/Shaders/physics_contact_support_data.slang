#ifndef PHYSICS_CONTACT_SUPPORT_DATA
#define PHYSICS_CONTACT_SUPPORT_DATA
#include "physics_contact_support_bodies.slang"
struct DeviceSupportSettings
{
    float3 up; float distanceTolerance;
    float motionTolerance; float angularMotionTolerance; float stickDistance; float retention;
    float initialErrorFraction; float relativePivotTolerance; uint contactsPerBody; uint bodies;
    uint contacts;
};
ConstantBuffer<DeviceSupportSettings> ContactSupportSettings;
struct DeviceBodySupportState
{
    float minimumHeight; float initialError; float reaction; uint enabled;
    uint moving; uint pointCount; uint loadCount; uint equationCount;
};
struct DeviceContactSupportState
{
    float minimumHeight; float initialError; float heightShift; float reaction;
    float load; uint enabled; uint upperIsSecond; uint lowerBody;
    float3 extraForceOnSecond; uint upperBody;
    uint loadCount; uint orderIndex; uint2 padding;
};
struct DeviceSupportLoad { uint contact; float weight; };
struct DeviceSupportCandidate
{
    uint contact; uint lower; uint upper; uint upperIsSecond;
    float2 lowerPoint; float2 upperPoint;
    float height; float gapHeight; uint2 padding;
};
struct DeviceSupportEquation
{
    uint contact; uint3 padding0;
    float3 linear; uint padding1;
    float3 angular; uint padding2;
    float3 translationResponse; uint padding3;
    float3 rotationResponse; uint padding4;
    float heightError; float reaction; uint2 padding5;
};
RWStructuredBuffer<DeviceBodySupportState> DeviceSupportBodyStates;
RWStructuredBuffer<DeviceContactSupportState> DeviceSupportContactStates;
RWStructuredBuffer<uint> DeviceSupportBodyContacts;
RWStructuredBuffer<DeviceSupportLoad> DeviceSupportBodyLoads;
RWStructuredBuffer<DeviceSupportLoad> DeviceSupportContactLoads;
RWStructuredBuffer<DeviceSupportCandidate> DeviceSupportCandidates;
RWStructuredBuffer<uint> DeviceSupportOrder;
RWStructuredBuffer<DeviceSupportEquation> DeviceSupportEquations;
StructuredBuffer<uint2> DeviceContactSupportSelection;
float3 deviceSupportHorizontalX()
{ return normalize(cross(ContactSupportSettings.up,abs(ContactSupportSettings.up.x)<.707?float3(1,0,0):float3(0,1,0))); }
float3 deviceSupportHorizontalY() { return cross(ContactSupportSettings.up,deviceSupportHorizontalX()); }
float2 deviceSupportProject(float3 point) { return float2(dot(point,deviceSupportHorizontalX()),dot(point,deviceSupportHorizontalY())); }
#endif

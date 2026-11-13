#ifndef PHYSICS_CONTACT_COLUMNS
#define PHYSICS_CONTACT_COLUMNS
#include "physics_contact_types.slang"
#include "physics_collision_contact.slang"
struct DeviceContactPair
{
    uint2 firstGeometry; uint2 secondGeometry;
    uint firstShape; uint secondShape; uint firstPoint; uint pointCount;
};
struct DeviceContactMaterial
{
    float normalStiffness; float normalDamping; float staticFriction;
    float dynamicFriction; float restitution; float restitutionThreshold;
};
struct DeviceContactBodyLink
{
    uint2 entity; float3 localPoint; float weight; uint normalReference;
};
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactCounts;
RWStructuredBuffer<DeviceContactPair, ScalarDataLayout> DeviceContactPairs;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactPairIndices;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactFirstFeatures;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactSecondFeatures;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactPointIndices;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceContactFirstLocalPoints;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceContactSecondLocalPoints;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceContactWorldPoints;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceContactNormals;
RWStructuredBuffer<float, ScalarDataLayout> DeviceContactSeparations;
RWStructuredBuffer<float3, ScalarDataLayout> DeviceContactBodyAnchorDeltas;
RWStructuredBuffer<DeviceContactPrediction, ScalarDataLayout> DeviceContactPredictions;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactFlags;
RWStructuredBuffer<uint2, ScalarDataLayout> DeviceContactInputVersions;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactBodyStarts;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactBodyCounts;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactFirstBodyCounts;
RWStructuredBuffer<DeviceContactMaterial, ScalarDataLayout> DeviceContactMaterials;
RWStructuredBuffer<DeviceContactBodyLink, ScalarDataLayout> DeviceContactBodyLinks;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactBodyStateIndices;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactCounts;
StructuredBuffer<DeviceContactPair, ScalarDataLayout> PreviousContactPairs;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactPairIndices;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactFirstFeatures;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactSecondFeatures;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactPointIndices;
StructuredBuffer<float3, ScalarDataLayout> PreviousContactFirstLocalPoints;
StructuredBuffer<float3, ScalarDataLayout> PreviousContactSecondLocalPoints;
StructuredBuffer<float3, ScalarDataLayout> PreviousContactWorldPoints;
StructuredBuffer<float3, ScalarDataLayout> PreviousContactNormals;
StructuredBuffer<float, ScalarDataLayout> PreviousContactSeparations;
StructuredBuffer<float3, ScalarDataLayout> PreviousContactBodyAnchorDeltas;
StructuredBuffer<DeviceContactPrediction, ScalarDataLayout> PreviousContactPredictions;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactFlags;
StructuredBuffer<uint2, ScalarDataLayout> PreviousContactInputVersions;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactBodyStarts;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactBodyCounts;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactFirstBodyCounts;
StructuredBuffer<DeviceContactMaterial, ScalarDataLayout> PreviousContactMaterials;
StructuredBuffer<DeviceContactBodyLink, ScalarDataLayout> PreviousContactBodyLinks;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactBodyStateIndices;

DeviceContactConstraint loadDeviceContact(uint row)
{
    DeviceContactConstraint value=(DeviceContactConstraint)0;
    value.bodyCount=DeviceContactBodyCounts[row]; value.firstBodyCount=DeviceContactFirstBodyCounts[row];
    value.bodyOffset=DeviceContactBodyStarts[row]; value.warmStart=DeviceContactFlags[row]&1;
    value.normal=DeviceContactNormals[row]; value.separation=DeviceContactSeparations[row];
    value.bodyAnchorDelta=DeviceContactBodyAnchorDeltas[row];
    DeviceContactMaterial material=DeviceContactMaterials[row];
    value.normalStiffness=material.normalStiffness; value.normalDamping=material.normalDamping;
    value.staticFriction=material.staticFriction; value.dynamicFriction=material.dynamicFriction;
    value.restitution=material.restitution; value.restitutionThreshold=material.restitutionThreshold;
    DeviceContactPrediction prediction=DeviceContactPredictions[row];
    value.predicted=prediction.enabled; value.sampleTime=prediction.sampleTime;
    value.worldPoint=DeviceContactWorldPoints[row]; value.trigger=(DeviceContactFlags[row]>>1)&1;
    DeviceContactPair pair=DeviceContactPairs[DeviceContactPairIndices[row]];
    value.firstGeometry=pair.firstGeometry; value.secondGeometry=pair.secondGeometry;
    value.firstFeature=int(DeviceContactFirstFeatures[row]); value.secondFeature=int(DeviceContactSecondFeatures[row]);
    value.pointIndex=int(DeviceContactPointIndices[row]);
    return value;
}
DeviceContactBody loadDeviceContactBody(uint index)
{
    DeviceContactBodyLink value=DeviceContactBodyLinks[index];
    DeviceContactBody result;
    result.entity=value.entity; result.localPoint=value.localPoint; result.weight=value.weight;
    result.normalReference=value.normalReference; result.stateIndex=DeviceContactBodyStateIndices[index];
    return result;
}
DeviceContactBody loadPreviousContactBody(uint index)
{
    DeviceContactBodyLink value=PreviousContactBodyLinks[index];
    DeviceContactBody result;
    result.entity=value.entity; result.localPoint=value.localPoint; result.weight=value.weight;
    result.normalReference=value.normalReference; result.stateIndex=PreviousContactBodyStateIndices[index];
    return result;
}
#endif

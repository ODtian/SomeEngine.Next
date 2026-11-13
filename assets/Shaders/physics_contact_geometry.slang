#include "physics_dispatch.slang"
#include "physics_contact_values.slang"
#include "physics_contact_anchor_math.slang"
struct DeviceContactGeometrySettings
{
    uint firstBin; uint binCount; uint inputBodiesPerContact; uint flags;
    DeviceContactMaterial material;
};
ConstantBuffer<DeviceContactGeometrySettings, ScalarDataLayout> ContactGeometrySettings;
struct DeviceContactInputPair
{
    uint2 first; uint2 second; uint firstShape; uint secondShape; uint firstBody; uint secondBody;
};
StructuredBuffer<DeviceContactInputPair, ScalarDataLayout> DeviceMatchInputPairs;
struct DeviceGeometryMaterial { DeviceContactMaterial material; uint present; uint revision; };
StructuredBuffer<DeviceGeometryMaterial, ScalarDataLayout> DeviceMatchInputMaterials;
DeviceContactMaterial deviceContactGeometryMaterial(uint geometry)
{ DeviceGeometryMaterial input=DeviceMatchInputMaterials[geometry]; return input.present!=0?input.material:ContactGeometrySettings.material; }
DeviceContactMaterial deviceCombineContactMaterials(DeviceContactMaterial a,DeviceContactMaterial b)
{
    DeviceContactMaterial value;
    value.normalStiffness=(a.normalStiffness+b.normalStiffness)*.5f;
    value.normalDamping=(a.normalDamping+b.normalDamping)*.5f;
    value.staticFriction=(a.staticFriction+b.staticFriction)*.5f;
    value.dynamicFriction=(a.dynamicFriction+b.dynamicFriction)*.5f;
    value.restitution=(a.restitution+b.restitution)*.5f;
    value.restitutionThreshold=(a.restitutionThreshold+b.restitutionThreshold)*.5f;
    return value;
}
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputBins;
StructuredBuffer<DeviceCollisionContact, ScalarDataLayout> DeviceMatchInputContacts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputTotal;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputCounts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputOffsets;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputBodyCounts;
StructuredBuffer<DeviceContactBody, ScalarDataLayout> DeviceMatchInputBodies;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactDispatchArguments;
void writeDeviceContactDispatch(uint entry,uint count)
{
    uint groups=(count+63)/64;
    DeviceContactDispatchArguments[entry*3]=max(1,min(groups,65535u));
    DeviceContactDispatchArguments[entry*3+1]=max(1,(groups+65534)/65535);
    DeviceContactDispatchArguments[entry*3+2]=1;
}
[shader("compute")][numthreads(1,1,1)]
void WriteDeviceContactDispatchArguments(uint3 id:SV_DispatchThreadID)
{
    uint first=DeviceMatchInputBins[ContactGeometrySettings.firstBin];
    uint last=DeviceMatchInputBins[ContactGeometrySettings.firstBin+ContactGeometrySettings.binCount];
    writeDeviceContactDispatch(0,last-first);
    writeDeviceContactDispatch(1,DeviceMatchInputTotal[0]);
    writeDeviceContactDispatch(2,PreviousContactCounts[1]);
    DeviceContactCounts[1]=DeviceMatchInputTotal[0];
}
void updateDeviceContactGeometry(uint inputPair)
{
    uint first=DeviceMatchInputBins[ContactGeometrySettings.firstBin];
    uint last=DeviceMatchInputBins[ContactGeometrySettings.firstBin+ContactGeometrySettings.binCount];
    if(inputPair>=last-first) return;
    uint count=DeviceMatchInputCounts[inputPair];
    if(count==0) return;
    uint pairIndex; InterlockedAdd(DeviceContactCounts[0],1,pairIndex);
    uint pointStart=DeviceMatchInputOffsets[inputPair];
    DeviceContactInputPair input=DeviceMatchInputPairs[first+inputPair];
    DeviceContactPair pair;
    pair.firstGeometry=input.first; pair.secondGeometry=input.second;
    pair.firstShape=input.firstShape; pair.secondShape=input.secondShape;
    pair.firstPoint=pointStart; pair.pointCount=count; DeviceContactPairs[pairIndex]=pair;
    for(uint point=0;point<count;point++)
    {
        uint row=pointStart+point;
        DeviceCollisionContact current=DeviceMatchInputContacts[row];
        uint bodies=DeviceMatchInputBodyCounts[row];
        uint start; InterlockedAdd(DeviceContactCounts[2],bodies,start);
        DeviceContactPairIndices[row]=pairIndex;
        DeviceContactFirstFeatures[row]=uint(current.firstFeature); DeviceContactSecondFeatures[row]=uint(current.secondFeature);
        DeviceContactPointIndices[row]=uint(current.pointIndex);
        DeviceContactFirstLocalPoints[row]=current.firstLocalPoint; DeviceContactSecondLocalPoints[row]=current.secondLocalPoint;
        DeviceContactWorldPoints[row]=current.worldPoint; DeviceContactNormals[row]=current.normal;
        DeviceContactSeparations[row]=current.separation; DeviceContactPredictions[row]=current.prediction;
        DeviceContactBodyStarts[row]=start; DeviceContactBodyCounts[row]=bodies;
        DeviceContactFirstBodyCounts[row]=uint(current.firstBodyCount);
        DeviceContactFlags[row]=ContactGeometrySettings.flags;
        DeviceContactInputVersions[row]=uint2(DeviceMatchInputMaterials[input.firstShape].revision,DeviceMatchInputMaterials[input.secondShape].revision);
        DeviceContactMaterials[row]=deviceCombineContactMaterials(deviceContactGeometryMaterial(input.firstShape),deviceContactGeometryMaterial(input.secondShape));
        DeviceContactBody originLink=DeviceMatchInputBodies[row*ContactGeometrySettings.inputBodiesPerContact+uint(current.firstBodyCount)];
        DeviceBodyState origin=DeviceBodyStates[originLink.stateIndex];
        if(current.prediction.enabled!=0) origin=deviceBodyStart(origin);
        float3 a=float3(0),b=float3(0);
        for(uint end=0;end<bodies;end++)
        {
            DeviceContactBody link=DeviceMatchInputBodies[row*ContactGeometrySettings.inputBodiesPerContact+end];
            DeviceContactBodyLink value;
            value.entity=link.entity; value.localPoint=link.localPoint; value.weight=link.weight;
            value.normalReference=link.normalReference;
            DeviceContactBodyLinks[start+end]=value; DeviceContactBodyStateIndices[start+end]=link.stateIndex;
            DeviceBodyState body=DeviceBodyStates[link.stateIndex];
            if(current.prediction.enabled!=0) body=deviceBodyStart(body);
            float3 anchor=deviceContactRelativeAnchor(body,deviceContactNormalLocalPoint(link),origin.position)*link.weight;
            if(end<uint(current.firstBodyCount)) a+=anchor; else b+=anchor;
        }
        DeviceContactBodyAnchorDeltas[row]=b-a;
    }
}

[shader("compute")][numthreads(64,1,1)]
void UpdateDeviceContactGeometry(uint3 id:SV_DispatchThreadID)
{ updateDeviceContactGeometry(devicePhysicsThreadIndex(id,64)); }

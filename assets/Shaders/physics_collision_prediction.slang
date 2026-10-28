#ifndef PHYSICS_COLLISION_PREDICTION
#define PHYSICS_COLLISION_PREDICTION
#include "physics_geometry_references.slang"
#include "physics_constraint_math.slang"

interface IDeviceCollisionPrediction : IDeviceCollisionNarrowPhase
{
    float4 getSampleRotation(DeviceGeometryInput geometry, DeviceBodyState body) { return body.startRotation; }
    DeviceFeaturePoint getSupportPoint(DeviceGeometryInput geometry, float3 unitDirection);
    uint getPredictedManifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, float manifoldDistance, out DeviceGeometryDistance points[4]);
};

DeviceBodyState getDeviceRigidPredictedBody(DeviceBodyState body)
{
    body.rotation = body.predictedRotation;
    bodySetCenterOfMass(body, body.predictedCenterOfMass);
    return body;
}

DeviceGeometryInput getDeviceCollisionGeometryPose(DeviceGeometryInput geometry, DeviceBodyState body)
{
    geometry.position = body.position;
    geometry.rotation = body.rotation;
    return geometry;
}

DeviceContactBody getDeviceRigidStartContactBody(uint index, DeviceBodyState start, float3 localPoint, uint normalReference)
{
    DeviceContactBody result;
    result.entity = uint2(start.entityIndex, start.entityGeneration);
    result.localPoint = localPoint;
    result.weight = 1;
    result.stateIndex = index;
    result.normalReference = normalReference;
    return result;
}

float3 getDeviceRigidContactDelta(DeviceBodyState first, float3 firstLocal, DeviceBodyState second, float3 secondLocal)
{
    return (second.position - first.position) + bodyRotate(second.rotation, secondLocal) - bodyRotate(first.rotation, firstLocal);
}

float getDeviceRigidTravel<TKernel : IDeviceCollisionPrediction>(TKernel kernel,
    DeviceGeometryInput geometry, DeviceBodyState body, float3 direction)
{
    float travel = dot(direction, body.predictedCenterOfMass - body.startCenterOfMass);
    float3 first = bodyRotate(float4(-body.startRotation.xyz, body.startRotation.w), direction);
    float3 last = bodyRotate(float4(-body.predictedRotation.xyz, body.predictedRotation.w), direction);
    float3 change = last - first;
    float size = length(change);
    if (size > 0)
    {
        geometry.position = -body.centerOfMass;
        geometry.rotation = float4(0, 0, 0, 1);
        float3 changeDirection=change/size;
        travel += size * dot(kernel.getSupportPoint(geometry,changeDirection).point,changeDirection);
    }
    return max(0, travel);
}

DeviceGeometryDistance getDeviceCollisionSampleDistance<TKernel : IDeviceCollisionPrediction>(TKernel kernel,
    DeviceGeometryInput first, DeviceGeometryInput second, float3 normal)
{
    DeviceFeaturePoint firstSupport=kernel.getSupportPoint(first,normal),secondSupport=kernel.getSupportPoint(second,-normal);
    DeviceGeometryDistance result;
    result.firstPoint=firstSupport.point;
    result.secondPoint=secondSupport.point;
    result.firstFeature=firstSupport.feature; result.secondFeature=secondSupport.feature;
    result.normal=normal; result.separation=dot(normal,result.secondPoint-result.firstPoint);
    return result;
}

// Each rigid reference supplies one body. The selected narrow phase samples its finite convex geometry.
void predictDeviceRigidCollision<TKernel : IDeviceCollisionPrediction>(uint work, TKernel kernel)
{
    if (work >= DetectionParameters.workItems) return;
    uint begin = DetectionBinOffsets[DetectionParameters.bin];
    uint end = DetectionBinOffsets[DetectionParameters.bin + 1];
    uint outputBegin = begin - DetectionBinOffsets[DetectionParameters.firstBin];
    [loop] for (uint local = work; local < end - begin; local += DetectionParameters.workItems)
    {
        uint index = outputBegin + local;
        DeviceCollisionPair pair = DetectionPairs[begin + local];
        uint firstIndex = DeviceGeometryReferences[pair.firstGeometry].firstBody;
        uint secondIndex = DeviceGeometryReferences[pair.secondGeometry].firstBody;
        DeviceBodyState firstBody = DeviceBodyStates[firstIndex], secondBody = DeviceBodyStates[secondIndex];
        DeviceBodyState firstStart = deviceBodyStart(firstBody), secondStart = deviceBodyStart(secondBody);
        DeviceBodyState firstPredicted = getDeviceRigidPredictedBody(firstBody), secondPredicted = getDeviceRigidPredictedBody(secondBody);
        DeviceBodyState firstSample = firstStart, secondSample = secondStart;
        firstSample.rotation = kernel.getSampleRotation(DeviceGeometries[pair.firstGeometry], firstBody);
        secondSample.rotation = kernel.getSampleRotation(DeviceGeometries[pair.secondGeometry], secondBody);
        bodySetCenterOfMass(firstSample, firstBody.startCenterOfMass);
        bodySetCenterOfMass(secondSample, secondBody.startCenterOfMass);
        DeviceGeometryInput first = getDeviceCollisionGeometryPose(DeviceGeometries[pair.firstGeometry], firstStart);
        DeviceGeometryInput second = getDeviceCollisionGeometryPose(DeviceGeometries[pair.secondGeometry], secondStart);
        DeviceGeometryInput firstSampleGeometry = getDeviceCollisionGeometryPose(first, firstSample);
        DeviceGeometryInput secondSampleGeometry = getDeviceCollisionGeometryPose(second, secondSample);
        DeviceGeometryDistance nearest = kernel.distance(work, first, second);
        DeviceCollisionDistance result;
        result.first = uint2(pair.firstEntity, pair.firstGeneration);
        result.second = uint2(pair.secondEntity, pair.secondGeneration);
        result.distance = nearest;
        CollisionDistances[index] = result;
        float firstTravel = getDeviceRigidTravel(kernel, first, firstBody, nearest.normal);
        float secondTravel = getDeviceRigidTravel(kernel, second, secondBody, -nearest.normal);
        float manifoldDistance = max(DetectionParameters.contactDistance, nearest.separation) + firstTravel + secondTravel;
        DeviceGeometryDistance sample=getDeviceCollisionSampleDistance(kernel,firstSampleGeometry,secondSampleGeometry,nearest.normal);
        DeviceGeometryDistance points[4];
        uint count = kernel.getPredictedManifold(work, firstSampleGeometry, secondSampleGeometry, sample, manifoldDistance, points);
        if (count == 0) { points[0] = sample; count = 1; }
        uint written = 0;
        for (uint point = 0; point < count; point++)
        {
            DeviceGeometryDistance distance = points[point];
            float3 firstLocal = bodyRotate(float4(-firstSample.rotation.xyz, firstSample.rotation.w), distance.firstPoint - firstSample.position);
            float3 secondLocal = bodyRotate(float4(-secondSample.rotation.xyz, secondSample.rotation.w), distance.secondPoint - secondSample.position);
            uint firstNormalReference=kernel.getNormalReference(first),secondNormalReference=kernel.getNormalReference(second);
            float3 firstNormalLocal=firstNormalReference==1?float3(0):firstLocal;
            float3 secondNormalLocal=secondNormalReference==1?float3(0):secondLocal;
            float3 startDelta = getDeviceRigidContactDelta(firstStart, firstNormalLocal, secondStart, secondNormalLocal);
            float3 sampleDelta = getDeviceRigidContactDelta(firstSample, firstNormalLocal, secondSample, secondNormalLocal);
            float3 predictedDelta = getDeviceRigidContactDelta(firstPredicted, firstNormalLocal, secondPredicted, secondNormalLocal);
            float startSeparation=distance.separation+dot(distance.normal,startDelta-sampleDelta);
            float gap = startSeparation + dot(distance.normal, predictedDelta - startDelta);
            if (startSeparation > DetectionParameters.contactDistance && gap > DetectionParameters.contactDistance) continue;
            DeviceCollisionContact contact;
            contact.first = result.first; contact.second = result.second;
            contact.firstFeature = distance.firstFeature; contact.secondFeature = distance.secondFeature;
            contact.pointIndex = int(point);
            contact.firstLocalPoint = firstLocal; contact.secondLocalPoint = secondLocal;
            float3 firstStartPoint=firstStart.position+bodyRotate(firstStart.rotation,firstLocal);
            float3 secondStartPoint=secondStart.position+bodyRotate(secondStart.rotation,secondLocal);
            contact.worldPoint = (firstStartPoint + secondStartPoint) * .5f;
            contact.normal = distance.normal; contact.separation = startSeparation;
            contact.firstBodyCount = 1; contact.predictionAlignment = 0;
            contact.prediction.enabled = 1; contact.prediction.padding = 0;
            contact.prediction.sampleTime = DetectionParameters.startTime;
            uint contactIndex = index * 4 + written++;
            CollisionContacts[contactIndex] = contact;
            uint bodyOffset = contactIndex * DetectionParameters.bodiesPerContact;
            CollisionContactBodies[bodyOffset] = getDeviceRigidStartContactBody(firstIndex, firstStart, firstLocal, firstNormalReference);
            CollisionContactBodies[bodyOffset + 1] = getDeviceRigidStartContactBody(secondIndex, secondStart, secondLocal, secondNormalReference);
            CollisionContactBodyCounts[contactIndex] = 2;
        }
        CollisionContactCounts[index] = written;
    }
}
#endif

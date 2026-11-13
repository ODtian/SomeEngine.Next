#ifndef PHYSICS_GEOMETRY_REFERENCES
#define PHYSICS_GEOMETRY_REFERENCES
#include "physics_collision.slang"
#include "physics_bodies.slang"

#include "physics_geometry_reference_data.slang"
#include "physics_contact_body.slang"
RWStructuredBuffer<DeviceContactBody, ScalarDataLayout> CollisionContactBodies;
RWStructuredBuffer<uint, ScalarDataLayout> CollisionContactBodyCounts;
RWStructuredBuffer<DeviceContactBody, ScalarDataLayout> CollisionWorkBodies;
RWStructuredBuffer<uint, ScalarDataLayout> CollisionWorkBodyCounts;
void writeDeviceCollisionContacts(uint pair, uint work, uint count)
{
    uint begin = reserveDeviceCollisionContacts(pair,count);
    for (uint point = 0; point < count; point++)
    {
        uint source = work*4+point, target = begin+point;
        if(target>=DetectionParameters.contacts) continue;
        CollisionContacts[target] = CollisionWorkContacts[source];
        uint bodies = CollisionWorkBodyCounts[source];
        CollisionContactBodyCounts[target] = bodies;
        for (uint body = 0; body < bodies; body++)
            CollisionContactBodies[target*DetectionParameters.bodiesPerContact+body] = CollisionWorkBodies[source*DetectionParameters.bodiesPerContact+body];
    }
}

struct DeviceTriangleAt<TPoints : IDeviceGeometryPointSample> : IDevicePointGeometry

{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        TPoints points;
        float3 a=points.read(geometry,0), b=points.read(geometry,1), c=points.read(geometry,2);
        float3 normal = normalize(cross(b-a,c-a));
        float height = dot(normal,point-a);
        float3 weights;
        float3 delta = closestDeviceTriangle(a-point,b-point,c-point,weights);
        float size = length(delta);
        float3 direction = size > 0 ? -delta/size : (height>=0?normal:-normal);
        int feature = weights.x>=1?2:weights.y>=1?3:weights.z>=1?4:weights.x<=0?5:weights.y<=0?6:weights.z<=0?7:height>=0?0:1;
        DeviceGeometryPointDistance result;
        result.point = point+delta+direction*geometry.convexRadius;
        result.normal = direction; result.distance = size-geometry.convexRadius; result.feature = feature;
        return result;
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        TPoints points;
        DeviceFeaturePoint result;
        result.point = points.read(geometry,0);
        result.feature = 2;
        for (uint index = 1; index < 3; index++)
        {
            float3 point = points.read(geometry,index);
            if (dot(point, direction) > dot(result.point, direction))
            { result.point = point; result.feature = int(index + 2); }
        }
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return geometry.convexRadius; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        TPoints points;
        float3 a = points.read(geometry,0), b = points.read(geometry,1), c = points.read(geometry,2);
        float3 normal = normalize(cross(b - a, c - a));
        bool front = dot(normal, direction) >= 0;
        float3 offset = normal * (front ? geometry.convexRadius : -geometry.convexRadius);
        DeviceFeaturePoint point;
        point.feature = front ? 0 : 1;
        point.point = a + offset; CollisionFaces[output] = point;
        point.point = (front ? b : c) + offset; CollisionFaces[output + 1] = point;
        point.point = (front ? c : b) + offset; CollisionFaces[output + 2] = point;
        return 3;
    }
};

typedef DeviceTriangleAt<DeviceCurrentGeometryPoints> DeviceTriangle;
typedef DeviceTriangleAt<DeviceStartGeometryPoints> DeviceStartingTriangle;

uint writeDeviceGeometryBodies(uint geometryIndex, float3 point, uint output, uint normalReference, uint sample = 0, bool temporary = false)
{
    DeviceGeometryReference reference = DeviceGeometryReferences[geometryIndex];
    uint indices[3] = { reference.firstBody, reference.secondBody, reference.thirdBody };
    float3 local[3] = { reference.firstLocalPoint, reference.secondLocalPoint, reference.thirdLocalPoint };
    float3 weights = float3(1, 0, 0);
    if (reference.kind == 1)
    {
        float3 first = deviceGeometryPoint(indices[0], local[0], sample);
        float3 second = deviceGeometryPoint(indices[1], local[1], sample);
        float3 edge = second - first;
        float parameter = saturate(dot(point - first, edge) / dot(edge, edge));
        weights = float3(1 - parameter, parameter, 0);
    }
    else if (reference.kind == 2)
    {
        float3 first = deviceGeometryPoint(indices[0], local[0], sample);
        float3 second = deviceGeometryPoint(indices[1], local[1], sample);
        float3 third = deviceGeometryPoint(indices[2], local[2], sample);
        closestDeviceTriangle(first - point, second - point, third - point, weights);
    }
    [loop] for (uint index = 0; index < reference.bodyCount; index++)
    {
        DeviceBodyState state = deviceGeometryBody(indices[index], sample);
        DeviceContactBody body;
        body.entity = uint2(state.entityIndex, state.entityGeneration);
        body.localPoint = reference.kind == 0
            ? rotateDeviceDirection(float4(-state.rotation.xyz, state.rotation.w), point - state.position) : local[index];
        body.weight = weights[index];
        body.stateIndex = indices[index];
        body.normalReference = normalReference;
        if (temporary) CollisionWorkBodies[output + index] = body;
        else CollisionContactBodies[output + index] = body;
    }
    return reference.bodyCount;
}
uint writeDeviceGeometryBodies(uint geometryIndex, float3 point, uint output)
{ return writeDeviceGeometryBodies(geometryIndex,point,output,0); }

void detectDeviceReferencedCollision<TKernel : IDeviceCollisionNarrowPhase>(uint work, TKernel kernel)
{
    detectDeviceCollision(work, kernel);
    if (work >= DetectionParameters.workItems) return;
    uint start = DetectionBinOffsets[DetectionParameters.bin];
    uint end = DetectionBinOffsets[DetectionParameters.bin + 1];
    uint outputStart = start - DetectionBinOffsets[DetectionParameters.firstBin];
    [loop] for (uint local = work; local < end - start; local += DetectionParameters.workItems)
    {
        uint index = outputStart + local;
        DeviceCollisionPair pair = DetectionPairs[start + local];
        DeviceGeometryInput first = DeviceGeometries[pair.firstGeometry];
        DeviceGeometryInput second = DeviceGeometries[pair.secondGeometry];
        uint count = CollisionContactCounts[index];
        [loop] for (uint point = 0; point < count; point++)
        {
            uint contactIndex = CollisionContactOffsets[index] + point;
            if(contactIndex>=DetectionParameters.contacts) continue;
            DeviceCollisionContact contact = CollisionContacts[contactIndex];
            float3 firstPoint = first.position + rotateDeviceDirection(first.rotation, contact.firstLocalPoint);
            float3 secondPoint = second.position + rotateDeviceDirection(second.rotation, contact.secondLocalPoint);
            uint firstCount = writeDeviceGeometryBodies(pair.firstGeometry, firstPoint,
                contactIndex * DetectionParameters.bodiesPerContact, kernel.getNormalReference(first));
            uint secondCount = writeDeviceGeometryBodies(pair.secondGeometry, secondPoint,
                contactIndex * DetectionParameters.bodiesPerContact + firstCount, kernel.getNormalReference(second));
            contact.firstBodyCount = int(firstCount);
            CollisionContacts[contactIndex] = contact;
            CollisionContactBodyCounts[contactIndex] = firstCount + secondCount;
        }
    }
}
#endif

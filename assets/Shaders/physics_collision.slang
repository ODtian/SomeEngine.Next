#ifndef PHYSICS_COLLISION
#define PHYSICS_COLLISION

#include "physics_geometry_types.slang"
#include "physics_collision_contact.slang"

struct DeviceCollisionPair
{
    uint firstEntity;
    uint firstGeneration;
    uint secondEntity;
    uint secondGeneration;
    uint firstGeometry;
    uint secondGeometry;
    uint firstBody;
    uint secondBody;
};

struct DeviceFeaturePoint { float3 point; int feature; };
struct DeviceSupportPoint
{
    float3 first;
    float3 second;
    int firstFeature;
    int secondFeature;
    float3 difference() { return first - second; }
};
struct DevicePenetrationFace
{
    int first;
    int second;
    int third;
    float3 normal;
    float distance;
};
struct DeviceGeometryDistance
{
    float3 firstPoint;
    float3 secondPoint;
    float3 normal;
    float separation;
    int firstFeature;
    int secondFeature;
};
struct DeviceCollisionDistance
{
    uint2 first;
    uint2 second;
    DeviceGeometryDistance distance;
};
struct DeviceDetectionParameters
{
    uint bin;
    uint pairCapacity;
    float tolerance;
    uint distanceIterations;
    uint penetrationIterations;
    uint faceCapacity;
    float contactDistance;
    uint workItems;
    uint bodiesPerContact;
};

ConstantBuffer<DeviceDetectionParameters> DetectionParameters;
RWStructuredBuffer<DeviceCollisionPair> DetectionPairs;
RWStructuredBuffer<uint> DetectionBinOffsets;
RWStructuredBuffer<DeviceGeometryInput> DeviceGeometries;
RWStructuredBuffer<DeviceCollisionDistance> CollisionDistances;
RWStructuredBuffer<DeviceCollisionContact> CollisionContacts;
RWStructuredBuffer<uint> CollisionContactCounts;
RWStructuredBuffer<DeviceSupportPoint> PenetrationVertices;
RWStructuredBuffer<DevicePenetrationFace> PenetrationFaces;
RWStructuredBuffer<int2> PenetrationEdges;
RWStructuredBuffer<DeviceFeaturePoint> CollisionFaces;
RWStructuredBuffer<DeviceFeaturePoint> ContactPolygon;
RWStructuredBuffer<DeviceFeaturePoint> ContactClip;
RWStructuredBuffer<DeviceGeometryDistance> ManifoldCandidates;

interface IDeviceConvex
{
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction);
    float radius(DeviceGeometryInput geometry);
    uint face(DeviceGeometryInput geometry, float3 direction, uint output);
};
struct DeviceGeometryPointDistance { float3 point; float3 normal; float distance; int feature; };
interface IDevicePointGeometry : IDeviceConvex
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point);
};

DeviceGeometryPointDistance deviceLocalPointDistance(DeviceGeometryInput geometry,
    float3 point, float3 normal, float distance, int feature)
{
    DeviceGeometryPointDistance result;
    result.point = geometry.position + rotateDeviceDirection(geometry.rotation, point);
    result.normal = rotateDeviceDirection(geometry.rotation, normal);
    result.distance = distance; result.feature = feature;
    return result;
}
interface IDeviceCollisionNarrowPhase
{
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second);
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4]);
};

struct DeviceSphere : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        float3 offset = point - geometry.position;
        float size = length(offset);
        DeviceGeometryPointDistance result;
        result.normal = size > 0 ? offset / size : float3(1,0,0);
        result.point = geometry.position + result.normal * geometry.convexRadius;
        result.distance = size - geometry.convexRadius; result.feature = 0;
        return result;
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        DeviceFeaturePoint result;
        result.point = geometry.position;
        result.feature = 0;
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return geometry.convexRadius; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        DeviceFeaturePoint point;
        point.point = geometry.position + normalize(direction) * geometry.convexRadius;
        point.feature = 0;
        CollisionFaces[output] = point;
        return 1;
    }
};

struct DeviceBox : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz,geometry.rotation.w), point-geometry.position);
        float3 outside = abs(local) - geometry.halfExtents;
        float3 closest = clamp(local,-geometry.halfExtents,geometry.halfExtents);
        float3 delta = local - closest;
        float size = length(delta);
        float3 normal;
        int axis;
        if (size > 0)
        {
            normal = delta / size; float3 absolute = abs(normal);
            axis = absolute.x >= absolute.y && absolute.x >= absolute.z ? 0 : absolute.y >= absolute.z ? 1 : 2;
        }
        else
        {
            axis = outside.x >= outside.y && outside.x >= outside.z ? 0 : outside.y >= outside.z ? 1 : 2;
            normal = 0; normal[axis] = local[axis] >= 0 ? 1 : -1;
            closest[axis] = normal[axis] * geometry.halfExtents[axis]; size = outside[axis];
        }
        return deviceLocalPointDistance(geometry,closest,normal,size,axis*2+(local[axis]>=0?1:0));
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction);
        float3 point = select(local >= 0, geometry.halfExtents, -geometry.halfExtents);
        DeviceFeaturePoint result;
        result.point = geometry.position + rotateDeviceDirection(geometry.rotation, point);
        result.feature = 8 + (point.x >= 0 ? 1 : 0) + (point.y >= 0 ? 2 : 0) + (point.z >= 0 ? 4 : 0);
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return 0; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction);
        float3 absolute = abs(local);
        int axis = absolute.x >= absolute.y && absolute.x >= absolute.z ? 0 : absolute.y >= absolute.z ? 1 : 2;
        float sign = local[axis] >= 0 ? 1 : -1;
        float3 center = 0;
        float3 first = 0;
        float3 second = 0;
        center[axis] = sign * geometry.halfExtents[axis];
        first[(axis + 1) % 3] = sign * geometry.halfExtents[(axis + 1) % 3];
        second[(axis + 2) % 3] = geometry.halfExtents[(axis + 2) % 3];
        DeviceFeaturePoint point;
        point.feature = axis * 2 + (sign > 0 ? 1 : 0);
        point.point = geometry.position + rotateDeviceDirection(geometry.rotation, center - first - second);
        CollisionFaces[output] = point;
        point.point = geometry.position + rotateDeviceDirection(geometry.rotation, center + first - second);
        CollisionFaces[output + 1] = point;
        point.point = geometry.position + rotateDeviceDirection(geometry.rotation, center + first + second);
        CollisionFaces[output + 2] = point;
        point.point = geometry.position + rotateDeviceDirection(geometry.rotation, center - first + second);
        CollisionFaces[output + 3] = point;
        return 4;
    }
};

struct DeviceCapsule : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz,geometry.rotation.w), point-geometry.position);
        float height = clamp(local.y,-geometry.halfHeight,geometry.halfHeight);
        float3 center = float3(0,height,0), offset = local-center;
        float size = length(offset); float3 normal = size > 0 ? offset/size : float3(1,0,0);
        int feature = height <= -geometry.halfHeight ? 0 : height >= geometry.halfHeight ? 1 : 2;
        return deviceLocalPointDistance(geometry,center+normal*geometry.convexRadius,normal,size-geometry.convexRadius,feature);
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        float3 axis = rotateDeviceDirection(geometry.rotation, float3(0, 1, 0));
        bool upper = dot(axis, direction) >= 0;
        DeviceFeaturePoint result;
        result.point = geometry.position + axis * (upper ? geometry.halfHeight : -geometry.halfHeight);
        result.feature = upper ? 1 : 0;
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return geometry.convexRadius; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        float3 axis = rotateDeviceDirection(geometry.rotation, float3(0, 1, 0));
        float3 normal = normalize(direction);
        DeviceFeaturePoint point;
        if (abs(dot(axis, normal)) <= 1e-5f)
        {
            point.feature = 2;
            point.point = geometry.position - axis * geometry.halfHeight + normal * geometry.convexRadius;
            CollisionFaces[output] = point;
            point.point = geometry.position + axis * geometry.halfHeight + normal * geometry.convexRadius;
            CollisionFaces[output + 1] = point;
            return 2;
        }
        point = support(geometry, direction);
        point.point += normal * geometry.convexRadius;
        CollisionFaces[output] = point;
        return 1;
    }
};

struct DeviceCylinder : IDevicePointGeometry
{
    DeviceGeometryPointDistance distanceToPoint(DeviceGeometryInput geometry, float3 point)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz,geometry.rotation.w), point-geometry.position);
        float size = length(local.xz); float3 radial = size > 0 ? float3(local.x,0,local.z)/size : float3(1,0,0);
        float radialDistance = size-geometry.radius, capDistance = abs(local.y)-geometry.halfHeight;
        float sign = local.y >= 0 ? 1 : -1;
        float3 closest, normal; float distance; int feature;
        if (radialDistance > 0 && capDistance > 0)
        {
            closest = radial*geometry.radius+float3(0,sign*geometry.halfHeight,0);
            float3 delta = local-closest; distance = length(delta); normal = delta/distance; feature = sign>0?3:4;
        }
        else if (radialDistance >= capDistance)
        { closest = radial*geometry.radius+float3(0,local.y,0); normal=radial; distance=radialDistance; feature=0; }
        else
        { closest = float3(local.x,sign*geometry.halfHeight,local.z); normal=float3(0,sign,0); distance=capDistance; feature=sign>0?1:2; }
        return deviceLocalPointDistance(geometry,closest,normal,distance,feature);
    }
    DeviceFeaturePoint support(DeviceGeometryInput geometry, float3 direction)
    {
        float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction);
        float radialLength = length(local.xz);
        float3 point = radialLength > 0 ? float3(local.x * geometry.radius / radialLength, 0,
            local.z * geometry.radius / radialLength) : float3(0);
        point.y = local.y >= 0 ? geometry.halfHeight : -geometry.halfHeight;
        DeviceFeaturePoint result;
        result.point = geometry.position + rotateDeviceDirection(geometry.rotation, point);
        result.feature = local.y >= 0 ? 1 : 2;
        return result;
    }
    float radius(DeviceGeometryInput geometry) { return 0; }
    uint face(DeviceGeometryInput geometry, float3 direction, uint output)
    {
        float3 local = normalize(rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), direction));
        DeviceFeaturePoint point;
        if (abs(local.y) >= 1 - 1e-5f)
        {
            float sign = local.y > 0 ? 1 : -1;
            float height = sign * geometry.halfHeight;
            point.feature = sign > 0 ? 1 : 2;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, float3(geometry.radius, height, 0));
            CollisionFaces[output] = point;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, float3(0, height, -sign * geometry.radius));
            CollisionFaces[output + 1] = point;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, float3(-geometry.radius, height, 0));
            CollisionFaces[output + 2] = point;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, float3(0, height, sign * geometry.radius));
            CollisionFaces[output + 3] = point;
            return 4;
        }
        if (abs(local.y) <= 1e-5f)
        {
            float3 radial = normalize(float3(local.x, 0, local.z)) * geometry.radius;
            point.feature = 0;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, radial - float3(0, geometry.halfHeight, 0));
            CollisionFaces[output] = point;
            point.point = geometry.position + rotateDeviceDirection(geometry.rotation, radial + float3(0, geometry.halfHeight, 0));
            CollisionFaces[output + 1] = point;
            return 2;
        }
        CollisionFaces[output] = support(geometry, direction);
        return 1;
    }
};

#include "physics_collision_simplex.slang"
#include "physics_collision_penetration.slang"
#include "physics_collision_manifolds.slang"

struct DeviceConvexCollision<TFirst : IDeviceConvex, TSecond : IDeviceConvex> : IDeviceCollisionNarrowPhase
{
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    {
        TFirst a;
        TSecond b;
        float3 direction = second.position - first.position;
        if (dot(direction, direction) == 0) direction = float3(1, 0, 0);
        DeviceSupportPoint simplex[4];
        simplex[0] = supportDevicePair<TFirst, TSecond>(first, second, direction);
        int count = 1;
        float4 weights = float4(1, 0, 0, 0);
        float3 closest = simplex[0].difference();
        float tolerance = DetectionParameters.tolerance;
        [loop] for (uint iteration = 0; iteration < DetectionParameters.distanceIterations; iteration++)
        {
            double squared = deviceCollisionDot(double3(closest), double3(closest));
            if (squared <= double(tolerance) * tolerance)
                return penetrationDevicePair<TFirst, TSecond>(work, first, second, simplex, count);
            DeviceSupportPoint support = supportDevicePair<TFirst, TSecond>(first, second, -closest);
            if (squared - deviceCollisionDot(double3(closest), double3(support.difference())) <= tolerance * sqrt(float(squared))) break;
            simplex[count++] = support;
            closest = reduceDeviceSimplex(simplex, count, weights);
        }
        float distance = length(closest);
        if (distance <= tolerance)
            return penetrationDevicePair<TFirst, TSecond>(work, first, second, simplex, count);
        float3 normal = -closest / distance;
        return witnessDeviceSimplex(simplex, count, weights, normal, distance, a.radius(first), b.radius(second));
    }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        return manifoldDevicePair<TFirst, TSecond>(work, first, second, distance, points);
    }
};

struct DeviceConvexPlaneCollision<TGeometry : IDeviceConvex> : IDeviceCollisionNarrowPhase
{
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    {
        TGeometry geometry;
        float3 normal = rotateDeviceDirection(second.rotation, second.planeNormal);
        DeviceFeaturePoint support = geometry.support(first, -normal);
        float3 point = support.point - normal * geometry.radius(first);
        float separation = dot(normal, point - second.position) - second.planeOffset;
        DeviceGeometryDistance result;
        result.firstPoint = point;
        result.secondPoint = point - normal * separation;
        result.normal = -normal;
        result.separation = separation;
        result.firstFeature = support.feature;
        result.secondFeature = 0;
        return result;
    }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        TGeometry geometry;
        uint face = work * 2 * DetectionParameters.faceCapacity;
        uint count = geometry.face(first, distance.normal, face);
        uint candidates = work * (2 * DetectionParameters.faceCapacity + 4);
        float3 normal = -distance.normal;
        uint contacts = 0;
        for (uint index = 0; index < count; index++)
        {
            DeviceFeaturePoint support = CollisionFaces[face + index];
            float separation = dot(normal, support.point - second.position) - second.planeOffset;
            if (separation > DetectionParameters.contactDistance + DetectionParameters.tolerance) continue;
            DeviceGeometryDistance contact;
            contact.firstPoint = support.point;
            contact.secondPoint = support.point - normal * separation;
            contact.normal = distance.normal;
            contact.separation = separation;
            contact.firstFeature = support.feature;
            contact.secondFeature = 0;
            ManifoldCandidates[candidates + contacts++] = contact;
        }
        return reduceDeviceManifold(work, contacts, points);
    }
};

struct DeviceReverseCollision<TKernel : IDeviceCollisionNarrowPhase> : IDeviceCollisionNarrowPhase
{
    TKernel kernel;
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    { return reverseDeviceDistance(kernel.distance(work, second, first)); }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        uint count = kernel.manifold(work, second, first, reverseDeviceDistance(distance), points);
        for (uint index = 0; index < count; index++) points[index] = reverseDeviceDistance(points[index]);
        return count;
    }
};

struct DeviceTypePairCollision<TKernel : IDeviceCollisionNarrowPhase> : IDeviceCollisionNarrowPhase
{
    uint firstType;
    TKernel kernel;
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    {
        bool reverse = first.geometryType != firstType;
        DeviceGeometryDistance result = kernel.distance(work, reverse ? second : first, reverse ? first : second);
        return reverse ? reverseDeviceDistance(result) : result;
    }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        bool reverse = first.geometryType != firstType;
        uint count = kernel.manifold(work, reverse ? second : first, reverse ? first : second,
            reverse ? reverseDeviceDistance(distance) : distance, points);
        if (reverse)
            for (uint index = 0; index < count; index++) points[index] = reverseDeviceDistance(points[index]);
        return count;
    }
};

void detectDeviceCollision<TKernel : IDeviceCollisionNarrowPhase>(uint work, TKernel kernel)
{
    if (work >= DetectionParameters.workItems) return;
    uint start = DetectionBinOffsets[DetectionParameters.bin];
    uint end = DetectionBinOffsets[DetectionParameters.bin + 1];
    [loop] for (uint index = work; index < end - start; index += DetectionParameters.workItems)
    {
        DeviceCollisionPair pair = DetectionPairs[start + index];
        DeviceCollisionDistance result;
        result.first = uint2(pair.firstEntity, pair.firstGeneration);
        result.second = uint2(pair.secondEntity, pair.secondGeneration);
        DeviceGeometryInput first = DeviceGeometries[pair.firstGeometry];
        DeviceGeometryInput second = DeviceGeometries[pair.secondGeometry];
        result.distance = kernel.distance(work, first, second);
        CollisionDistances[index] = result;
        DeviceGeometryDistance points[4];
        uint count = result.distance.separation <= DetectionParameters.contactDistance
            ? kernel.manifold(work, first, second, result.distance, points) : 0;
        CollisionContactCounts[index] = count;
        for (uint point = 0; point < count; point++)
        {
            DeviceGeometryDistance distance = points[point];
            DeviceCollisionContact contact;
            contact.first = result.first;
            contact.second = result.second;
            contact.firstFeature = distance.firstFeature;
            contact.secondFeature = distance.secondFeature;
            contact.pointIndex = int(point);
            contact.firstLocalPoint = rotateDeviceDirection(float4(-first.rotation.xyz, first.rotation.w),
                distance.firstPoint - first.position);
            contact.secondLocalPoint = rotateDeviceDirection(float4(-second.rotation.xyz, second.rotation.w),
                distance.secondPoint - second.position);
            contact.worldPoint = (distance.firstPoint + distance.secondPoint) * 0.5f;
            contact.normal = distance.normal;
            contact.separation = distance.separation;
            contact.firstBodyCount = 1;
            contact.predictionAlignment = 0;
            contact.prediction.enabled = 0;
            contact.prediction.padding = 0;
            contact.prediction.sampleTime = uint2(0, 0);
            CollisionContacts[index * 4 + point] = contact;
        }
    }
}

#endif

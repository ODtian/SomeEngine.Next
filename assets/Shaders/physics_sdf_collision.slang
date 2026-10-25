#ifndef PHYSICS_SDF_COLLISION
#define PHYSICS_SDF_COLLISION
#include "physics_geometry_references.slang"
#include "physics_sdf_data.slang"

struct DeviceSdfSampling { uint3 cells; uint rootIterations; float tolerance; };
struct DeviceSdfCollisionSettings { uint3 cells; uint rootIterations; float tolerance; uint supportIterations; };

DeviceGeometryPointDistance sampleDeviceSdf<TField : IDeviceSdfField>(DeviceGeometryInput geometry, float3 point)
{
    DeviceSdfInput input = DeviceSdfInputs[DeviceGeometryReferences[geometry.geometryIndex].fieldIndex];
    float3 local = rotateDeviceDirection(float4(-geometry.rotation.xyz, geometry.rotation.w), point - geometry.position) / input.scale;
    TField field;
    DeviceSdfSample sample = field.sample(input, local);
    float squared = dot(sample.gradient, sample.gradient);
    float3 direction = squared > 0 ? sample.gradient / sqrt(squared) : float3(1,0,0);
    DeviceGeometryPointDistance result;
    result.normal = rotateDeviceDirection(geometry.rotation, direction);
    result.distance = sample.distance * input.scale;
    result.point = point - result.normal * result.distance;
    result.feature = sample.feature;
    return result;
}

struct DeviceSdfContacts
{
    DeviceGeometryDistance best;
    DeviceGeometryDistance points[4];
    uint count;
    uint work;
    bool collect;
    float tolerance;
};
DeviceSdfContacts beginDeviceSdfContacts(uint work, bool collect, float tolerance)
{
    DeviceSdfContacts result;
    result.best.firstPoint = result.best.secondPoint = 0;
    result.best.normal = float3(1,0,0);
    result.best.firstFeature = result.best.secondFeature = 0;
    result.best.separation = 3.402823466e38f;
    result.count = 0; result.work = work; result.collect = collect; result.tolerance = tolerance;
    return result;
}
void addDeviceSdfContact(inout DeviceSdfContacts contacts, DeviceGeometryDistance candidate)
{
    if (candidate.separation < contacts.best.separation) contacts.best = candidate;
    if (!contacts.collect || candidate.separation > DetectionParameters.contactDistance + contacts.tolerance) return;
    for (uint index = 0; index < contacts.count; index++)
    {
        float3 a = candidate.firstPoint - contacts.points[index].firstPoint;
        float3 b = candidate.secondPoint - contacts.points[index].secondPoint;
        if (dot(a,a) > contacts.tolerance * contacts.tolerance || dot(b,b) > contacts.tolerance * contacts.tolerance) continue;
        if (candidate.separation < contacts.points[index].separation) contacts.points[index] = candidate;
        return;
    }
    if (contacts.count < 4) contacts.points[contacts.count++] = candidate;
    else
    {
        uint start = contacts.work * (2 * DetectionParameters.faceCapacity + 4);
        for (uint index = 0; index < 4; index++) ManifoldCandidates[start + index] = contacts.points[index];
        ManifoldCandidates[start + 4] = candidate;
        DeviceGeometryDistance reduced[4];
        reduceDeviceManifold(contacts.work, 5, reduced);
        for (uint index = 0; index < 4; index++) contacts.points[index] = reduced[index];
    }
}

DeviceGeometryPointDistance addDeviceConvexSdfPoint<TField : IDeviceSdfField>(DeviceFeaturePoint point,
    DeviceGeometryInput field, inout DeviceSdfContacts contacts)
{
    DeviceGeometryPointDistance surface = sampleDeviceSdf<TField>(field, point.point);
    DeviceGeometryDistance distance;
    distance.firstPoint = point.point; distance.secondPoint = surface.point;
    distance.normal = -surface.normal; distance.separation = surface.distance;
    distance.firstFeature = point.feature; distance.secondFeature = surface.feature;
    addDeviceSdfContact(contacts, distance);
    return surface;
}

interface IDeviceSdfSurfaceOutput { [mutating] void add(DeviceFeaturePoint point); };

DeviceFeaturePoint deviceSdfSurfacePoint(DeviceGeometryInput geometry, DeviceSdfInput input, float3 local, int feature)
{
    DeviceFeaturePoint point;
    point.point = geometry.position + rotateDeviceDirection(geometry.rotation, local * input.scale);
    point.feature = feature;
    return point;
}

void readDeviceSdfEdge<TField : IDeviceSdfField, TOutput : IDeviceSdfSurfaceOutput>(DeviceGeometryInput geometry,
    DeviceSdfInput input, DeviceSdfSampling sampling, float3 first, float3 second,
    DeviceSdfSample firstSample, inout TOutput output)
{
    TField field;
    DeviceSdfSample secondSample = field.sample(input, second);
    if (abs(firstSample.distance) <= sampling.tolerance) output.add(deviceSdfSurfacePoint(geometry,input,first,firstSample.feature));
    if (abs(secondSample.distance) <= sampling.tolerance) output.add(deviceSdfSurfacePoint(geometry,input,second,secondSample.feature));
    if (firstSample.distance * secondSample.distance >= 0) return;
    float3 root = (first + second) * .5f;
    DeviceSdfSample sample = firstSample;
    [loop] for (uint iteration = 0; iteration < sampling.rootIterations; iteration++)
    {
        root = (first + second) * .5f;
        sample = field.sample(input, root);
        if (abs(sample.distance) <= sampling.tolerance) break;
        if ((sample.distance < 0) == (firstSample.distance < 0)) { first = root; firstSample = sample; }
        else second = root;
    }
    output.add(deviceSdfSurfacePoint(geometry, input, root, sample.feature));
}

void readDeviceSdfSurface<TField : IDeviceSdfField, TOutput : IDeviceSdfSurfaceOutput>(DeviceGeometryInput geometry,
    DeviceSdfSampling sampling, inout TOutput output)
{
    DeviceSdfInput input = DeviceSdfInputs[DeviceGeometryReferences[geometry.geometryIndex].fieldIndex];
    float3 step = (input.max - input.min) / float3(sampling.cells);
    TField field;
    [loop] for (uint z = 0; z <= sampling.cells.z; z++)
    [loop] for (uint y = 0; y <= sampling.cells.y; y++)
    [loop] for (uint x = 0; x <= sampling.cells.x; x++)
    {
        float3 first = input.min + step * float3(x,y,z);
        DeviceSdfSample sample = field.sample(input, first);
        if (x < sampling.cells.x) readDeviceSdfEdge<TField,TOutput>(geometry,input,sampling,first,first+float3(step.x,0,0),sample,output);
        if (y < sampling.cells.y) readDeviceSdfEdge<TField,TOutput>(geometry,input,sampling,first,first+float3(0,step.y,0),sample,output);
        if (z < sampling.cells.z) readDeviceSdfEdge<TField,TOutput>(geometry,input,sampling,first,first+float3(0,0,step.z),sample,output);
    }
}

struct DeviceConvexSdfSurface<TGeometry : IDevicePointGeometry, TField : IDeviceSdfField> : IDeviceSdfSurfaceOutput
{
    DeviceGeometryInput convex;
    DeviceGeometryInput field;
    DeviceSdfContacts contacts;
    [mutating] void add(DeviceFeaturePoint point)
    {
        TGeometry geometry;
        DeviceGeometryPointDistance surface = geometry.distanceToPoint(convex,point.point);
        DeviceGeometryDistance distance;
        distance.firstPoint=surface.point;distance.secondPoint=point.point;distance.normal=surface.normal;
        distance.separation=surface.distance;distance.firstFeature=surface.feature;distance.secondFeature=point.feature;
        addDeviceSdfContact(contacts, distance);
        DeviceFeaturePoint projected;
        projected.point = distance.firstPoint; projected.feature = distance.firstFeature;
        addDeviceConvexSdfPoint<TField>(projected, field, contacts);
    }
};

struct DeviceSdfCollision<TGeometry : IDevicePointGeometry, TField : IDeviceSdfField> : IDeviceCollisionNarrowPhase
{
    DeviceSdfCollisionSettings settings;
    DeviceSdfContacts read(uint work, DeviceGeometryInput first, DeviceGeometryInput second, bool collect)
    {
        DeviceSdfContacts contacts = beginDeviceSdfContacts(work, collect, settings.tolerance);
        TGeometry geometry;
        for (int axis = 0; axis < 3; axis++)
        {
            float3 direction = 0; direction[axis] = 1;
            DeviceFeaturePoint point = geometry.support(first, direction); point.point += direction * geometry.radius(first);
            addDeviceConvexSdfPoint<TField>(point, second, contacts);
            point = geometry.support(first, -direction); point.point -= direction * geometry.radius(first);
            addDeviceConvexSdfPoint<TField>(point, second, contacts);
        }
        DeviceGeometryPointDistance surface = sampleDeviceSdf<TField>(second, first.position);
        float3 previous = first.position;
        [loop] for (uint iteration = 0; iteration < settings.supportIterations; iteration++)
        {
            DeviceFeaturePoint point = geometry.support(first, -surface.normal);
            point.point -= surface.normal * geometry.radius(first);
            surface = addDeviceConvexSdfPoint<TField>(point, second, contacts);
            float3 offset = point.point - previous;
            if (dot(offset,offset) <= settings.tolerance * settings.tolerance) break;
            previous = point.point;
        }
        uint start = work * 2 * DetectionParameters.faceCapacity;
        uint count = geometry.face(first, -surface.normal, start);
        for (uint index = 0; index < count; index++) addDeviceConvexSdfPoint<TField>(CollisionFaces[start+index], second, contacts);
        DeviceConvexSdfSurface<TGeometry,TField> output;
        output.convex = first; output.field = second; output.contacts = contacts;
        DeviceSdfSampling sampling;
        sampling.cells = settings.cells; sampling.rootIterations = settings.rootIterations; sampling.tolerance = settings.tolerance;
        readDeviceSdfSurface<TField,DeviceConvexSdfSurface<TGeometry,TField>>(second,sampling,output);
        return output.contacts;
    }
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    { return read(work,first,second,false).best; }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        DeviceSdfContacts contacts = read(work,first,second,true);
        for (uint index = 0; index < contacts.count; index++) points[index] = contacts.points[index];
        return contacts.count;
    }
};
struct DeviceFirstSdfSurface<TField : IDeviceSdfField> : IDeviceSdfSurfaceOutput
{
    DeviceGeometryInput other;
    DeviceSdfContacts contacts;
    [mutating] void add(DeviceFeaturePoint point)
    {
        DeviceGeometryPointDistance surface = sampleDeviceSdf<TField>(other, point.point);
        DeviceGeometryDistance distance;
        distance.firstPoint = point.point; distance.secondPoint = surface.point;
        distance.normal = -surface.normal; distance.separation = surface.distance;
        distance.firstFeature = point.feature; distance.secondFeature = surface.feature;
        addDeviceSdfContact(contacts, distance);
    }
};

struct DeviceSecondSdfSurface<TField : IDeviceSdfField> : IDeviceSdfSurfaceOutput
{
    DeviceGeometryInput other;
    DeviceSdfContacts contacts;
    [mutating] void add(DeviceFeaturePoint point)
    {
        DeviceGeometryPointDistance surface = sampleDeviceSdf<TField>(other, point.point);
        DeviceGeometryDistance distance;
        distance.firstPoint = surface.point; distance.secondPoint = point.point;
        distance.normal = surface.normal; distance.separation = surface.distance;
        distance.firstFeature = surface.feature; distance.secondFeature = point.feature;
        addDeviceSdfContact(contacts, distance);
    }
};

struct DeviceSdfFieldCollision<TFirst : IDeviceSdfField, TSecond : IDeviceSdfField> : IDeviceCollisionNarrowPhase
{
    DeviceSdfSampling firstSampling;
    DeviceSdfSampling secondSampling;
    float tolerance;
    DeviceSdfContacts read(uint work, DeviceGeometryInput first, DeviceGeometryInput second, bool collect)
    {
        DeviceFirstSdfSurface<TSecond> firstPoints;
        firstPoints.other = second;
        firstPoints.contacts = beginDeviceSdfContacts(work, collect, tolerance);
        readDeviceSdfSurface<TFirst, DeviceFirstSdfSurface<TSecond>>(first, firstSampling, firstPoints);
        DeviceSecondSdfSurface<TFirst> secondPoints;
        secondPoints.other = first;
        secondPoints.contacts = firstPoints.contacts;
        readDeviceSdfSurface<TSecond, DeviceSecondSdfSurface<TFirst>>(second, secondSampling, secondPoints);
        return secondPoints.contacts;
    }
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    { return read(work, first, second, false).best; }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        DeviceSdfContacts contacts = read(work, first, second, true);
        for (uint index = 0; index < contacts.count; index++) points[index] = contacts.points[index];
        return contacts.count;
    }
};
struct DeviceSdfPlaneSurface : IDeviceSdfSurfaceOutput
{
    DeviceGeometryInput plane;
    DeviceSdfContacts contacts;
    [mutating] void add(DeviceFeaturePoint point)
    {
        float3 normal = rotateDeviceDirection(plane.rotation, plane.planeNormal);
        float separation = dot(normal, point.point - plane.position) - plane.planeOffset;
        DeviceGeometryDistance distance;
        distance.firstPoint = point.point;
        distance.secondPoint = point.point - normal * separation;
        distance.normal = -normal;
        distance.separation = separation;
        distance.firstFeature = point.feature;
        distance.secondFeature = 0;
        addDeviceSdfContact(contacts, distance);
    }
};

struct DeviceSdfPlaneCollision<TField : IDeviceSdfField> : IDeviceCollisionNarrowPhase
{
    DeviceSdfSampling sampling;
    float tolerance;
    DeviceSdfContacts read(uint work, DeviceGeometryInput first, DeviceGeometryInput second, bool collect)
    {
        DeviceSdfPlaneSurface output;
        output.plane = second;
        output.contacts = beginDeviceSdfContacts(work, collect, tolerance);
        readDeviceSdfSurface<TField, DeviceSdfPlaneSurface>(first, sampling, output);
        return output.contacts;
    }
    DeviceGeometryDistance distance(uint work, DeviceGeometryInput first, DeviceGeometryInput second)
    { return read(work, first, second, false).best; }
    uint manifold(uint work, DeviceGeometryInput first, DeviceGeometryInput second,
        DeviceGeometryDistance distance, out DeviceGeometryDistance points[4])
    {
        DeviceSdfContacts contacts = read(work, first, second, true);
        for (uint index = 0; index < contacts.count; index++) points[index] = contacts.points[index];
        return contacts.count;
    }
};
#endif

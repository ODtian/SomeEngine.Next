#ifndef PHYSICS_RIGID_GEOMETRY_MOTION
#define PHYSICS_RIGID_GEOMETRY_MOTION
#include "physics_geometry_update.slang"
#include "physics_constraint_math.slang"

DeviceGeometryInput getDeviceRigidGeometryPose(DeviceGeometryInput geometry, DeviceBodyState body)
{
    geometry.position = body.position;
    geometry.rotation = body.rotation;
    return geometry;
}

struct DeviceRigidMotionUpdate<TBounds : IDeviceGeometryBounds> : IDeviceGeometryUpdate
{
    DeviceGeometryBounds update(uint index, inout DeviceGeometryInput geometry)
    {
        DeviceBodyState body = DeviceBodyStates[DeviceGeometryReferences[index].firstBody];
        DeviceBodyState start = deviceBodyStart(body);
        DeviceBodyState predicted = body;
        predicted.rotation = body.predictedRotation;
        bodySetCenterOfMass(predicted, body.predictedCenterOfMass);
        TBounds shape;
        DeviceGeometryBounds first = shape.getBounds(getDeviceRigidGeometryPose(geometry, start));
        DeviceGeometryBounds last = shape.getBounds(getDeviceRigidGeometryPose(geometry, predicted));
        geometry = getDeviceRigidGeometryPose(geometry, body);
        DeviceGeometryBounds current = shape.getBounds(geometry);
        DeviceGeometryBounds result;
        result.min = min(first.min, min(last.min, current.min));
        result.max = max(first.max, max(last.max, current.max));
        if (body.motion != 0 && any(body.angularVelocity != 0))
        {
            float3 farthest = max(abs(first.min - body.startCenterOfMass), abs(first.max - body.startCenterOfMass));
            float radius = length(farthest);
            result.min = min(result.min, min(body.startCenterOfMass, body.predictedCenterOfMass) - radius);
            result.max = max(result.max, max(body.startCenterOfMass, body.predictedCenterOfMass) + radius);
        }
        return result;
    }
};
#endif

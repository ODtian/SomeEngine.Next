#ifndef PHYSICS_BODY_PREDICTION
#define PHYSICS_BODY_PREDICTION
#include "physics_body_step.slang"

DeviceBodyState predictDeviceBodyState(DeviceBodyState body, DeviceBodyStep step, bool advance, bool resume)
{
    if (!resume)
    {
        body.startCenterOfMass = bodyCenterOfMass(body);
        body.startRotation = body.rotation;
    }
    body.number = step.number;
    body.substep = int(step.substep);
    body.time = step.time;
    if (advance)
    {
        if (body.motion == 2) body.linearVelocity += step.acceleration * step.duration;
        if (body.motion != 0)
        {
            body.rotation = bodyIntegrateRotation(body.rotation, body.angularVelocity, step.duration);
            bodySetCenterOfMass(body, body.startCenterOfMass + body.linearVelocity * step.duration);
        }
    }
    body.predictedCenterOfMass = bodyCenterOfMass(body);
    body.predictedRotation = body.rotation;
    return body;
}

DeviceBodyState updateDeviceBodyVelocityState(DeviceBodyState body, float duration)
{
    if (body.motion == 0) return body;
    body.linearVelocity = (bodyCenterOfMass(body) - body.startCenterOfMass) / duration;
    body.angularVelocity = bodyRotationVelocity(body.startRotation, body.rotation, duration);
    return body;
}
#endif

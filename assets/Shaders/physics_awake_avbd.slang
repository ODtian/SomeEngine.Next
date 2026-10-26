#ifndef PHYSICS_AWAKE_AVBD
#define PHYSICS_AWAKE_AVBD
#include "physics_avbd.slang"
#include "physics_awake_bodies.slang"

struct DeviceAwakeAvbdConstraints<T : IDeviceAvbdConstraints> : IDeviceAvbdConstraints
{
    T inner;
    uint count() { return inner.count(); }
    bool active(uint row) { return inner.active(row); }
    uint bodyCount(uint row) { return inner.bodyCount(row); }
    uint body(uint row,uint end) { return inner.body(row,end); }
    bool writes(uint row,uint end) { return inner.writes(row,end); }
    uint groupCount() { return inner.groupCount(); }
    uint group(uint row) { return inner.group(row); }
    void initialize(uint row,DeviceAvbdStep context)
    { if (deviceConstraintHasAwakeBody(row,inner)) inner.initialize(row,context); }
    void accumulate(uint row,uint body,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    { inner.accumulate(row,body,context,equation); }
    void updateDuals(uint row,DeviceAvbdStep context) { inner.updateDuals(row,context); }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation)
    { inner.adjust(body,context,equation,translation,rotation); }
};
#endif

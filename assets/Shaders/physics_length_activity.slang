#ifndef PHYSICS_LENGTH_ACTIVITY
#define PHYSICS_LENGTH_ACTIVITY
#include "physics_body_activity.slang"
#include "physics_length_graph.slang"
struct DeviceLengthActivity : IDeviceConstraintActivity
{
    DeviceLengthBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceLengthConstraint constraint=DeviceLengths[row];
        float lengthError=length(DeviceBodyStates[constraint.secondBody].position-
            DeviceBodyStates[constraint.firstBody].position)-constraint.restLength;
        return float2(abs(lengthError),0);
    }
};
#endif

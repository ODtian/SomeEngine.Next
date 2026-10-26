#ifndef PHYSICS_CONSTRAINT_ACTIVITY
#define PHYSICS_CONSTRAINT_ACTIVITY
#include "physics_body_activity.slang"

struct DeviceConstraintActivityGroups<TFirst : IDeviceConstraintActivity,TSecond : IDeviceConstraintActivity> : IDeviceConstraintActivity
{
    DeviceConstraintBodyGroups<TFirst,TSecond> participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    {
        uint firstCount=participants.first.count();
        return row<firstCount?participants.first.entity(row):participants.second.entity(row-firstCount);
    }
    float2 error(uint row,DeviceActivityStep context)
    {
        uint firstCount=participants.first.count();
        return row<firstCount?participants.first.error(row,context):participants.second.error(row-firstCount,context);
    }
};
#endif

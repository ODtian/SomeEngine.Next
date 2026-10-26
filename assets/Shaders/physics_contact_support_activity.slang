#ifndef PHYSICS_CONTACT_SUPPORT_ACTIVITY
#define PHYSICS_CONTACT_SUPPORT_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_contact_activity.slang"
#include "physics_contact_support_geometry.slang"

struct DeviceSupportBodyActivity : IDeviceConstraintActivity
{
    DeviceSupportBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceBodyStates[row].entityIndex,DeviceBodyStates[row].entityGeneration); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceBodySupportState support=DeviceSupportBodyStates[row];
        float height=dot(bodyCenterOfMass(DeviceBodyStates[row]),ContactSupportSettings.up);
        return float2(support.enabled!=0?max(0,support.minimumHeight-height):0,0);
    }
};
struct DeviceSupportedContactActivity : IDeviceConstraintActivity
{
    DeviceContactActivity ordinary;
    uint count() { return ordinary.count(); }
    bool active(uint row) { return ordinary.active(row); }
    uint bodyCount(uint row) { return ordinary.bodyCount(row); }
    uint body(uint row,uint end) { return ordinary.body(row,end); }
    bool writes(uint row,uint end) { return ordinary.writes(row,end); }
    uint groupCount() { return ordinary.groupCount(); }
    uint group(uint row) { return ordinary.group(row); }
    uint2 entity(uint row) { return ordinary.entity(row); }
    override uint2 sourceVersions(uint row) { return ordinary.sourceVersions(row); }
    float2 error(uint row,DeviceActivityStep context)
    {
        float2 result=ordinary.error(row,context);
        if(deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactSupportSelection[row]))
        {
            DeviceContactSupportState support=DeviceSupportContactStates[row];
            if(support.enabled!=0)
            {
                float height=dot(deviceSupportSidePoint(DeviceContacts[row],support.upperIsSecond!=0,false),ContactSupportSettings.up);
                result.x=max(result.x,max(0,support.minimumHeight-height));
            }
        }
        return result;
    }
};
#endif

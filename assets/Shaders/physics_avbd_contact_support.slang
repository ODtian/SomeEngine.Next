#ifndef PHYSICS_AVBD_CONTACT_SUPPORT
#define PHYSICS_AVBD_CONTACT_SUPPORT
#include "physics_contact_support_solve.slang"
float3 deviceContactSupportForce(uint row)
{
    return deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactSupportSelection[row])
        ?DeviceSupportContactStates[row].extraForceOnSecond:float3(0);
}
struct DeviceSupportedContactAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceContactAvbdConstraints ordinary;
    uint count() { return ordinary.count(); }
    bool active(uint row) { return ordinary.active(row); }
    uint bodyCount(uint row) { return ordinary.bodyCount(row); }
    uint body(uint row,uint end) { return ordinary.body(row,end); }
    bool writes(uint row,uint end) { return ordinary.writes(row,end); }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context) { ordinary.initialize(row,context); }
    void accumulate(uint row,uint body,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    { deviceAccumulateAvbdContact(row,body,context,deviceContactSupportForce(row),equation); }
    void updateDuals(uint row,DeviceAvbdStep context) { deviceUpdateAvbdContact(row,context,deviceContactSupportForce(row)); }
    override bool adjustsBody() { return ordinary.adjustsBody(); }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
struct DeviceContactSupportAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceSupportBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return 1; }
    uint body(uint row,uint end) { return row; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context) { DeviceSupportBodyStates[row]=(DeviceBodySupportState)0; }
    void accumulate(uint row,uint body,DeviceAvbdStep context,inout DeviceAvbdEquation equation) { }
    void updateDuals(uint row,DeviceAvbdStep context) { }
    override bool adjustsBody() { return true; }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation)
    { correctDeviceContactSupport(body,context,equation,translation,rotation); }
};
#endif

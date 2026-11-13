#ifndef PHYSICS_CONTACT_VALUES
#define PHYSICS_CONTACT_VALUES
#include "physics_body_math.slang"
#include "physics_contact_columns.slang"
bool deviceContactSelected(uint row)
{
    if(row>=DeviceContactCounts[1]) return false;
    uint start=DeviceContactBodyStarts[row], count=DeviceContactBodyCounts[row];
    for(uint end=0;end<count;end++)
    {
        DeviceContactBody link=loadDeviceContactBody(start+end);
        DeviceBodyState body=DeviceBodyStates[link.stateIndex];
        if(any(uint2(body.entityIndex,body.entityGeneration)!=link.entity)) return false;
    }
    return true;
}
uint deviceSelectedContactRow(uint work)
{ return work<DeviceContactCounts[1]?work:0xffffffff; }
#endif

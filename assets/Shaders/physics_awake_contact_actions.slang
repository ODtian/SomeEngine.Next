#ifndef PHYSICS_AWAKE_CONTACT_ACTIONS
#define PHYSICS_AWAKE_CONTACT_ACTIONS
#include "physics_contact_actions.slang"
#include "physics_awake_bodies.slang"

struct DeviceAwakeContactResponseActions<TSelection : IDeviceBodySelection> : IDeviceContactResponseActions
{
    DeviceContactResponseActions<TSelection> inner;
    uint count() { return inner.count(); }
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row,DeviceContactStep context)
    {
        DeviceContactBodies bodies;
        if (deviceConstraintHasAwakeBody(row,bodies)) inner.initialize(row,context);
    }
    void applyRestitution(uint row,DeviceContactStep context) { inner.applyRestitution(row,context); }
};
#endif

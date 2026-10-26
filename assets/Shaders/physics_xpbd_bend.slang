#include "physics_xpbd_selection.slang"
#include "physics_xpbd_bend_math.slang"
struct DeviceXpbdBend : IDeviceXpbdProjection
{
    DeviceXpbdBendProjection<DeviceSelectedBodies> inner;
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row, DeviceXpbdStep context) { inner.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { inner.project(row, context); }
};

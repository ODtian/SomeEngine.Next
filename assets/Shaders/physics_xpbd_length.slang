#include "physics_xpbd_selection.slang"
#include "physics_xpbd_length_math.slang"
struct DeviceXpbdLength : IDeviceXpbdProjection
{
    DeviceXpbdLengthProjection<DeviceSelectedBodies> inner;
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row, DeviceXpbdStep context) { inner.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { inner.project(row, context); }
};

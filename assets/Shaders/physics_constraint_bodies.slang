#ifndef PHYSICS_CONSTRAINT_BODIES
#define PHYSICS_CONSTRAINT_BODIES
#include "physics_constraint_selection.slang"
interface IDeviceConstraintBodies
{
    uint count(); bool active(uint row);
    uint bodyCount(uint row); uint body(uint row, uint end); bool writes(uint row, uint end);
    uint groupCount(); uint group(uint row);
};
struct DeviceNoConstraintBodies : IDeviceConstraintBodies
{
    uint count() { return 0; }
    bool active(uint row) { return false; }
    uint bodyCount(uint row) { return 0; }
    uint body(uint row,uint end) { return 0; }
    bool writes(uint row,uint end) { return false; }
    uint groupCount() { return 0; }
    uint group(uint row) { return 0; }
};
struct DeviceConstraintBodyGroups<TFirst : IDeviceConstraintBodies, TSecond : IDeviceConstraintBodies> : IDeviceConstraintBodies
{
    TFirst first; TSecond second;
    uint count() { return first.count() + second.count(); }
    bool active(uint row) { return row < first.count() ? first.active(row) : second.active(row - first.count()); }
    uint bodyCount(uint row) { return row < first.count() ? first.bodyCount(row) : second.bodyCount(row - first.count()); }
    uint body(uint row, uint end) { return row < first.count() ? first.body(row, end) : second.body(row - first.count(), end); }
    bool writes(uint row, uint end) { return row < first.count() ? first.writes(row, end) : second.writes(row - first.count(), end); }
    uint groupCount() { return first.groupCount() + second.groupCount(); }
    uint group(uint row) { return row < first.count() ? first.group(row) : first.groupCount() + second.group(row - first.count()); }
};
#endif

#ifndef PHYSICS_XPBD_GRAPH
#define PHYSICS_XPBD_GRAPH
#include "physics_constraint_graph.slang"
#include "physics_dispatch.slang"
#include "physics_xpbd_types.slang"
interface IDeviceXpbdConstraints : IDeviceConstraintBodies
{
    void initialize(uint row, DeviceXpbdStep context);
    void project(uint row, DeviceXpbdStep context);
};
struct DeviceXpbdConstraintRows<TBodies : IDeviceConstraintBodies, TProjection : IDeviceXpbdProjection> : IDeviceXpbdConstraints
{
    TBodies bodies; TProjection projection;
    uint count() { return bodies.count(); }
    bool active(uint row) { return bodies.active(row) && projection.active(row); }
    uint bodyCount(uint row) { return bodies.bodyCount(row); }
    uint body(uint row, uint end) { return bodies.body(row, end); }
    bool writes(uint row, uint end) { return bodies.writes(row, end); }
    uint groupCount() { return bodies.groupCount(); }
    uint group(uint row) { return bodies.group(row); }
    void initialize(uint row, DeviceXpbdStep context) { projection.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { projection.project(row, context); }
};
struct DeviceXpbdConstraintGroups<TFirst : IDeviceXpbdConstraints, TSecond : IDeviceXpbdConstraints> : IDeviceXpbdConstraints
{
    TFirst first; TSecond second;
    uint count() { return first.count() + second.count(); }
    bool active(uint row) { return row < first.count() ? first.active(row) : second.active(row - first.count()); }
    uint bodyCount(uint row) { return row < first.count() ? first.bodyCount(row) : second.bodyCount(row - first.count()); }
    uint body(uint row, uint end) { return row < first.count() ? first.body(row, end) : second.body(row - first.count(), end); }
    bool writes(uint row, uint end) { return row < first.count() ? first.writes(row, end) : second.writes(row - first.count(), end); }
    uint groupCount() { return first.groupCount() + second.groupCount(); }
    uint group(uint row) { return row < first.count() ? first.group(row) : first.groupCount() + second.group(row - first.count()); }
    void initialize(uint row, DeviceXpbdStep context)
    { if (row < first.count()) first.initialize(row, context); else second.initialize(row - first.count(), context); }
    void project(uint row, DeviceXpbdStep context)
    { if (row < first.count()) first.project(row, context); else second.project(row - first.count(), context); }
};
struct DeviceXpbdGraphStep
{
    DeviceXpbdStep step;
    uint stage; uint rowsPerBlock; uint threadsPerGroup; uint padding;
};
uniform DeviceXpbdGraphStep XpbdGraphStep;
#ifdef PHYSICS_XPBD_INDIRECT_DISPATCH
RWStructuredBuffer<uint> DeviceXpbdDispatchArguments;
void writeDeviceXpbdDispatchArguments(uint stage)
{
    if (stage >= ConstraintGraphCounts[2]) return;
    uint count = ConstraintGraphRowRanges[stage].y;
    uint groups = (count + XpbdGraphStep.rowsPerBlock - 1) / XpbdGraphStep.rowsPerBlock;
    uint first = stage * 3;
    DeviceXpbdDispatchArguments[first] = min(groups, DevicePhysicsGroupsPerRow);
    DeviceXpbdDispatchArguments[first + 1] = (groups + DevicePhysicsGroupsPerRow - 1) / DevicePhysicsGroupsPerRow;
    DeviceXpbdDispatchArguments[first + 2] = 1;
}
#endif
void initializeDeviceXpbdGraph<T : IDeviceXpbdConstraints>(uint block, uint thread, T rows)
{
    for (uint offset = thread; offset < XpbdGraphStep.rowsPerBlock; offset += XpbdGraphStep.threadsPerGroup)
    {
        uint row = block * XpbdGraphStep.rowsPerBlock + offset;
        if (row < rows.count() && rows.active(row) && deviceGraphRowActive(ConstraintGraphRows[row])) rows.initialize(row, XpbdGraphStep.step);
    }
}
void projectDeviceXpbdGraph<T : IDeviceXpbdConstraints>(uint block, uint thread, T rows)
{
    uint2 range = ConstraintGraphRowRanges[XpbdGraphStep.stage];
    for (uint offset = thread; offset < XpbdGraphStep.rowsPerBlock; offset += XpbdGraphStep.threadsPerGroup)
    {
        uint index = block * XpbdGraphStep.rowsPerBlock + offset;
        if (index >= range.y) continue;
        uint row = ConstraintGraphRowOrder[range.x + index];
        if (rows.active(row)) rows.project(row, XpbdGraphStep.step);
    }
}
#endif

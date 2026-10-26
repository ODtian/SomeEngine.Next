#ifndef PHYSICS_ELASTIC_DATA
#define PHYSICS_ELASTIC_DATA
#include "physics_constraint_graph.slang"
struct DeviceElasticConstraint
{
    uint firstBody; uint secondBody; float linearStiffness; float angularStiffness;
    float3 firstAnchor; uint padding0;
    float3 secondAnchor; uint padding1;
    float4 restRotation;
};
RWStructuredBuffer<DeviceElasticConstraint> DeviceElasticConstraints;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceElasticConstraintsMetadata;
StructuredBuffer<uint2> DeviceElasticConstraintsSelection;
struct DeviceElasticBodies : IDeviceConstraintBodies
{
    uint count() { uint count,stride; DeviceElasticConstraints.GetDimensions(count,stride); return count; }
    bool active(uint row) { return deviceConstraintSelected(DeviceElasticConstraintsMetadata[row], DeviceElasticConstraintsSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return end == 0 ? DeviceElasticConstraints[row].firstBody : DeviceElasticConstraints[row].secondBody; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif

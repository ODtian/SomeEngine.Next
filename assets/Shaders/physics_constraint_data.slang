#ifndef PHYSICS_CONSTRAINT_DATA
#define PHYSICS_CONSTRAINT_DATA
struct DeviceConstraintMetadata
{
    uint entityIndex; uint entityGeneration; uint active; uint inputVersion;
};
struct DeviceLengthConstraint
{
    uint firstBody; uint secondBody; float restLength; float compliance;
};
struct DeviceBendConstraint
{
    uint firstBody; uint middleBody; uint lastBody; uint padding0;
    float minimumCosine; float compliance; uint2 padding1;
};
#endif

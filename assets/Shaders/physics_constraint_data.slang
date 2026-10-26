#ifndef PHYSICS_CONSTRAINT_DATA
#define PHYSICS_CONSTRAINT_DATA
struct DeviceConstraintMetadata
{
    uint entityIndex; uint entityGeneration; uint active; uint padding;
};
struct DeviceLengthConstraint
{
    uint firstBody; uint secondBody; float restLength; float compliance;
};
#endif

#ifndef PHYSICS_BEND_DATA
#define PHYSICS_BEND_DATA
#include "physics_constraint_data.slang"
RWStructuredBuffer<DeviceBendConstraint, ScalarDataLayout> DeviceBends;
RWStructuredBuffer<DeviceConstraintMetadata, ScalarDataLayout> DeviceBendsMetadata;
#endif

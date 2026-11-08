#ifndef PHYSICS_LENGTH_DATA
#define PHYSICS_LENGTH_DATA
#include "physics_constraint_data.slang"
RWStructuredBuffer<DeviceLengthConstraint, ScalarDataLayout> DeviceLengths;
RWStructuredBuffer<DeviceConstraintMetadata, ScalarDataLayout> DeviceLengthsMetadata;
#endif

#ifndef PHYSICS_BODY_INPUT_DATA
#define PHYSICS_BODY_INPUT_DATA
#include "physics_body_input_types.slang"
struct DeviceBodyInputParameters { uint count; float duration; uint2 padding; };
ConstantBuffer<DeviceBodyInputParameters, ScalarDataLayout> BodyInputParameters;
StructuredBuffer<DeviceBodyInput, ScalarDataLayout> DeviceBodyInputs;
#endif

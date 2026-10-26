#ifndef PHYSICS_BODY_INPUT_DATA
#define PHYSICS_BODY_INPUT_DATA
#include "physics_body_input_types.slang"
struct DeviceBodyInputParameters { uint count; uint3 padding; };
ConstantBuffer<DeviceBodyInputParameters> BodyInputParameters;
StructuredBuffer<DeviceBodyInput> DeviceBodyInputs;
#endif

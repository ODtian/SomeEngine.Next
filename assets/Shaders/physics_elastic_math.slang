// SPDX-License-Identifier: Apache-2.0
// Elastic coordinates follow the shared AvbdElasticConnectionMath and Newton v1.7.0.
#ifndef PHYSICS_ELASTIC_MATH
#define PHYSICS_ELASTIC_MATH
#include "physics_body_math.slang"
#include "physics_elastic_data.slang"
float3 deviceElasticLinearError(DeviceElasticConstraint constraint,DeviceBodyState first,DeviceBodyState second)
{ return first.position+bodyRotate(first.rotation,constraint.firstAnchor)-second.position-bodyRotate(second.rotation,constraint.secondAnchor); }
float3 deviceElasticAngularError(DeviceElasticConstraint constraint,float4 first,float4 second)
{ return bodyRotationVector(bodyRotationMultiply(bodyRotationMultiply(bodyRotationConjugate(first),second),bodyRotationConjugate(constraint.restRotation))); }
#endif

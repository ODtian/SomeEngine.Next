using SomeEngine.Graphics;

namespace DefaultGame;

internal sealed partial class PhysicsSceneShaders
{
    internal ComputePipelineDesc CrowdMotorTargets() => Compile("crowd_motor_targets", """
        #include "physics_motor_data.slang"
        #include "physics_body_math.slang"
        struct CrowdMotorInput
        {
            float4x4 modelToWorld;
            DeviceMotorConstraint parameters;
            float3 referencePosition; uint modelStart;
            uint sourceBone; uint targetBone; uint motorRow; uint inputVersion;
        };
        struct CrowdMotorStep
        {
            uint count; uint reset; float duration; uint padding0;
            uint2 endTime; uint2 padding1;
        };
        uniform CrowdMotorStep CrowdMotorTime;
        StructuredBuffer<CrowdMotorInput> CrowdMotorInputs;
        RWStructuredBuffer<float4x4> CrowdMotorModels;
        [shader("compute")][numthreads(64,1,1)]
        void Write(uint3 id:SV_DispatchThreadID)
        {
            uint index=devicePhysicsThreadIndex(id,64);
            if(index>=CrowdMotorTime.count) return;
            CrowdMotorInput input=CrowdMotorInputs[index];
            DeviceMotorConstraint motor=input.parameters;
            DeviceMotorConstraint previous=DeviceMotors[input.motorRow];
            float3 hand=mul(float4(CrowdMotorModels[input.modelStart+input.targetBone][3].xyz,1),input.modelToWorld).xyz;
            float3 goal=hand;
            if(motor.space==0)
            {
                DeviceJointConstraint joint=DeviceJoints[motor.jointRow];
                float3 root=mul(float4(CrowdMotorModels[input.modelStart+input.sourceBone][3].xyz,1),input.modelToWorld).xyz;
                float4 frame=bodyRotationMultiply(DeviceBodyStates[joint.sourceBody].rotation,joint.sourceRotation);
                goal=bodyRotate(bodyRotationConjugate(frame),hand-root);
            }
            float elapsed=float(asdouble(CrowdMotorTime.endTime.x,CrowdMotorTime.endTime.y)-asdouble(motor.targetTime.x,motor.targetTime.y));
            motor.targetPosition+=motor.targetLinearVelocity*elapsed+goal-input.referencePosition;
            motor.targetRotation=bodyIntegrateRotation(motor.targetRotation,motor.targetAngularVelocity,elapsed);
            if(CrowdMotorTime.reset==0)
                motor.targetLinearVelocity=(motor.targetPosition-previous.targetPosition)/CrowdMotorTime.duration;
            motor.targetTime=CrowdMotorTime.endTime;
            DeviceMotors[input.motorRow]=motor;
            DeviceConstraintMetadata metadata=DeviceMotorsMetadata[input.motorRow];
            metadata.inputVersion=input.inputVersion;
            DeviceMotorsMetadata[input.motorRow]=metadata;
        }
        """, "Write")[0];
}

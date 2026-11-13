using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>The original joint frames, limits, stiffness and derived body references.</summary>
[StructLayout(LayoutKind.Explicit, Size = 208)]
public struct DeviceJointConstraint
{
    [FieldOffset(0)] public uint SourceBody;
    [FieldOffset(4)] public uint TargetBody;
    [FieldOffset(16)] public Vector3 SourcePosition;
    [FieldOffset(32)] public Quaternion SourceRotation;
    [FieldOffset(48)] public Vector3 TargetPosition;
    [FieldOffset(64)] public Quaternion TargetRotation;
    [FieldOffset(80)] public uint LinearXMotion;
    [FieldOffset(84)] public uint LinearYMotion;
    [FieldOffset(88)] public uint LinearZMotion;
    [FieldOffset(96)] public uint AngularXMotion;
    [FieldOffset(100)] public uint AngularYMotion;
    [FieldOffset(104)] public uint AngularZMotion;
    [FieldOffset(112)] public Vector3 LinearMinimum;
    [FieldOffset(128)] public Vector3 LinearMaximum;
    [FieldOffset(144)] public Vector3 AngularMinimum;
    [FieldOffset(160)] public Vector3 AngularMaximum;
    [FieldOffset(176)] public Vector3 LinearStiffness;
    [FieldOffset(192)] public Vector3 AngularStiffness;
}

public readonly struct DeviceJointSource : IDeviceConstraintSource<JointConstraint, DeviceJointConstraint>
{
    public static void Collect(in JointConstraint component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<RelationEndpoints<JointConstraint>>()];
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => chunk.WasWritten<RelationEndpoints<JointConstraint>>(since);
    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceJointConstraint> output)
    {
        ReadOnlySpan<JointConstraint> values = chunk.Read<JointConstraint>().Slice(start, output.Length);
        ReadOnlySpan<RelationEndpoints<JointConstraint>> endpoints = chunk.Read<RelationEndpoints<JointConstraint>>().Slice(start, output.Length);
        for (int item = 0; item < output.Length; item++)
        {
            ref readonly JointConstraint component = ref values[item];
            RelationEndpoints<JointConstraint> ends = endpoints[item];
            output[item] = new()
            {
                SourceBody = (uint)bodies.Read(ends.Source).Index,
                TargetBody = (uint)bodies.Read(ends.Target).Index,
                SourcePosition = component.SourceFrame.Position,
                SourceRotation = component.SourceFrame.Rotation,
                TargetPosition = component.TargetFrame.Position,
                TargetRotation = component.TargetFrame.Rotation,
                LinearXMotion = (uint)component.LinearX.Motion,
                LinearYMotion = (uint)component.LinearY.Motion,
                LinearZMotion = (uint)component.LinearZ.Motion,
                AngularXMotion = (uint)component.AngularX.Motion,
                AngularYMotion = (uint)component.AngularY.Motion,
                AngularZMotion = (uint)component.AngularZ.Motion,
                LinearMinimum = new(component.LinearX.Minimum, component.LinearY.Minimum, component.LinearZ.Minimum),
                LinearMaximum = new(component.LinearX.Maximum, component.LinearY.Maximum, component.LinearZ.Maximum),
                AngularMinimum = new(component.AngularX.Minimum, component.AngularY.Minimum, component.AngularZ.Minimum),
                AngularMaximum = new(component.AngularX.Maximum, component.AngularY.Maximum, component.AngularZ.Maximum),
                LinearStiffness = component.LinearStiffness,
                AngularStiffness = component.AngularStiffness,
            };
        }
    }
}

/// <summary>The timed drive parameters; frames and protected endpoints remain in DeviceJointConstraint.</summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
public struct DeviceMotorConstraint
{
    [FieldOffset(0)] public uint JointRow;
    [FieldOffset(4)] public uint Space;
    [FieldOffset(8)] public uint Response;
    [FieldOffset(16)] public Vector3 LinearStiffness;
    [FieldOffset(32)] public Vector3 LinearDamping;
    [FieldOffset(48)] public Vector3 MaximumForce;
    [FieldOffset(64)] public Vector3 AngularStiffness;
    [FieldOffset(80)] public Vector3 AngularDamping;
    [FieldOffset(96)] public Vector3 MaximumTorque;
    [FieldOffset(112)] public Vector3 TargetPosition;
    [FieldOffset(128)] public Quaternion TargetRotation;
    [FieldOffset(144)] public Vector3 TargetLinearVelocity;
    [FieldOffset(160)] public Vector3 TargetAngularVelocity;
    [FieldOffset(176)] public double TargetTime;
}

public readonly struct DeviceMotorSource : IDeviceConstraintSource<JointMotor, DeviceMotorConstraint>
{
    public static void Collect(in JointMotor component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<JointMotorTarget>(), ChunkAccess.Read<DeviceConstraintBinding<DeviceJointConstraint>>(true)];
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => chunk.WasWritten<JointMotorTarget>(since)
        || chunk.WasWritten<DeviceConstraintBinding<DeviceJointConstraint>>(since);
    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceMotorConstraint> output)
    {
        ReadOnlySpan<JointMotor> values = chunk.Read<JointMotor>().Slice(start, output.Length);
        ReadOnlySpan<JointMotorTarget> targets = chunk.Read<JointMotorTarget>().Slice(start, output.Length);
        ReadOnlySpan<DeviceConstraintBinding<DeviceJointConstraint>> bindings = chunk.Read<DeviceConstraintBinding<DeviceJointConstraint>>().Slice(start, output.Length);
        for (int item = 0; item < output.Length; item++)
        {
            ref readonly JointMotor component = ref values[item]; JointMotorTarget target = targets[item];
            output[item] = new()
            {
                JointRow = (uint)bindings[item].Index,
                Space = (uint)component.Space,
                Response = (uint)component.Response,
                LinearStiffness = component.LinearStiffness,
                LinearDamping = component.LinearDamping,
                MaximumForce = component.MaximumForce,
                AngularStiffness = component.AngularStiffness,
                AngularDamping = component.AngularDamping,
                MaximumTorque = component.MaximumTorque,
                TargetPosition = target.Frame.Position,
                TargetRotation = target.Frame.Rotation,
                TargetLinearVelocity = target.LinearVelocity,
                TargetAngularVelocity = target.AngularVelocity,
                TargetTime = target.Time,
            };
        }
    }
}

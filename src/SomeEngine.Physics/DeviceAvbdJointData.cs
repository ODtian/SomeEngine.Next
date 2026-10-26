using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 96)]
public struct DeviceAvbdJointState
{
    [FieldOffset(0)] public Vector3 LinearMultiplier;
    [FieldOffset(16)] public Vector3 AngularMultiplier;
    [FieldOffset(32)] public Vector3 LinearPenalty;
    [FieldOffset(48)] public Vector3 AngularPenalty;
    [FieldOffset(64)] public Vector3 InitialLinearError;
    [FieldOffset(80)] public Vector3 InitialAngularError;
}
public readonly struct DeviceAvbdJointStateSource : IDeviceAvbdStateSource<AvbdJointState, DeviceAvbdJointState>
{
    public DeviceAvbdJointState Read(in AvbdJointState component) => new()
    {
        LinearMultiplier = component.LinearMultiplier, AngularMultiplier = component.AngularMultiplier,
        LinearPenalty = component.LinearPenalty, AngularPenalty = component.AngularPenalty,
        InitialLinearError = component.InitialLinearError, InitialAngularError = component.InitialAngularError,
    };
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceAvbdMotorState
{
    [FieldOffset(0)] public Vector3 LinearMultiplier;
    [FieldOffset(16)] public Vector3 LinearPenalty;
    [FieldOffset(32)] public Vector3 AngularMultiplier;
    [FieldOffset(48)] public Vector3 AngularPenalty;
}
public readonly struct DeviceAvbdMotorStateSource : IDeviceAvbdStateSource<AvbdMotorState, DeviceAvbdMotorState>
{
    public DeviceAvbdMotorState Read(in AvbdMotorState component) => new()
    {
        LinearMultiplier = component.LinearMultiplier, LinearPenalty = component.LinearPenalty,
        AngularMultiplier = component.AngularMultiplier, AngularPenalty = component.AngularPenalty,
    };
}

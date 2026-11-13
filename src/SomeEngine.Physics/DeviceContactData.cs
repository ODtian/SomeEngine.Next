using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct DeviceContactConstraint
{
    [FieldOffset(0)] public uint BodyCount;
    [FieldOffset(4)] public uint FirstBodyCount;
    [FieldOffset(8)] public uint BodyOffset;
    [FieldOffset(12)] public uint WarmStart;
    [FieldOffset(16)] public Vector3 Normal;
    [FieldOffset(28)] public float Separation;
    [FieldOffset(32)] public Vector3 BodyAnchorDelta;
    [FieldOffset(44)] public float NormalStiffness;
    [FieldOffset(48)] public float NormalDamping;
    [FieldOffset(52)] public float StaticFriction;
    [FieldOffset(56)] public float DynamicFriction;
    [FieldOffset(60)] public float Restitution;
    [FieldOffset(64)] public float RestitutionThreshold;
    [FieldOffset(68)] public uint Predicted;
    [FieldOffset(72)] public double SampleTime;
    [FieldOffset(80)] public Vector3 WorldPoint;
    [FieldOffset(92)] public uint Trigger;
    [FieldOffset(96)] public Entity FirstGeometry;
    [FieldOffset(104)] public Entity SecondGeometry;
    [FieldOffset(112)] public int FirstFeature;
    [FieldOffset(116)] public int SecondFeature;
    [FieldOffset(120)] public int PointIndex;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceAvbdContactState
{
    [FieldOffset(0)] public float NormalMultiplier;
    [FieldOffset(4)] public float NormalPenalty;
    [FieldOffset(8)] public float TangentPenalty;
    [FieldOffset(12)] public float FrictionRegularizationSpeed;
    [FieldOffset(16)] public Vector3 MultiplierNormal;
    [FieldOffset(28)] public float InitialSeparation;
    [FieldOffset(32)] public Vector3 InitialDelta;
    [FieldOffset(48)] public Vector3 TangentMultiplier;
}

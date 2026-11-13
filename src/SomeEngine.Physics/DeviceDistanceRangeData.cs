using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

/// <summary>The original two local anchors, radial material and derived device body references.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceDistanceRangeConstraint
{
    [FieldOffset(0)] public uint FirstBody;
    [FieldOffset(4)] public uint SecondBody;
    [FieldOffset(16)] public Vector3 FirstAnchor;
    [FieldOffset(32)] public Vector3 SecondAnchor;
    [FieldOffset(48)] public float Minimum;
    [FieldOffset(52)] public float Maximum;
    [FieldOffset(56)] public float Stiffness;
}

public readonly struct DeviceDistanceRangeSource : IDeviceConstraintSource<DistanceRange, DeviceDistanceRangeConstraint>
{
    public static void Collect(in DistanceRange component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<RelationEndpoints<DistanceRange>>()];
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => chunk.WasWritten<RelationEndpoints<DistanceRange>>(since);
    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceDistanceRangeConstraint> output)
    {
        ReadOnlySpan<DistanceRange> values = chunk.Read<DistanceRange>().Slice(start, output.Length);
        ReadOnlySpan<RelationEndpoints<DistanceRange>> endpoints = chunk.Read<RelationEndpoints<DistanceRange>>().Slice(start, output.Length);
        for (int row = 0; row < output.Length; row++)
        {
            ref readonly DistanceRange component = ref values[row];
            output[row] = new()
            {
                FirstBody = (uint)bodies.Read(endpoints[row].Source).Index,
                SecondBody = (uint)bodies.Read(endpoints[row].Target).Index,
                FirstAnchor = component.FirstAnchor,
                SecondAnchor = component.SecondAnchor,
                Minimum = component.Minimum,
                Maximum = component.Maximum,
                Stiffness = component.Stiffness
            };
        }
    }
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct DeviceAvbdDistanceRangeState
{
    [FieldOffset(0)] public Vector2 Multiplier;
    [FieldOffset(8)] public float Penalty;
    [FieldOffset(16)] public Vector2 InitialError;
}

public readonly struct DeviceAvbdDistanceRangeStateSource : IDeviceAvbdStateSource<AvbdDistanceRangeState, DeviceAvbdDistanceRangeState>
{
    public DeviceAvbdDistanceRangeState Read(in AvbdDistanceRangeState component) => new()
    {
        Multiplier = component.Multiplier,
        Penalty = component.Penalty,
        InitialError = component.InitialError,
    };
}

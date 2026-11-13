using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Size = 16)]
public struct DeviceLengthConstraint
{
    public uint FirstBody, SecondBody;
    public float RestLength, Compliance;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct DeviceBendConstraint
{
    [FieldOffset(0)] public uint FirstBody;
    [FieldOffset(4)] public uint MiddleBody;
    [FieldOffset(8)] public uint LastBody;
    [FieldOffset(16)] public float MinimumCosine;
    [FieldOffset(20)] public float Compliance;
}

public readonly struct DeviceBendSource : IDeviceConstraintSource<XpbdBend, DeviceBendConstraint>
{
    public static void Collect(in XpbdBend component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [];
    public static SystemAccesses AdditionalAccesses { get; } = new SystemAccesses().ReadRandom<DeviceBodyBinding>()
        .ReadRandom<RelationEndpoints<ConstraintBodyLink>>();
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) =>
        world.ComponentWriteVersion(ComponentType<RelationEndpoints<ConstraintBodyLink>>.TypeId) > since;

    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceBendConstraint> output)
    {
        ReadOnlySpan<XpbdBend> values = chunk.Read<XpbdBend>().Slice(start, output.Length);
        for (int row = 0; row < output.Length; row++)
        {
            using RelationRead links = ConstraintBodies.GetBodies(world, chunk.Entities[start + row]);
            output[row] = new()
            {
                FirstBody = (uint)bodies.Read(links.Entries[0].Other).Index,
                MiddleBody = (uint)bodies.Read(links.Entries[1].Other).Index,
                LastBody = (uint)bodies.Read(links.Entries[2].Other).Index,
                MinimumCosine = values[row].MinimumCosine,
                Compliance = values[row].Compliance
            };
        }
    }
}

public readonly struct DeviceLengthSource : IDeviceConstraintSource<XpbdLength, DeviceLengthConstraint>
{
    public static void Collect(in XpbdLength component, ref JobResourceAccessBuilder resources) { }
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<RelationEndpoints<XpbdLength>>()];
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => chunk.WasWritten<RelationEndpoints<XpbdLength>>(since);
    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceLengthConstraint> output)
    {
        ReadOnlySpan<XpbdLength> values = chunk.Read<XpbdLength>().Slice(start, output.Length);
        ReadOnlySpan<RelationEndpoints<XpbdLength>> endpoints = chunk.Read<RelationEndpoints<XpbdLength>>().Slice(start, output.Length);
        for (int row = 0; row < output.Length; row++) output[row] = new()
        {
            FirstBody = (uint)bodies.Read(endpoints[row].Source).Index,
            SecondBody = (uint)bodies.Read(endpoints[row].Target).Index,
            RestLength = values[row].RestLength,
            Compliance = values[row].Compliance
        };
    }
}

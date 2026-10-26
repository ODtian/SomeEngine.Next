using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
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
    public QueryDefinition AddReads(QueryDefinition definition) =>
        ConstraintBodies.AddReads(definition).WithOptionalRead<DeviceBodyBinding>();
    public DeviceBendConstraint Read(in XpbdBend component, scoped QueryRow row, scoped QueryCursor access)
    {
        ReadOnlySpan<ConstraintBodyLink> links = row.ReadBuffer<ConstraintBodyLink>().AsSpan();
        access.TryGetRow(ConstraintBodies.GetBody(in links[0], access), out QueryRow first);
        access.TryGetRow(ConstraintBodies.GetBody(in links[1], access), out QueryRow middle);
        access.TryGetRow(ConstraintBodies.GetBody(in links[2], access), out QueryRow last);
        return new() { FirstBody = (uint)first.Read<DeviceBodyBinding>().Index,
            MiddleBody = (uint)middle.Read<DeviceBodyBinding>().Index, LastBody = (uint)last.Read<DeviceBodyBinding>().Index,
            MinimumCosine = component.MinimumCosine, Compliance = component.Compliance };
    }
}

public readonly struct DeviceLengthSource : IDeviceConstraintSource<XpbdLength, DeviceLengthConstraint>
{
    public static void Collect(in XpbdLength component, ref JobResourceAccessBuilder resources) { }
    public DeviceLengthConstraint Read(in XpbdLength component, scoped QueryRow row, scoped QueryCursor access)
    {
        UndirectedRelationEndpoints<XpbdLength> endpoints = row.Read<UndirectedRelationEndpoints<XpbdLength>>();
        access.TryGetRow(endpoints.EndpointA, out QueryRow first);
        access.TryGetRow(endpoints.EndpointB, out QueryRow second);
        return new() { FirstBody = (uint)first.Read<DeviceBodyBinding>().Index,
            SecondBody = (uint)second.Read<DeviceBodyBinding>().Index,
            RestLength = component.RestLength, Compliance = component.Compliance };
    }
}

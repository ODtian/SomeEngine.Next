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

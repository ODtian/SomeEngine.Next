using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceElasticConstraint
{
    [FieldOffset(0)] public uint FirstBody;
    [FieldOffset(4)] public uint SecondBody;
    [FieldOffset(8)] public float LinearStiffness;
    [FieldOffset(12)] public float AngularStiffness;
    [FieldOffset(16)] public Vector3 FirstAnchor;
    [FieldOffset(32)] public Vector3 SecondAnchor;
    [FieldOffset(48)] public Quaternion RestRotation;
}
public readonly struct DeviceElasticSource : IDeviceConstraintSource<ElasticConnection, DeviceElasticConstraint>
{
    public static void Collect(in ElasticConnection component, ref JobResourceAccessBuilder resources) { }
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceBodyBinding>()
        .WithOptionalRead<UndirectedRelationEndpoints<ElasticConnection>>();
    public DeviceElasticConstraint Read(in ElasticConnection component, scoped QueryRow row, scoped QueryCursor access)
    {
        var endpoints = row.Read<UndirectedRelationEndpoints<ElasticConnection>>();
        access.TryGetRow(endpoints.EndpointA, out QueryRow first); access.TryGetRow(endpoints.EndpointB, out QueryRow second);
        return new()
        {
            FirstBody = (uint)first.Read<DeviceBodyBinding>().Index, SecondBody = (uint)second.Read<DeviceBodyBinding>().Index,
            FirstAnchor = component.FirstAnchor, SecondAnchor = component.SecondAnchor, RestRotation = component.RestRotation,
            LinearStiffness = component.LinearStiffness, AngularStiffness = component.AngularStiffness,
        };
    }
}
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct DeviceAvbdElasticState
{
    [FieldOffset(0)] public Vector3 LinearMultiplier;
    [FieldOffset(12)] public float LinearPenalty;
    [FieldOffset(16)] public Vector3 AngularMultiplier;
    [FieldOffset(28)] public float AngularPenalty;
    [FieldOffset(32)] public Vector3 InitialLinearError;
    [FieldOffset(48)] public Vector3 InitialAngularError;
}
public readonly struct DeviceAvbdElasticStateSource : IDeviceAvbdStateSource<AvbdElasticState, DeviceAvbdElasticState>
{
    public DeviceAvbdElasticState Read(in AvbdElasticState component) => new()
    {
        LinearMultiplier = component.LinearMultiplier, AngularMultiplier = component.AngularMultiplier,
        LinearPenalty = component.LinearPenalty, AngularPenalty = component.AngularPenalty,
        InitialLinearError = component.InitialLinearError, InitialAngularError = component.InitialAngularError,
    };
}

using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
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
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<RelationEndpoints<ElasticConnection>>()];
    public static bool HasChangedInputs(SystemChunk chunk, World world, ulong since) => chunk.WasWritten<RelationEndpoints<ElasticConnection>>(since);
    public void Write(SystemChunk chunk, World world, int start, scoped ReadLookup<DeviceBodyBinding> bodies, Span<DeviceElasticConstraint> output)
    {
        ReadOnlySpan<ElasticConnection> values = chunk.Read<ElasticConnection>().Slice(start, output.Length);
        ReadOnlySpan<RelationEndpoints<ElasticConnection>> endpoints = chunk.Read<RelationEndpoints<ElasticConnection>>().Slice(start, output.Length);
        for (int row = 0; row < output.Length; row++)
        {
            ref readonly ElasticConnection component = ref values[row];
            output[row] = new()
            {
                FirstBody = (uint)bodies.Read(endpoints[row].Source).Index,
                SecondBody = (uint)bodies.Read(endpoints[row].Target).Index,
                FirstAnchor = component.FirstAnchor,
                SecondAnchor = component.SecondAnchor,
                RestRotation = component.RestRotation,
                LinearStiffness = component.LinearStiffness,
                AngularStiffness = component.AngularStiffness
            };
        }
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
        LinearMultiplier = component.LinearMultiplier,
        AngularMultiplier = component.AngularMultiplier,
        LinearPenalty = component.LinearPenalty,
        AngularPenalty = component.AngularPenalty,
        InitialLinearError = component.InitialLinearError,
        InitialAngularError = component.InitialAngularError,
    };
}

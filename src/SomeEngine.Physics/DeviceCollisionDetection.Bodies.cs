using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Explicit, Size = 32)]
public readonly record struct DeviceContactBody
{
    [FieldOffset(0)] private readonly Entity _entity;
    [FieldOffset(8)] private readonly Vector3 _localPoint;
    [FieldOffset(20)] private readonly float _weight;
    [FieldOffset(28)] private readonly ContactNormalReference _normalReference;
    [field: FieldOffset(24)] public uint StateIndex { get; }
    public ContactBody Body => new(_entity, _localPoint, _weight, _normalReference);

    public DeviceContactBody(ContactBody body, uint stateIndex)
    {
        _entity = body.Body; _localPoint = body.LocalPoint; _weight = body.Weight;
        _normalReference = body.NormalReference; StateIndex = stateIndex;
    }
}

public sealed partial class DeviceCollisionDetection
{
    private DeviceContactBody[] _contactBodies = [];
    private int[] _contactBodyCounts = [];
    public Buffer? ContactBodies { get; private set; }
    public Buffer? ContactBodyCounts { get; private set; }

    private void CreateContactBodies(Dictionary<string, ResourceBinding[]> bindings)
    {
        if (_geometry.Bodies is null) return;
        ContactBodies = CreateWorkBuffer("CollisionContactBodies", (ulong)Capacity.Pairs * 4 * (ulong)Capacity.BodiesPerContact * 32, 32, bindings);
        ContactBodyCounts = CreateWorkBuffer("CollisionContactBodyCounts", (ulong)Capacity.Pairs * 4 * 4, 4, bindings);
        if ((_readback & DeviceCollisionReadback.Contacts) == 0) return;
        _contactBodies = new DeviceContactBody[Capacity.Pairs * 4 * Capacity.BodiesPerContact];
        _contactBodyCounts = new int[Capacity.Pairs * 4];
    }
    private ulong ContactBodyCountReadbackOffset => ContactReadbackOffset +
        ((_readback & DeviceCollisionReadback.Contacts) != 0 ? Contacts.Info.Size : 0);
    private ulong ContactBodyReadbackOffset => ContactBodyCountReadbackOffset +
        ((_readback & DeviceCollisionReadback.Contacts) != 0 ? ContactBodyCounts?.Info.Size ?? 0 : 0);
    private ulong ResultReadbackSize => ContactBodyReadbackOffset +
        ((_readback & DeviceCollisionReadback.Contacts) != 0 ? ContactBodies?.Info.Size ?? 0 : 0);

    public ReadOnlySpan<DeviceContactBody> ReadContactBodies(int pair, int point) =>
        _contactBodies.AsSpan((pair * 4 + point) * Capacity.BodiesPerContact, _contactBodyCounts[pair * 4 + point]);

    private void AcceptContactBodies()
    {
        if (_geometry.Bodies is null || (_readback & DeviceCollisionReadback.Contacts) == 0) return;
        ReadResults(ContactBodyCountReadbackOffset, _contactBodyCounts.AsSpan(0, Count * 4));
        ReadResults(ContactBodyReadbackOffset, _contactBodies.AsSpan(0, Count * 4 * Capacity.BodiesPerContact));
    }
}

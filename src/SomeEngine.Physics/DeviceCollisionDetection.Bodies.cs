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
        ContactBodies = CreateWorkBuffer("CollisionContactBodies", (ulong)Capacity.Contacts * (ulong)Capacity.BodiesPerContact * 32, 32, bindings, contactBytes: (uint)Capacity.BodiesPerContact * 32);
        ContactBodyCounts = CreateWorkBuffer("CollisionContactBodyCounts", (ulong)Capacity.Contacts * 4, 4, bindings, contactBytes: 4);
        CreateWorkBuffer("CollisionWorkBodies", (ulong)Capacity.WorkItems * 4 * (ulong)Capacity.BodiesPerContact * 32, 32, bindings);
        CreateWorkBuffer("CollisionWorkBodyCounts", (ulong)Capacity.WorkItems * 4 * 4, 4, bindings);
        if ((_readback & DeviceCollisionReadback.Contacts) == 0) return;
    }
    private ulong ContactBodyCountReadbackOffset => ContactReadbackOffset +
        (!ReadsContactChanges && (_readback & DeviceCollisionReadback.Contacts) != 0 ? (ulong)ContactCount * ContactStride : 0);
    private ulong ContactBodyReadbackOffset => ContactBodyCountReadbackOffset +
        (!ReadsContactChanges && (_readback & DeviceCollisionReadback.Contacts) != 0 ? ContactBodyCounts is null ? 0 : (ulong)ContactCount * 4 : 0);
    private ulong ResultReadbackSize => ContactBodyReadbackOffset +
        (!ReadsContactChanges && (_readback & DeviceCollisionReadback.Contacts) != 0 ? ContactBodies is null ? 0 : (ulong)ContactCount * (ulong)Capacity.BodiesPerContact * 32 : 0);

    public ReadOnlySpan<DeviceContactBody> ReadContactBodies(int pair, int point) =>
        _contactBodies.AsSpan((_contactOffsets[pair] + point) * Capacity.BodiesPerContact, _contactBodyCounts[_contactOffsets[pair] + point]);

    private void AcceptContactBodies()
    {
        if (_geometry.Bodies is null || (_readback & DeviceCollisionReadback.Contacts) == 0) return;
        ReadResults(ContactBodyCountReadbackOffset, _contactBodyCounts.AsSpan(0, ContactCount));
        ReadResults(ContactBodyReadbackOffset, _contactBodies.AsSpan(0, ContactCount * Capacity.BodiesPerContact));
    }
}

using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

[StructLayout(LayoutKind.Sequential, Size = 32)]
public readonly record struct DeviceContactBody(ContactBody Body, uint StateIndex);

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

    private void AcceptContactBodies(Span<byte> bytes)
    {
        if (_geometry.Bodies is null || (_readback & DeviceCollisionReadback.Contacts) == 0) return;
        MemoryMarshal.Cast<byte, int>(bytes.Slice((int)ContactBodyCountReadbackOffset, Count * 4 * 4)).CopyTo(_contactBodyCounts);
        MemoryMarshal.Cast<byte, DeviceContactBody>(bytes.Slice((int)ContactBodyReadbackOffset, Count * 4 * Capacity.BodiesPerContact * 32)).CopyTo(_contactBodies);
    }
}

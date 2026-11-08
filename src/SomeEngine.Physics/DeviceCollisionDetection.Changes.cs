using SomeEngine.Graphics;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionDetection
{
    private int _changeCount;
    private ulong ChangeBodyCountOffset => (ulong)_changeCount * ContactStride;
    private ulong ChangeBodyOffset => ChangeBodyCountOffset + (ContactBodies is null ? 0 : (ulong)_changeCount * 4);

    internal QueueCompletion SubmitContactChanges(List<int> inputs)
    {
        _changeCount = inputs.Count;
        ulong bytes = ChangeBodyOffset + (ContactBodies is null ? 0 : (ulong)inputs.Count * (ulong)Capacity.BodiesPerContact * 32);
        if (_readbackBuffer!.Info.Size < bytes)
        {
            _objects.Remove(_readbackBuffer); _readbackBuffer.Dispose();
            _readbackBuffer = Keep(_backend.CreateBuffer(_device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback));
        }
        _backend.Begin(_commands, new(0, 0, 16));
        int first = 0;
        while (first < inputs.Count)
        {
            int end = first + 1;
            while (end < inputs.Count && inputs[end] == inputs[end - 1] + 1) end++;
            CopyResults(Contacts, (ulong)inputs[first] * ContactStride, (ulong)first * ContactStride, (ulong)(end - first) * ContactStride);
            if (ContactBodies is not null)
            {
                CopyResults(ContactBodyCounts!, (ulong)inputs[first] * 4, ChangeBodyCountOffset + (ulong)first * 4, (ulong)(end - first) * 4);
                CopyResults(ContactBodies, (ulong)inputs[first] * (ulong)Capacity.BodiesPerContact * 32,
                    ChangeBodyOffset + (ulong)first * (ulong)Capacity.BodiesPerContact * 32,
                    (ulong)(end - first) * (ulong)Capacity.BodiesPerContact * 32);
            }
            first = end;
        }
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    internal MappedBuffer MapContactChanges()
    {
        ulong bytes = ChangeBodyOffset + (ContactBodies is null ? 0 : (ulong)_changeCount * (ulong)Capacity.BodiesPerContact * 32);
        MappedBuffer mapping = _backend.Map(_readbackBuffer!, MapType.Read, new(0, bytes));
        mapping.Invalidate(mapping.Range);
        return mapping;
    }
}

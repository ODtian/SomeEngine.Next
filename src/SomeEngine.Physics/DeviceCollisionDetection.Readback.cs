using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionDetection
{
    private void ReadBounds()
    {
        using MappedBuffer mapping = _backend.Map(_readbackBuffer!, MapType.Read, new(0, 12));
        mapping.Invalidate(mapping.Range);
        MemoryMarshal.Cast<byte, int>(mapping.Bytes).CopyTo(_binBounds);
    }

    private QueueCompletion SubmitResultPrefix()
    {
        _backend.Begin(_commands, new(0, 0, 16));
        if ((_readback & DeviceCollisionReadback.Distances) != 0)
            CopyResults(Distances, 0, 12, (ulong)Count * 64);
        if (ReadsContactRows)
        {
            CopyResults(ContactCounts, 0, CountReadbackOffset, (ulong)Count * 4);
            CopyResults(ContactOffsets, 0, ContactOffsetReadbackOffset, (ulong)Count * 4);
            if (!ReadsContactChanges && ContactCount != 0) CopyResults(Contacts, 0, ContactReadbackOffset, (ulong)ContactCount * ContactStride);
            if (!ReadsContactChanges && _geometry.Bodies is not null && ContactCount != 0)
            {
                CopyResults(ContactBodyCounts!, 0, ContactBodyCountReadbackOffset, (ulong)ContactCount * 4);
                CopyResults(ContactBodies!, 0, ContactBodyReadbackOffset, (ulong)ContactCount * (ulong)Capacity.BodiesPerContact * 32);
            }
        }
        using RecordedCommands commands = _backend.End(_commands);
        Completion = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [commands], [], []));
        return Completion;
    }

    private void Accept(in PhysicsStepContext context)
    {
        if (_readback != DeviceCollisionReadback.None && Count != 0)
        {
            if ((_readback & DeviceCollisionReadback.Distances) != 0)
                ReadResults(12, _distances.AsSpan(0, Count));
            if (ReadsContactRows)
            {
                ReadResults(CountReadbackOffset, _contactCounts.AsSpan(0, Count));
                ReadResults(ContactOffsetReadbackOffset, _contactOffsets.AsSpan(0, Count));
                if (!ReadsContactChanges && ContactCount != 0)
                {
                    ReadResults(ContactReadbackOffset, _contacts.AsSpan(0, ContactCount));
                    AcceptContactBodies();
                }
            }
        }
        Number = context.Number;
        Time = context.EndTime;
    }

    private void ReadResults<T>(ulong offset, Span<T> values) where T : unmanaged
    {
        Span<byte> bytes = MemoryMarshal.AsBytes(values);
        using MappedBuffer mapping = _backend.Map(_readbackBuffer!, MapType.Read, new(offset, (ulong)bytes.Length));
        mapping.Invalidate(mapping.Range);
        mapping.Bytes.CopyTo(bytes);
    }

    private void GrowReadback()
    {
        if (_readbackBuffer!.Info.Size < ResultReadbackSize)
        {
            _objects.Remove(_readbackBuffer); _readbackBuffer.Dispose();
            _readbackBuffer = Keep(_backend.CreateBuffer(_device, new(ResultReadbackSize, BufferUsages.CopyDestination), MemoryType.Readback));
        }
        if ((_readback & DeviceCollisionReadback.Distances) != 0 && _distances.Length < Count) Array.Resize(ref _distances, Math.Max(Count, _distances.Length * 2));
        if (!ReadsContactRows) return;
        if (_contactCounts.Length < Count) Array.Resize(ref _contactCounts, Math.Max(Count, _contactCounts.Length * 2));
        if (_contactOffsets.Length < Count) Array.Resize(ref _contactOffsets, _contactCounts.Length);
        if (ReadsContactChanges) return;
        if (_contacts.Length < ContactCount) Array.Resize(ref _contacts, Math.Max(ContactCount, _contacts.Length * 2));
        if (_geometry.Bodies is null) return;
        if (_contactBodyCounts.Length < ContactCount) Array.Resize(ref _contactBodyCounts, _contacts.Length);
        if (_contactBodies.Length < ContactCount * Capacity.BodiesPerContact) Array.Resize(ref _contactBodies, _contacts.Length * Capacity.BodiesPerContact);
    }

    private JobHandle ScheduleAccept(Scheduler scheduler, in PhysicsStepContext context, JobResourceAccess[] accesses,
        JobHandle dependency) => scheduler.Schedule(new AcceptDetection(this, context), accesses, dependency);

    private readonly struct ReadDetectionBounds(DeviceCollisionDetection detection, Scheduler scheduler,
        PhysicsStepContext context, JobResourceAccess[] deviceAccesses, JobResourceAccess[] acceptAccesses) : IJob
    {
        public void Execute()
        {
            detection.ReadBounds();
            if (detection.Capacity.Memory is { } memory && detection.ContactCount > memory.Rows)
            {
                memory.EnsureRows(detection.ContactCount);
                QueueCompletion repeated = detection.Submit(in context);
                JobHandle filled = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, detection._backend, repeated), deviceAccesses);
                scheduler.Schedule(new ReadDetectionBounds(detection, scheduler, context, deviceAccesses, acceptAccesses), deviceAccesses, filled);
                return;
            }
            detection.GrowReadback();
            JobHandle ready = default;
            if (detection.Count != 0 && (detection.ReadsContactRows || (detection._readback & DeviceCollisionReadback.Distances) != 0))
            {
                QueueCompletion completion = detection.SubmitResultPrefix();
                ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, detection._backend, completion), deviceAccesses);
            }
            detection.ScheduleAccept(scheduler, in context, acceptAccesses, ready);
        }
    }
}

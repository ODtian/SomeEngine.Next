using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionDetection
{
    private void ReadBounds()
    {
        using MappedBuffer mapping = _backend.Map(_readbackBuffer!, MapType.Read, new(0, 8));
        mapping.Invalidate(mapping.Range);
        MemoryMarshal.Cast<byte, int>(mapping.Bytes).CopyTo(_binBounds);
    }

    private QueueCompletion SubmitResultPrefix()
    {
        _backend.Begin(_commands, new(0, 0, 16));
        if ((_readback & DeviceCollisionReadback.Distances) != 0)
            CopyResults(Distances, 0, 8, (ulong)Count * 64);
        if ((_readback & DeviceCollisionReadback.Contacts) != 0)
        {
            CopyResults(ContactCounts, 0, CountReadbackOffset, (ulong)Count * 4);
            CopyResults(Contacts, 0, ContactReadbackOffset, (ulong)Count * 4 * ContactStride);
            if (_geometry.Bodies is not null)
            {
                CopyResults(ContactBodyCounts!, 0, ContactBodyCountReadbackOffset, (ulong)Count * 4 * 4);
                CopyResults(ContactBodies!, 0, ContactBodyReadbackOffset, (ulong)Count * 4 * (ulong)Capacity.BodiesPerContact * 32);
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
                ReadResults(8, _distances.AsSpan(0, Count));
            if ((_readback & DeviceCollisionReadback.Contacts) != 0)
            {
                ReadResults(CountReadbackOffset, _contactCounts.AsSpan(0, Count));
                ReadResults(ContactReadbackOffset, _contacts.AsSpan(0, Count * 4));
                AcceptContactBodies();
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

    private JobHandle ScheduleAccept(Scheduler scheduler, in PhysicsStepContext context, JobResourceAccess[] accesses,
        JobHandle dependency) => scheduler.Schedule(new AcceptDetection(this, context), accesses, dependency);

    private readonly struct ReadDetectionBounds(DeviceCollisionDetection detection, Scheduler scheduler,
        PhysicsStepContext context, JobResourceAccess[] deviceAccesses, JobResourceAccess[] acceptAccesses) : IJob
    {
        public void Execute()
        {
            detection.ReadBounds();
            JobHandle ready = default;
            if (detection.Count != 0)
            {
                QueueCompletion completion = detection.SubmitResultPrefix();
                ready = scheduler.CreateExternalFenceHandle(new DeviceQueueFence(scheduler, detection._backend, completion), deviceAccesses);
            }
            detection.ScheduleAccept(scheduler, in context, acceptAccesses, ready);
        }
    }
}

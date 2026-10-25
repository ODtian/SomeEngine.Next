using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionDetection
{
    /// <summary>Appends the selected contact readback to the concrete output and its declared resources.</summary>
    public WriteDeviceCollisionContacts<TOutput> WriteContacts<TOutput>(TOutput output,
        ReadOnlyMemory<JobResourceAccess> outputAccesses) where TOutput : struct, ICollisionContactOutput =>
        new(this, output, outputAccesses.ToArray());
}

/// <summary>Publishes rigid geometry contacts through the same output used by CPU detection.</summary>
public readonly struct WriteDeviceCollisionContacts<TOutput> : IPhysicsStage
    where TOutput : struct, ICollisionContactOutput
{
    private readonly DeviceCollisionDetection _detection;
    private readonly TOutput _output;
    private readonly JobResourceAccess[] _outputAccesses;

    internal WriteDeviceCollisionContacts(DeviceCollisionDetection detection, TOutput output,
        JobResourceAccess[] outputAccesses) => (_detection, _output, _outputAccesses) = (detection, output, outputAccesses);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        var accesses = new List<JobResourceAccess>(_detection.GetReadbackAccesses(scheduler!));
        accesses.AddRange(_outputAccesses);
        return scheduler!.Schedule(new WriteContacts(_detection, _output), CollectionsMarshal.AsSpan(accesses), dependency);
    }

    private readonly struct WriteContacts(DeviceCollisionDetection detection, TOutput output) : IJob
    {
        public void Execute()
        {
            Span<ContactBody> first = stackalloc ContactBody[1];
            Span<ContactBody> second = stackalloc ContactBody[1];
            for (int pair = 0; pair < detection.Count; pair++)
                foreach (ref readonly CollisionContact contact in detection.ReadContacts(pair))
                {
                    first[0] = new(contact.First, contact.FirstLocalPoint, 1);
                    second[0] = new(contact.Second, contact.SecondLocalPoint, 1);
                    output.Add(in contact, first, second);
                }
        }
    }
}

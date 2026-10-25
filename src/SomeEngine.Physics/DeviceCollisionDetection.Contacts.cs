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
            Span<ContactBody> first = stackalloc ContactBody[3];
            Span<ContactBody> second = stackalloc ContactBody[3];
            for (int pair = 0; pair < detection.Count; pair++)
            {
                int point = 0;
                foreach (ref readonly CollisionContact contact in detection.ReadContacts(pair))
                {
                    if (detection.ContactBodies is not null)
                    {
                        ReadOnlySpan<DeviceContactBody> bodies = detection.ReadContactBodies(pair, point++);
                        for (int index = 0; index < contact.FirstBodyCount; index++) first[index] = bodies[index].Body;
                        int secondCount = bodies.Length - contact.FirstBodyCount;
                        for (int index = 0; index < secondCount; index++) second[index] = bodies[index + contact.FirstBodyCount].Body;
                        output.Add(in contact, first[..contact.FirstBodyCount], second[..secondCount]);
                        continue;
                    }
                    first[0] = new(contact.First, contact.FirstLocalPoint, 1);
                    second[0] = new(contact.Second, contact.SecondLocalPoint, 1);
                    output.Add(in contact, first[..1], second[..1]);
                }
            }
        }
    }
}

using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceAvbdBodyEquations
{
    private readonly Dictionary<(World World, Query Bodies), EquationInputRows> _inputRows = [];

    internal JobHandle Upload(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var key = (context.World, context.Bodies);
        if (!_inputRows.TryGetValue(key, out EquationInputRows? rows))
            _inputRows.Add(key, rows = new(context.World, context.Bodies));
        JobResourceAccess[] writes = [.. rows.Accesses,
            JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size)];
        JobHandle written = scheduler.Schedule(new WriteInputRows(this, rows, context), writes, dependency);
        JobResourceAccess[] accesses = [JobResourceAccess.Write(scheduler.GetResourceToken(_metadata), 0, (long)_metadata.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, written);
    }

    private sealed class EquationInputRows : IDisposable
    {
        internal readonly World World;
        internal readonly Query Query;
        internal readonly PhysicsQueryWindow Window;
        internal JobResourceAccess[] Accesses => ((WorldJobs)World.Execution!).CollectAccess<BodyAccess>(Query);

        internal EquationInputRows(World world, Query bodies)
        {
            World = world;
            Query = bodies.WithOptionalRead<DeviceBodyBinding>().WithOptionalRead<DeviceAvbdEquationBinding>();
            Window = new(Query);
        }

        public void Dispose() { }
    }

    private readonly struct BodyAccess : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceBodyBinding>(true), ChunkAccess.Read<DeviceAvbdEquationBinding>(true)];
        public void Execute(SystemChunk chunk) { }
    }
    private readonly struct EquationInputReferences : IJobComponentResources<DeviceAvbdEquationBinding>
    {
        public static void Collect(in DeviceAvbdEquationBinding component, ref JobResourceAccessBuilder resources) { }
    }

    private readonly struct WriteInputRows(DeviceAvbdBodyEquations equations, EquationInputRows rows, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            equations._mapping.Bytes.Clear();
            ulong since = rows.Window.Begin(context);
            using QueryScope query = rows.Query.Since(since).OpenRead();
            foreach (QueryChunk chunk in query)
            {
                ReadOnlySpan<DeviceBodyBinding> bodies = chunk.Read<DeviceBodyBinding>();
                ReadOnlySpan<DeviceAvbdEquationBinding> bindings = chunk.Read<DeviceAvbdEquationBinding>();
                for (int row = 0; row < chunk.Count; row++)
                    if (ReferenceEquals(bindings[row].Equations, equations) && ReferenceEquals(bodies[row].Bodies, equations.Bodies))
                        equations.Write(bindings[row].Index, bodies[row].Index, chunk.Entities[row]);
            }
            rows.Window.Accept(rows.World.Version);
        }
    }
}

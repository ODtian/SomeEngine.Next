using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceAvbdBodyEquations
{
    private readonly Dictionary<(World World, QueryDefinition Bodies), EquationInputRows> _inputRows = [];

    internal JobHandle Upload(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        var key = (context.World, context.Bodies);
        if (!_inputRows.TryGetValue(key, out EquationInputRows? rows))
            _inputRows.Add(key, rows = new(context.World, context.Bodies));
        JobResourceAccess[] writes = [.. rows.Accesses,
            JobResourceAccess.Write(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size)];
        JobHandle written = scheduler.Schedule(new WriteInputRows(this, rows), writes, dependency);
        JobResourceAccess[] accesses = [JobResourceAccess.Write(scheduler.GetResourceToken(_metadata), 0, (long)_metadata.Info.Size),
            JobResourceAccess.Read(scheduler.GetResourceToken(_metadataUpload), 0, (long)_metadataUpload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, this, accesses, written);
    }

    private sealed class EquationInputRows : IDisposable
    {
        internal readonly World World;
        internal readonly QueryHandle Query;
        internal readonly JobResourceAccess[] Accesses;

        internal EquationInputRows(World world, QueryDefinition bodies)
        {
            World = world;
            var input = new GeneratedQueryAccessDescriptor(new QueryDefinitionBuilder()
                .Read<DeviceBodyBinding>().Read<DeviceAvbdEquationBinding>().Build(),
                GeneratedQueryAccess.Table<DeviceBodyBinding, DeviceBodyReferenceResources>(GeneratedQueryMode.Read),
                GeneratedQueryAccess.Table<DeviceAvbdEquationBinding, EquationInputReferences>(GeneratedQueryMode.Read));
            QueryDefinition definition = input.WithFilter(bodies.AsFilter()).Query;
            Query = world.Query(definition);
            var references = new QueryReadResources();
            references.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
            references.Add<DeviceAvbdEquationBinding, EquationInputReferences>();
            Accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, Accesses, references);
        }

        public void Dispose() => World.ReleaseQuery(Query);
    }

    private readonly struct EquationInputReferences : IJobComponentResources<DeviceAvbdEquationBinding>
    {
        public static void Collect(in DeviceAvbdEquationBinding component, ref JobResourceAccessBuilder resources) { }
    }

    private readonly struct WriteInputRows(DeviceAvbdBodyEquations equations, EquationInputRows rows) : IJob
    {
        public void Execute()
        {
            equations._mapping.Bytes.Clear();
            DeviceAvbdBodyEquations destination = equations;
            rows.World.ExecuteReadQuery(rows.Query, ref destination,
                static (scoped QueryCursor query, ref DeviceAvbdBodyEquations output) =>
                {
                    foreach (QueryRow row in query.Rows)
                    {
                        ref readonly DeviceBodyBinding body = ref row.ReadRef<DeviceBodyBinding>();
                        ref readonly DeviceAvbdEquationBinding binding = ref row.ReadRef<DeviceAvbdEquationBinding>();
                        if (ReferenceEquals(binding.Equations, output) && ReferenceEquals(body.Bodies, output.Bodies))
                            output.Write(binding.Index, body.Index, row.Entity);
                    }
                });
        }
    }
}

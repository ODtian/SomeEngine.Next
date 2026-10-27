using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Selects the corresponding component's device ALM history; its parameter binding supplies the row.</summary>
public readonly record struct DeviceAvbdState<TState>(DeviceConstraintState<TState> States) : IComponent where TState : unmanaged;
public readonly struct DeviceAvbdStateResources<TState> : IJobComponentResources<DeviceAvbdState<TState>> where TState : unmanaged
{
    public static void Collect(in DeviceAvbdState<TState> component, ref JobResourceAccessBuilder resources) { }
}
public readonly struct DeviceAvbdStateRows<TState>(DeviceConstraintState<TState> states) : IDeviceConstraintRowSelection where TState : unmanaged
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<DeviceAvbdState<TState>>();
    public void AddReadResources(QueryReadResources resources) => resources.Add<DeviceAvbdState<TState>, DeviceAvbdStateResources<TState>>();
    public bool Matches(scoped QueryRow row) => row.TryRead<DeviceAvbdState<TState>>(out var binding) && ReferenceEquals(binding.States, states);
    public bool HasChangedInputs(scoped QueryChunkView chunk) => !chunk.Has<DeviceAvbdState<TState>>()
        || chunk.HasChangedSinceLastSystemVersion<DeviceAvbdState<TState>>();
}

public interface IDeviceAvbdStateSource<TComponent, TState> where TComponent : unmanaged, IComponent where TState : unmanaged
{
    TState Read(in TComponent component);
}

/// <summary>Transfers new or explicitly edited component histories; resident numerical updates keep their values.</summary>
public sealed class DeviceAvbdStateInputs<TComponent, TData, TState, TSource> : IDisposable
    where TComponent : unmanaged, IComponent where TData : unmanaged where TState : unmanaged
    where TSource : struct, IDeviceAvbdStateSource<TComponent, TState>
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly QueryHandle _rows;
    private readonly DeviceConstraintData<TData> _parameters;
    private readonly DeviceConstraintState<TState> _states;
    private readonly TSource _source;
    private readonly Buffer _upload;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Dictionary<Entity, RowVersion> _versions = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<Entity> _removed = [];
    private readonly List<(Entity Entity, RowVersion Version)> _changed = [];
    private readonly List<int> _indices = [];
    private bool _initialized;
    private bool _hasSourceVersions;
    private uint _membershipVersion, _lastSystemVersion;

    public DeviceAvbdStateInputs(IGraphicsBackend backend, Device device, World world, QueryHandle rows,
        DeviceConstraintData<TData> parameters, DeviceConstraintState<TState> states, TSource source)
    {
        _backend = backend; _device = device; _world = world; _parameters = parameters; _states = states; _source = source;
        QueryDefinition definition = world.GetQueryDefinition(rows).AsReadOnly().WithOptionalRead<TComponent>()
            .WithOptionalRead<DeviceConstraintBinding<TData>>().WithOptionalRead<DeviceAvbdState<TState>>();
        _rows = world.Query(definition);
        var descriptions = new QueryReadResources();
        descriptions.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        descriptions.Add<DeviceAvbdState<TState>, DeviceAvbdStateResources<TState>>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, descriptions);
        _upload = backend.CreateBuffer(device, new(states.States.Info.Size, BufferUsages.CopySource,
            "Edited AVBD component input"), MemoryType.Upload);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "AVBD component transfer"));
    }

    public UploadDeviceAvbdState<TComponent, TData, TState, TSource> Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, _states.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)_upload.Info.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this), accesses, dependency);
    }
    private QueueCompletion Submit()
    {
        uint membership = _world.MembershipVersion(_rows);
        var changes = new SourceChanges { Changed = !_hasSourceVersions || membership != _membershipVersion };
        _world.ExecuteReadQuery(_rows, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref SourceChanges changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.Has<TComponent>() && chunk.HasChangedSinceLastSystemVersion<TComponent>()
                        || chunk.Has<DeviceConstraintBinding<TData>>() && chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>()
                        || chunk.Has<DeviceAvbdState<TState>>() && chunk.HasChangedSinceLastSystemVersion<DeviceAvbdState<TState>>())
                    { changes.Changed = true; break; }
            });
        _changed.Clear(); _indices.Clear();
        if (changes.Changed)
        {
            _seen.Clear();
            using MappedBuffer mapping = _backend.Map(_upload, MapType.Write, BufferRange.Whole);
            var read = new ReadRows(this, mapping);
            _world.ExecuteReadQuery(_rows, ref read, static (scoped QueryCursor rows, ref ReadRows input) => input.Read(rows));
            foreach (int index in _indices) mapping.Flush(new((ulong)(index * Unsafe.SizeOf<TState>()), (ulong)Unsafe.SizeOf<TState>()));
        }
        _backend.Begin(_commands, new(0, 0, 8));
        if (_indices.Count != 0)
        {
            _backend.Barrier(_commands, new BufferBarrier(_states.States,
                _initialized ? PipelineSync.ComputeShading : _states.States.InitialSync, PipelineSync.Copy,
                _initialized ? ResourceAccess.UnorderedAccess : _states.States.InitialAccess, ResourceAccess.CopyDestination));
            _indices.Sort();
            int first = 0;
            while (first < _indices.Count)
            {
                int last = first;
                while (last + 1 < _indices.Count && _indices[last + 1] == _indices[last] + 1) last++;
                ulong offset = (ulong)(_indices[first] * Unsafe.SizeOf<TState>());
                _backend.CopyBuffer(_commands, new(_upload, offset, _states.States, offset,
                    (ulong)((last - first + 1) * Unsafe.SizeOf<TState>())));
                first = last + 1;
            }
            _backend.Barrier(_commands, new BufferBarrier(_states.States, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completed = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        _initialized |= _indices.Count != 0;
        foreach (var change in _changed) _versions[change.Entity] = change.Version;
        if (changes.Changed)
        {
            _removed.Clear();
            foreach (Entity entity in _versions.Keys) if (!_seen.Contains(entity)) _removed.Add(entity);
            foreach (Entity entity in _removed) _versions.Remove(entity);
        }
        _hasSourceVersions = true; _membershipVersion = membership; _lastSystemVersion = changes.Version;
        return completed;
    }
    private struct SourceChanges
    {
        internal bool Changed;
        internal uint Version;
    }
    private readonly record struct RowVersion(int Index, uint Version);
    private readonly ref struct ReadRows(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input, MappedBuffer mapping)
    {
        internal void Read(scoped QueryCursor rows)
        {
            foreach (QueryRow row in rows.Rows)
            {
                if (!row.TryRead<DeviceConstraintBinding<TData>>(out var binding) || !ReferenceEquals(binding.Data, input._parameters)
                    || !row.TryRead<DeviceAvbdState<TState>>(out var state) || !ReferenceEquals(state.States, input._states)
                    || !row.Has<TComponent>()) continue;
                Entity entity = row.Entity;
                input._seen.Add(entity);
                var version = new RowVersion(binding.Index, row.WriteVersion<TComponent>());
                if (input._versions.TryGetValue(entity, out RowVersion previous) && previous == version) continue;
                TState data = input._source.Read(in row.ReadRef<TComponent>());
                MemoryMarshal.Write(mapping.Bytes.Slice(binding.Index * Unsafe.SizeOf<TState>(), Unsafe.SizeOf<TState>()), in data);
                input._changed.Add((entity, version)); input._indices.Add(binding.Index);
            }
        }
    }
    private readonly struct Transfer(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> inputs) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._backend;
        public Device Device => inputs._device;
        public QueueCompletion Submit() => inputs.Submit();
    }
    public void Dispose() { _world.ReleaseQuery(_rows); _commands.Dispose(); _upload.Dispose(); }
}

public readonly struct UploadDeviceAvbdState<TComponent, TData, TState, TSource>(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input)
    : IPhysicsStage where TComponent : unmanaged, IComponent where TData : unmanaged where TState : unmanaged
    where TSource : struct, IDeviceAvbdStateSource<TComponent, TState>
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => input.Schedule(in context, dependency);
}

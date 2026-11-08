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
    private readonly QueryKey _sourceKey;
    private readonly DeviceConstraintData<TData> _parameters;
    private readonly DeviceConstraintState<TState> _states;
    private readonly TSource _source;
    private readonly DeviceInputPages _upload;
    private readonly CommandContext _commands;
    private readonly JobResourceAccess[] _queryAccesses;
    private readonly Dictionary<Entity, RowVersion> _versions = [];
    private readonly HashSet<Entity> _seen = [];
    private readonly List<Entity> _removed = [];
    private readonly List<(Entity Entity, RowVersion Version)> _changed = [];
    private readonly List<int> _indices = [];
    private readonly List<BufferRange> _ranges = [];
    private bool _hasSourceVersions;
    private uint _membershipVersion;
    private (uint Component, uint Binding, uint State) _sourceVersions;
    private readonly PhysicsQueryWindow _window;

    public DeviceAvbdStateInputs(IGraphicsBackend backend, Device device, World world, QueryHandle rows,
        DeviceConstraintData<TData> parameters, DeviceConstraintState<TState> states, TSource source)
    {
        _backend = backend; _device = device; _world = world; _parameters = parameters; _states = states; _source = source;
        _sourceKey = world.GetQueryDefinition(rows).AsFilter().Key;
        QueryDefinition definition = world.GetQueryDefinition(rows).AsReadOnly().WithOptionalRead<TComponent>()
            .WithOptionalRead<DeviceConstraintBinding<TData>>().WithOptionalRead<DeviceAvbdState<TState>>();
        _window = new(definition);
        _rows = world.Query(definition);
        var descriptions = new QueryReadResources();
        descriptions.Add<DeviceConstraintBinding<TData>, DeviceConstraintReferenceResources<TData>>();
        descriptions.Add<DeviceAvbdState<TState>, DeviceAvbdStateResources<TState>>();
        _queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
        QueryJobAccess.CopyAccesses(world, definition, _queryAccesses, descriptions);
        _upload = new(backend, device, states.Capacity, Unsafe.SizeOf<TState>(), "Edited AVBD component input");
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "AVBD component transfer"));
    }

    public UploadDeviceAvbdState<TComponent, TData, TState, TSource> Upload(DeviceContactMatches? changes = null) => new(this, changes);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency, DeviceContactMatches? changes = null)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] accesses = [.. _queryAccesses, _states.GetWriteAccess(scheduler),
            JobResourceAccess.Write(scheduler.GetResourceToken(_upload), 0, (long)(ulong)_upload.Size),
            JobResourceAccess.Write(scheduler.GetResourceToken(_commands))];
        if (changes is not null) accesses = [.. accesses, changes.ReadAccess];
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, context, changes), accesses, dependency);
    }
    private QueueCompletion Submit(in PhysicsStepContext context, DeviceContactMatches? lifecycle = null)
    {
        uint baseline = _window.Begin(context);
        bool useLifecycleChanges = _hasSourceVersions && !_window.UsesVersions
            && lifecycle?.ProvidesLifecycleChanges(_world, _sourceKey, _parameters) == true;
        uint membership = _world.MembershipVersion(_rows);
        var sourceVersions = (_world.ComponentWriteVersion<TComponent>(),
            _world.ComponentWriteVersion<DeviceConstraintBinding<TData>>(), _world.ComponentWriteVersion<DeviceAvbdState<TState>>());
        var changes = new SourceChanges { Changed = !_hasSourceVersions || membership != _membershipVersion
            || sourceVersions != _sourceVersions || _window.UsesVersions };
        changes.Version = _window.LastVersion;
        _changed.Clear(); _indices.Clear(); _ranges.Clear();
        if (changes.Changed)
        {
            _seen.Clear();
            if (useLifecycleChanges)
                foreach (Entity entity in lifecycle!.RemovedPoints) _versions.Remove(entity);
            var read = new ReadRows(this, useLifecycleChanges);
            _world.ExecuteReadQuery(_rows, baseline, ref read, static (scoped QueryCursor rows, ref ReadRows input) => input.Read(rows));
            changes.Version = read.Version;
        }
        _states.Memory?.EnsureRows(1);
        _backend.Begin(_commands, new(0, 0, 8));
        if (_indices.Count != 0)
        {
            _states.BeginWrite(_backend,_commands,PipelineSync.Copy,ResourceAccess.CopyDestination);
            _indices.Sort();
            int first = 0;
            while (first < _indices.Count)
            {
                int last = first;
                while (last + 1 < _indices.Count && _indices[last + 1] == _indices[last] + 1) last++;
                ulong offset = (ulong)(_indices[first] * Unsafe.SizeOf<TState>());
                _ranges.Add(new(offset, (ulong)((last - first + 1) * Unsafe.SizeOf<TState>())));
                first = last + 1;
            }
            _upload.Copy(_commands, _states.States, CollectionsMarshal.AsSpan(_ranges));
            _backend.Barrier(_commands, new BufferBarrier(_states.States, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        }
        using RecordedCommands recorded = _backend.End(_commands);
        QueueCompletion completed = _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
        foreach (var change in _changed) _versions[change.Entity] = change.Version;
        if (changes.Changed && !useLifecycleChanges)
        {
            _removed.Clear();
            foreach (Entity entity in _versions.Keys) if (_window.UsesVersions ? !_world.IsAlive(entity) : !_seen.Contains(entity)) _removed.Add(entity);
            foreach (Entity entity in _removed) _versions.Remove(entity);
        }
        _hasSourceVersions = true; _membershipVersion = membership; _sourceVersions = sourceVersions; _window.Accept(changes.Version);
        return completed;
    }
    private struct SourceChanges
    {
        internal bool Changed;
        internal uint Version;
    }
    private readonly record struct RowVersion(int Index, uint Version);
    private ref struct ReadRows(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input, bool useLifecycleChanges)
    {
        internal uint Version;
        internal void Read(scoped QueryCursor rows)
        {
            Version = rows.CurrentSystemVersion;
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                if (!chunk.Has<DeviceConstraintBinding<TData>>() || !chunk.Has<DeviceAvbdState<TState>>())
                {
                    if (useLifecycleChanges)
                        foreach (int index in chunk.RowIndices) input._versions.Remove(chunk.Entities[index]);
                    continue;
                }
                bool changed = input._window.UsesVersions || !chunk.Has<TComponent>() || chunk.HasChangedSinceLastSystemVersion<TComponent>()
                    || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<TData>>()
                    || chunk.HasChangedSinceLastSystemVersion<DeviceAvbdState<TState>>();
                if (!changed)
                {
                    if (useLifecycleChanges) continue;
                    ReadOnlySpan<Entity> entities = chunk.Entities;
                    foreach (int index in chunk.RowIndices)
                    {
                        Entity entity = entities[index];
                        if (!input._versions.ContainsKey(entity)) { changed = true; break; }
                        input._seen.Add(entity);
                    }
                }
                if (!changed) continue;
                foreach (QueryRow row in chunk.Rows)
                {
                    if (!row.TryRead<DeviceConstraintBinding<TData>>(out var binding) || !ReferenceEquals(binding.Data, input._parameters)
                        || !row.TryRead<DeviceAvbdState<TState>>(out var state) || !ReferenceEquals(state.States, input._states)
                        || !row.Has<TComponent>())
                    {
                        if (useLifecycleChanges) input._versions.Remove(row.Entity);
                        continue;
                    }
                    Entity entity = row.Entity;
                    if (!useLifecycleChanges) input._seen.Add(entity);
                    var version = new RowVersion(binding.Index, row.WriteVersion<TComponent>());
                    if (input._versions.TryGetValue(entity, out RowVersion previous) && previous == version) continue;
                    TState data = input._source.Read(in row.ReadRef<TComponent>());
                    input._states.Memory?.EnsureRows(binding.Index + 1);
                    MemoryMarshal.Write(input._upload.Row(binding.Index), in data);
                    input._changed.Add((entity, version)); input._indices.Add(binding.Index);
                }
            }
        }
    }
    private readonly struct Transfer(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> inputs, PhysicsStepContext context, DeviceContactMatches? changes) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._backend;
        public Device Device => inputs._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => inputs.Submit(in context, changes);
    }
    public void Dispose() { _world.ReleaseQuery(_rows); _commands.Dispose(); _upload.Dispose(); }
}

public readonly struct UploadDeviceAvbdState<TComponent, TData, TState, TSource>(DeviceAvbdStateInputs<TComponent, TData, TState, TSource> input, DeviceContactMatches? changes = null)
    : IDevicePhysicsStage where TComponent : unmanaged, IComponent where TData : unmanaged where TState : unmanaged
    where TSource : struct, IDeviceAvbdStateSource<TComponent, TState>
{
    public object InputKey => input;
    public bool CanBatch => true;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => input.Schedule(in context, dependency, changes);
}

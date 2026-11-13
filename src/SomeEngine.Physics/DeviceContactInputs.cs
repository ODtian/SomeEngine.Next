using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>An explicit CPU-to-device handoff of one completed contact group.</summary>
public sealed class DeviceContactInputs<TState> : IDisposable where TState : unmanaged
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _world;
    private readonly ContactManifolds<TState> _source;
    private readonly DeviceContactManifolds<TState> _destination;
    private readonly Query _bodies;
    private readonly CommandContext _commands;
    private Buffer _upload;
    private uint _inputRevision;
    public DeviceContactInputs(IGraphicsBackend backend, Device device, World world, ContactManifolds<TState> source,
        DeviceContactManifolds<TState> destination)
    {
        _backend = backend; _device = device; _world = world; _source = source; _destination = destination;
        _bodies = world.Query(new QueryDescription().With<DeviceBodyBinding>());
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact executor input"));
        _upload = backend.CreateBuffer(device, new(12, BufferUsages.CopySource), MemoryType.Upload);
    }
    public UploadDeviceContactInputs<TState> Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(_world);
        DeviceContactColumns current = _destination.Current;
        JobResourceAccess[] accesses = [_source.ReadAccess,..current.GetWriteAccesses(scheduler),.._destination.GetInitializationAccesses(scheduler),
            ..((WorldJobs)_world.Execution!).CollectAccess<BodyAccess>(_bodies),
            JobResourceAccess.Write(scheduler.GetResourceToken(this)),
            JobResourceAccess.Write(scheduler.GetResourceToken(_destination.DispatchValues.Buffer))];
        return DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, current), accesses, dependency);
    }
    private readonly struct BodyAccess : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceBodyBinding>(true)];
        public void Execute(SystemChunk rows) { }
    }
    private QueueCompletion Submit(DeviceContactColumns target)
    {
        ContactColumns source = _source.Columns;
        uint revision = ++_inputRevision;
        ulong bytes = 36;
        foreach (DeviceContactColumn column in target.Values) bytes += Length(column, source) * column.Stride;
        if (_upload.Info.Size < bytes)
        { _upload.Dispose(); _upload = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopySource), MemoryType.Upload); }
        using (MappedBuffer mapping = _backend.Map(_upload, MapType.Write, BufferRange.Whole))
        {
            int offset = 0;
            using ReadLookup<DeviceBodyBinding> bodies = _world.OpenRead<DeviceBodyBinding>();
            foreach (DeviceContactColumn column in target.Values)
            {
                int length = (int)(Length(column, source) * column.Stride);
                Span<byte> value = mapping.Bytes.Slice(offset, length);
                switch (column.Name)
                {
                    case "Counts":
                        var counts = MemoryMarshal.Cast<byte, uint>(value);
                        counts[0] = (uint)source.PairCount; counts[1] = (uint)source.PointCount; counts[2] = (uint)source.BodyLinkCount; break;
                    case "Pairs": Copy(source.Pairs, value); break;
                    case "PairIndices": Copy(source.PairIndices, value); break;
                    case "FirstFeatures": Copy(source.FirstFeatures, value); break;
                    case "SecondFeatures": Copy(source.SecondFeatures, value); break;
                    case "PointIndices": Copy(source.PointIndices, value); break;
                    case "FirstLocalPoints": Copy(source.FirstLocalPoints, value); break;
                    case "SecondLocalPoints": Copy(source.SecondLocalPoints, value); break;
                    case "WorldPoints": Copy(source.WorldPoints, value); break;
                    case "Normals": Copy(source.Normals, value); break;
                    case "Separations": Copy(source.Separations, value); break;
                    case "BodyAnchorDeltas": Copy(source.BodyAnchorDeltas, value); break;
                    case "Predictions": Copy(source.Predictions, value); break;
                    case "Flags": Copy(source.Flags, value); break;
                    case "BodyStarts": Copy(source.BodyStarts, value); break;
                    case "BodyCounts": Copy(source.BodyCounts, value); break;
                    case "FirstBodyCounts": Copy(source.FirstBodyCounts, value); break;
                    case "Materials": Copy(source.Materials, value); break;
                    case "Responses": Copy(source.Responses, value); break;
                    case "BodyLinks": Copy(source.BodyLinks, value); break;
                    case "States": Copy(_source.States, value); break;
                    case "SupportStates": value.Clear(); break;
                    case "InputVersions": MemoryMarshal.Cast<byte, uint>(value).Fill(revision); break;
                    case "BodyStateIndices":
                        var indices = MemoryMarshal.Cast<byte, uint>(value);
                        for (int link = 0; link < source.BodyLinkCount; link++) indices[link] = (uint)bodies.Read(source.BodyLinks[link].Body).Index;
                        break;
                }
                offset += length;
            }
            Span<uint> arguments = MemoryMarshal.Cast<byte, uint>(mapping.Bytes.Slice(offset, 36));
            WriteGroups(arguments, 0, source.PairCount); WriteGroups(arguments, 1, source.PointCount); WriteGroups(arguments, 2, 0);
            mapping.Flush(new(0, bytes));
        }
        _backend.Begin(_commands, new(0, 0, 8));
        _destination.Initialize(_backend, _commands);
        ulong position = 0;
        foreach (DeviceContactColumn column in target.Values)
        {
            ulong length = Length(column, source) * column.Stride;
            if (length == 0) continue;
            column.Transition(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopyDestination);
            _backend.CopyBuffer(_commands, new(_upload, position, column.Buffer, 0, length));
            column.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            position += length;
        }
        _destination.DispatchValues.Transition(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopyDestination);
        _backend.CopyBuffer(_commands, new(_upload, position, _destination.DispatchValues.Buffer, 0, 36));
        _destination.DispatchValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private static void WriteGroups(Span<uint> arguments, int entry, int count)
    {
        int groups = (count + 63) / 64;
        arguments[entry * 3] = (uint)Math.Max(1, Math.Min(groups, DevicePhysicsDispatch.GroupsPerRow));
        arguments[entry * 3 + 1] = (uint)Math.Max(1, (groups + DevicePhysicsDispatch.GroupsPerRow - 1) / DevicePhysicsDispatch.GroupsPerRow);
        arguments[entry * 3 + 2] = 1;
    }
    private static ulong Length(DeviceContactColumn column, ContactColumns source) => (ulong)(column.Name switch
    { "Counts" => 3, "Pairs" => source.PairCount, "BodyLinks" or "BodyStateIndices" => source.BodyLinkCount, _ => source.PointCount });
    private static void Copy<T>(Span<T> values, Span<byte> target) where T : unmanaged => MemoryMarshal.AsBytes(values).CopyTo(target);
    private readonly struct Transfer(DeviceContactInputs<TState> inputs, DeviceContactColumns target) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => inputs._backend;
        public Device Device => inputs._device;
        public SomeEngine.Graphics.Queue Queue => Backend.GetQueue(Device, QueueType.Compute);
        public QueueCompletion Submit() => inputs.Submit(target);
    }
    public void Dispose() { _commands.Dispose(); _upload.Dispose(); }
}
public readonly struct UploadDeviceContactInputs<TState>(DeviceContactInputs<TState> inputs) : IDevicePhysicsStage where TState : unmanaged
{
    public object InputKey => inputs;
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}

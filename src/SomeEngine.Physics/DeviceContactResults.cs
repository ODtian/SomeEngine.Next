using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Explicitly transfers a completed device contact prefix into the common CPU contact columns.</summary>
public sealed class DeviceContactResults : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly ContactColumns _columns;
    private readonly CommandContext _commands;
    private readonly Buffer _counts;
    private Buffer _values;
    private int _pairs, _points, _links;
    public DeviceContactResults(IGraphicsBackend backend, Device device, Scheduler scheduler)
    {
        _backend = backend; _device = device; _columns = new(scheduler);
        _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1, Label: "Contact columns readback"));
        _counts = backend.CreateBuffer(device, new(12, BufferUsages.CopyDestination), MemoryType.Readback);
        _values = backend.CreateBuffer(device, new(4, BufferUsages.CopyDestination), MemoryType.Readback);
    }
    public PhysicsContacts Contacts => _columns.Contacts;
    public ReadDeviceContactResults<TState> Readback<TState>(DeviceContactManifolds<TState> contacts) where TState : unmanaged => new(this, contacts);
    internal JobHandle Schedule(in PhysicsStepContext context, DeviceContactColumns source, JobHandle dependency)
    {
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess state = JobResourceAccess.Write(scheduler.GetResourceToken(this));
        JobResourceAccess[] accesses = [.. source.GetReadAccesses(scheduler), state];
        JobHandle copied = DevicePhysicsJobs.Schedule(scheduler, new Transfer(this, source), accesses, dependency);
        JobHandle mapped = scheduler.Schedule(new Map(this, source), [state, _columns.WriteAccess], copied);
        _columns.SetStep(new(context.Number, context.StartTime, context.EndTime, mapped));
        return mapped;
    }
    private async ValueTask<QueueCompletion> SubmitAsync(DeviceContactColumns source)
    {
        _backend.Begin(_commands, new(0, 0, 8));
        source.CountValues.Transition(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopySource);
        _backend.CopyBuffer(_commands, new(source.Counts, 0, _counts, 0, 12));
        source.CountValues.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        QueueCompletion completion = Submit();
        await _backend.WaitAsync(completion).ConfigureAwait(false);
        using (MappedBuffer mapping = _backend.Map(_counts, MapType.Read, BufferRange.Whole))
        {
            mapping.Invalidate(mapping.Range);
            ReadOnlySpan<uint> counts = MemoryMarshal.Cast<byte, uint>(mapping.Bytes);
            _pairs = (int)counts[0]; _points = (int)counts[1]; _links = (int)counts[2];
        }
        ulong bytes = 0;
        foreach (DeviceContactColumn column in source.Values)
            if (IsCommon(column)) bytes += Bytes(column);
        if (bytes == 0) return completion;
        if (_values.Info.Size < bytes)
        {
            _values.Dispose();
            _values = _backend.CreateBuffer(_device, new(bytes, BufferUsages.CopyDestination), MemoryType.Readback);
        }
        _backend.Begin(_commands, new(0, 0, 8));
        ulong offset = 0;
        foreach (DeviceContactColumn column in source.Values)
        {
            if (!IsCommon(column)) continue;
            ulong length = Bytes(column);
            if (length == 0) continue;
            column.Transition(_backend, _commands, PipelineSync.Copy, ResourceAccess.CopySource);
            _backend.CopyBuffer(_commands, new(column.Buffer, 0, _values, offset, length));
            column.Transition(_backend, _commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            offset += length;
        }
        return Submit();
    }
    private QueueCompletion Submit()
    {
        using RecordedCommands recorded = _backend.End(_commands);
        return _backend.Submit(_backend.GetQueue(_device, QueueType.Compute), new([], [], [recorded], [], []));
    }
    private static bool IsCommon(DeviceContactColumn column) => column.Name is not ("Counts" or "States" or "BodyStateIndices" or "SupportStates" or "InputVersions");
    private ulong Bytes(DeviceContactColumn column) => (ulong)(column.Name == "Pairs" ? _pairs : column.Name == "BodyLinks" ? _links : _points) * column.Stride;
    private void MapValues(DeviceContactColumns source)
    {
        _columns.Resize(_pairs, _points, _links);
        if (_pairs + _points + _links == 0) { _columns.RebuildBodyPairs(); return; }
        using MappedBuffer mapping = _backend.Map(_values, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        int offset = 0;
        foreach (DeviceContactColumn column in source.Values)
        {
            if (!IsCommon(column)) continue;
            int bytes = (int)Bytes(column);
            ReadOnlySpan<byte> value = mapping.Bytes.Slice(offset, bytes);
            switch (column.Name)
            {
                case "Pairs": Copy(value, _columns.Pairs); break;
                case "PairIndices": Copy(value, _columns.PairIndices); break;
                case "FirstFeatures": Copy(value, _columns.FirstFeatures); break;
                case "SecondFeatures": Copy(value, _columns.SecondFeatures); break;
                case "PointIndices": Copy(value, _columns.PointIndices); break;
                case "FirstLocalPoints": Copy(value, _columns.FirstLocalPoints); break;
                case "SecondLocalPoints": Copy(value, _columns.SecondLocalPoints); break;
                case "WorldPoints": Copy(value, _columns.WorldPoints); break;
                case "Normals": Copy(value, _columns.Normals); break;
                case "Separations": Copy(value, _columns.Separations); break;
                case "BodyAnchorDeltas": Copy(value, _columns.BodyAnchorDeltas); break;
                case "Predictions": Copy(value, _columns.Predictions); break;
                case "Flags": Copy(value, _columns.Flags); break;
                case "BodyStarts": Copy(value, _columns.BodyStarts); break;
                case "BodyCounts": Copy(value, _columns.BodyCounts); break;
                case "FirstBodyCounts": Copy(value, _columns.FirstBodyCounts); break;
                case "Materials": Copy(value, _columns.Materials); break;
                case "Responses": Copy(value, _columns.Responses); break;
                case "BodyLinks": Copy(value, _columns.BodyLinks); break;
            }
            offset += bytes;
        }
        _columns.RebuildBodyPairs();
    }
    private static void Copy<T>(ReadOnlySpan<byte> source, Span<T> target) where T : unmanaged => MemoryMarshal.Cast<byte, T>(source).CopyTo(target);
    public void Dispose() { _columns.Dispose(); _commands.Dispose(); _counts.Dispose(); _values.Dispose(); }
    private readonly struct Transfer(DeviceContactResults results, DeviceContactColumns source) : IDevicePhysicsSubmission
    {
        public IGraphicsBackend Backend => results._backend;
        public Device Device => results._device;
        public QueueCompletion Submit() => throw new NotSupportedException();
        public ValueTask<QueueCompletion> SubmitAsync() => results.SubmitAsync(source);
    }
    private readonly struct Map(DeviceContactResults results, DeviceContactColumns source) : IJob
    { public void Execute() => results.MapValues(source); }
}
public readonly struct ReadDeviceContactResults<TState>(DeviceContactResults results, DeviceContactManifolds<TState> contacts) : IPhysicsStage where TState : unmanaged
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => results.Schedule(in context, contacts.Current, dependency);
}

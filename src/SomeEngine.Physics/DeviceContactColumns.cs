using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

public readonly record struct DeviceContactCapacity(int Pairs, int Points, int BodyLinks)
{
    public bool SupportStates { get; init; }
}

/// <summary>The device columns of one contact step, including its solver state.</summary>
public sealed class DeviceContactColumns : IDisposable
{
    private readonly DeviceContactColumn[] _columns;
    internal readonly DeviceContactColumn? SupportValues;
    internal readonly DeviceContactColumn CountValues, PairValues, PairIndexValues,
        FirstFeatureValues, SecondFeatureValues, PointIndexValues, FirstLocalPointValues,
        SecondLocalPointValues, WorldPointValues, NormalValues, SeparationValues, AnchorDeltaValues,
        PredictionValues, FlagValues, InputVersionValues, BodyStartValues, BodyCountValues, FirstBodyCountValues,
        MaterialValues, ResponseValues, BodyLinkValues, BodyStateIndexValues, StateValues;

    internal DeviceContactColumns(IGraphicsBackend backend, Device device, DeviceContactCapacity capacity,
        uint stateStride)
    {
        Capacity = capacity;
        var columns = new List<DeviceContactColumn>();
        try
        {
            CountValues = Add("Counts", 3, 4);
            PairValues = Add("Pairs", capacity.Pairs, 32);
            PairIndexValues = Add("PairIndices", capacity.Points, 4);
            FirstFeatureValues = Add("FirstFeatures", capacity.Points, 4);
            SecondFeatureValues = Add("SecondFeatures", capacity.Points, 4);
            PointIndexValues = Add("PointIndices", capacity.Points, 4);
            FirstLocalPointValues = Add("FirstLocalPoints", capacity.Points, 12);
            SecondLocalPointValues = Add("SecondLocalPoints", capacity.Points, 12);
            WorldPointValues = Add("WorldPoints", capacity.Points, 12);
            NormalValues = Add("Normals", capacity.Points, 12);
            SeparationValues = Add("Separations", capacity.Points, 4);
            AnchorDeltaValues = Add("BodyAnchorDeltas", capacity.Points, 12);
            PredictionValues = Add("Predictions", capacity.Points, 16);
            FlagValues = Add("Flags", capacity.Points, 4);
            InputVersionValues = Add("InputVersions", capacity.Points, 8);
            BodyStartValues = Add("BodyStarts", capacity.Points, 4);
            BodyCountValues = Add("BodyCounts", capacity.Points, 4);
            FirstBodyCountValues = Add("FirstBodyCounts", capacity.Points, 4);
            MaterialValues = Add("Materials", capacity.Points, 24);
            ResponseValues = Add("Responses", capacity.Points, 48);
            BodyLinkValues = Add("BodyLinks", capacity.BodyLinks, 28);
            BodyStateIndexValues = Add("BodyStateIndices", capacity.BodyLinks, 4);
            StateValues = Add("States", capacity.Points, stateStride);
            if (capacity.SupportStates) SupportValues = Add("SupportStates", capacity.Points, 64);
            _columns = columns.ToArray();
        }
        catch
        {
            foreach (DeviceContactColumn column in columns) column.Dispose();
            throw;
        }

        DeviceContactColumn Add(string name, int count, uint stride, BufferUsages usages = 0)
        {
            var column = new DeviceContactColumn(backend, device, name, count, stride);
            columns.Add(column);
            return column;
        }
    }

    internal ReadOnlySpan<DeviceContactColumn> Values => _columns;
    public DeviceContactCapacity Capacity { get; }
    public Buffer Counts => CountValues.Buffer;
    public Buffer Pairs => PairValues.Buffer;
    public Buffer PairIndices => PairIndexValues.Buffer;
    public Buffer FirstFeatures => FirstFeatureValues.Buffer;
    public Buffer SecondFeatures => SecondFeatureValues.Buffer;
    public Buffer PointIndices => PointIndexValues.Buffer;
    public Buffer FirstLocalPoints => FirstLocalPointValues.Buffer;
    public Buffer SecondLocalPoints => SecondLocalPointValues.Buffer;
    public Buffer WorldPoints => WorldPointValues.Buffer;
    public Buffer Normals => NormalValues.Buffer;
    public Buffer Separations => SeparationValues.Buffer;
    public Buffer BodyAnchorDeltas => AnchorDeltaValues.Buffer;
    public Buffer Predictions => PredictionValues.Buffer;
    public Buffer Flags => FlagValues.Buffer;
    public Buffer InputVersions => InputVersionValues.Buffer;
    public Buffer BodyStarts => BodyStartValues.Buffer;
    public Buffer BodyCounts => BodyCountValues.Buffer;
    public Buffer FirstBodyCounts => FirstBodyCountValues.Buffer;
    public Buffer Materials => MaterialValues.Buffer;
    public Buffer Responses => ResponseValues.Buffer;
    public Buffer BodyLinks => BodyLinkValues.Buffer;
    public Buffer BodyStateIndices => BodyStateIndexValues.Buffer;
    public Buffer States => StateValues.Buffer;
    public Buffer? SupportStates => SupportValues?.Buffer;

    public void AddBindings(Dictionary<string, ResourceBinding[]> bindings, string prefix = "DeviceContact",
        bool writable = true)
    {
        if (prefix == "DeviceContact" && SupportValues is { } supports)
            bindings["DeviceSupportContactStates"] = [ResourceBinding.WritableBuffer(supports.WriteView)];
        foreach (DeviceContactColumn column in _columns)
            bindings[prefix + column.Name] = [writable
                ? ResourceBinding.WritableBuffer(column.WriteView)
                : ResourceBinding.ReadOnlyBuffer(column.ReadView)];
    }

    public JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => GetAccesses(scheduler, false);
    public JobResourceAccess[] GetWriteAccesses(Scheduler scheduler) => GetAccesses(scheduler, true);

    private JobResourceAccess[] GetAccesses(Scheduler scheduler, bool write)
    {
        var accesses = new JobResourceAccess[_columns.Length];
        for (int index = 0; index < accesses.Length; index++)
        {
            Buffer buffer = _columns[index].Buffer;
            JobResourceToken resource = scheduler.GetResourceToken(buffer);
            accesses[index] = write ? JobResourceAccess.Write(resource, 0, (long)buffer.Info.Size)
                : JobResourceAccess.Read(resource, 0, (long)buffer.Info.Size);
        }
        return accesses;
    }

    internal void BeginWrite(IGraphicsBackend backend, CommandContext commands)
    {
        foreach (DeviceContactColumn column in _columns)
            column.Transition(backend, commands, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
    }

    internal void BeginRead(IGraphicsBackend backend, CommandContext commands)
    {
        foreach (DeviceContactColumn column in _columns)
            column.Transition(backend, commands, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
    }

    public void Dispose()
    {
        foreach (DeviceContactColumn column in _columns) column.Dispose();
    }
}

internal sealed class DeviceContactColumn : IDisposable
{
    private PipelineSync _sync;
    private ResourceAccess _access;
    internal readonly Buffer Buffer;
    internal readonly BufferUav WriteView;
    internal readonly BufferSrv ReadView;
    internal string Name { get; }
    internal uint Stride { get; }

    internal DeviceContactColumn(IGraphicsBackend backend, Device device, string name, int count, uint stride, BufferUsages usages = 0)
    {
        Name = name; Stride = stride;
        Buffer = backend.CreateBuffer(device, new((ulong)count * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination | usages,
            "Contact " + name), MemoryType.DeviceLocal);
        WriteView = backend.CreateBufferUav(device, new(Buffer, BufferRange.Whole, StructureStride: stride));
        ReadView = backend.CreateBufferSrv(device, new(Buffer, BufferRange.Whole, StructureStride: stride));
        _sync = Buffer.InitialSync; _access = Buffer.InitialAccess;
    }

    internal void Transition(IGraphicsBackend backend, CommandContext commands, PipelineSync sync, ResourceAccess access)
    {
        backend.Barrier(commands, new BufferBarrier(Buffer, _sync, sync, _access, access));
        _sync = sync; _access = access;
    }

    public void Dispose() { ReadView.Dispose(); WriteView.Dispose(); Buffer.Dispose(); }
}

namespace SomeEngine.RenderGraph;

public interface IGraphReadbackReceiver
{
    /// <summary>Consumes completed bytes. The span is valid for the duration of this call.</summary>
    void OnReadback(ReadOnlySpan<byte> bytes, ulong tag);
}

public readonly struct GraphReadbackId : IEquatable<GraphReadbackId>
{
    private readonly GraphIdentity _value;
    internal GraphReadbackId(GraphIdentity value) => _value = value;
    internal GraphIdentity Value => _value;
    public bool Equals(GraphReadbackId other) => _value.Equals(other._value);
    public override bool Equals(object? value) => value is GraphReadbackId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(GraphReadbackId left, GraphReadbackId right) => left.Equals(right);
    public static bool operator !=(GraphReadbackId left, GraphReadbackId right) => !left.Equals(right);
}

internal readonly record struct ReadbackDescription(
    GraphBufferId Source, BufferRange Range, IGraphReadbackReceiver Receiver)
{
    internal GraphBufferInput? Input { get; init; }
}

internal sealed class ReadbackCommands : PassCallbackStorage, IStaticPassDataStorage<ReadbackDescription>
{
    internal ReadbackDescription Description;
    internal ulong Tag;
    internal override Type FrameDataType => typeof(void);
    public void SetStaticData(in ReadbackDescription value) => Description = value;
    internal override void ClearData() => Description = default;

    internal override void Record(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        GraphBufferSlice destination = frame.GetReadbackDestination(this);
        var commands = new CopyPassCommandScope(frame, passIndex, context);
        BufferRegion source = Description.Input is { } input ? commands.GetBufferRegion(input) :
            new BufferRegion(commands.GetBuffer(Description.Source), BufferRange.Whole);
        commands.CopyBuffer(new BufferCopy(source.Buffer, checked(source.Range.Offset + Description.Range.Offset),
            commands.GetBuffer(destination.Buffer), destination.Range.Offset, destination.Range.Size));
        commands.Finish();
    }
}

public ref partial struct RenderGraphEdit
{
    public GraphReadbackId AddReadback(GraphBufferId source, BufferRange range,
        IGraphReadbackReceiver receiver, string label, GraphRegionId region = default,
        in PassOptions options = default)
    {
        DevelopmentCheck.Require(receiver is not null, "A readback requires a receiver.");
        DevelopmentCheck.Require(source.Value.ScopeId == Graph.Identity && Structure.Buffers.Contains(source.Value),
            "The readback source must belong to this graph.");
        GraphBuffer buffer = Structure.Buffers.Get(source.Value);
        DevelopmentCheck.Require((buffer.Description.Usages & BufferUsages.CopySource) != 0,
            "A readback source requires CopySource usage.");
        range = GraphStructureIndex.ResolveRange(range, buffer.Description.Size);
        DevelopmentCheck.Require(range.Size != 0 && range.Size <= int.MaxValue,
            "A readback range must fit in a byte span.");
        GraphPassId pass = AddPersistentFramePass(label, GraphPassKind.Copy,
            PassQueueSelection.AnyCompatible(), new ReadbackDescription(source, range, receiver),
            options with { Culling = PassCullingMode.NeverCull }, null, VariableLayoutReflection.Null, [],
            static (ref PassDefinition pass, ref ReadbackDescription data) =>
                pass.Read(data.Source, data.Range, PipelineSync.Copy, ResourceAccess.CopySource),
            new ReadbackCommands(), region);
        return new GraphReadbackId(pass.Value);
    }

    /// <summary>Reads a bound frame input. The explicit range is relative to the input slice.</summary>
    public GraphReadbackId AddReadback(GraphBufferInput source, BufferRange range,
        IGraphReadbackReceiver receiver, string label, GraphRegionId region = default,
        in PassOptions options = default)
    {
        DevelopmentCheck.Require(receiver is not null, "A readback requires a receiver.");
        DevelopmentCheck.Require(range.IsWhole || range.Size != 0 && range.Size <= int.MaxValue,
            "An input readback range must fit a byte span.");
        GraphPassId pass = AddPersistentFramePass(label, GraphPassKind.Copy,
            PassQueueSelection.AnyCompatible(), new ReadbackDescription(default, range, receiver) { Input = source },
            options with { Culling = PassCullingMode.NeverCull }, null, VariableLayoutReflection.Null, [],
            static (ref PassDefinition pass, ref ReadbackDescription data) =>
                pass.Read(data.Input!, data.Range, PipelineSync.Copy, ResourceAccess.CopySource),
            new ReadbackCommands(), region);
        return new GraphReadbackId(pass.Value);
    }

    /// <summary>Orders a mutable producer's next write after capturing its current value.</summary>
    public void OrderAfter(GraphPassId pass, GraphReadbackId predecessor) =>
        OrderAfter(pass, new GraphPassId(predecessor.Value));

    /// <summary>Orders a readback after the producer whose completed value it captures.</summary>
    public void OrderAfter(GraphReadbackId readback, GraphPassId predecessor) =>
        OrderAfter(new GraphPassId(readback.Value), predecessor);

    public void Remove(GraphReadbackId readback) => Remove(new GraphPassId(readback.Value));
}

public ref partial struct RenderGraphFrame
{
    /// <summary>Captures this frame's buffer range and delivers its bytes after GPU completion.</summary>
    public GraphReadbackId AddReadback(GraphBufferSlice source, IGraphReadbackReceiver receiver,
        string label, ulong tag = 0, GraphRegionId region = default) =>
        State.AddReadback(source, receiver, label, tag, region);

    public void OrderAfter(GraphReadbackId readback, GraphPassId predecessor) =>
        State.OrderAfter(new GraphPassId(readback.Value), predecessor);

    public void OrderAfter(GraphPassId pass, GraphReadbackId predecessor) =>
        State.OrderAfter(pass, new GraphPassId(predecessor.Value));

    public void SetReadbackTag(GraphReadbackId readback, ulong tag) => State.SetReadbackTag(readback, tag);
    public void SetReadbackEnabled(GraphReadbackId readback, bool enabled) =>
        State.SetPassEnabled(new GraphPassId(readback.Value), enabled);
}

internal sealed partial class RenderGraphFrameState
{
    private readonly HashSet<GraphIdentity> _savedReadbacks = [];
    private readonly List<(GraphPassId Pass, Buffer Buffer, BufferRange Range,
        IGraphReadbackReceiver Receiver, ulong Tag)> _frameReadbacks = [];

    internal GraphReadbackId AddReadback(GraphBufferSlice source, IGraphReadbackReceiver receiver,
        string label, ulong tag, GraphRegionId region)
    {
        EnsureEditable();
        ValidateResourceIdentity(source.Buffer.Value, true);
        ulong size = source.Buffer.Value.ScopeId == Graph.Identity
            ? StructureIndex.Structure.Buffers.Get(source.Buffer.Value).Description.Size
            : _dynamicBuffers[source.Buffer.Value.Index].Description.Size;
        BufferRange range = GraphStructureIndex.ResolveRange(source.Range, size);
        var allocation = Resources.GetBufferTransfers(Graph).ReserveReadback(range.Size);
        GraphBufferId destination = Import(allocation.Destination.Buffer, allocation.BoundaryStates);
        var data = (Source: source with { Range = range },
            Destination: new GraphBufferSlice(destination, allocation.Destination.Range));
        GraphPassId pass = AddCopyPass(label, PassQueueSelection.AnyCompatible(), data,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref (GraphBufferSlice Source, GraphBufferSlice Destination) data) =>
            {
                access.Read(data.Source.Buffer, data.Source.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(data.Destination.Buffer, data.Destination.Range, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphBufferSlice Source, GraphBufferSlice Destination) data) =>
                commands.CopyBuffer(new(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
                    commands.GetBuffer(data.Destination.Buffer), data.Destination.Range.Offset, data.Source.Range.Size)), region);
        _frameReadbacks.Add((pass, allocation.Destination.Buffer, allocation.Destination.Range, receiver, tag));
        return new(pass.Value);
    }

    internal bool TryGetReadbackCompletion(GraphReadbackId readback, out ulong completedFrame)
    {
        bool submitted = _savedReadbacks.Contains(readback.Value);
        completedFrame = submitted ? Identity : 0;
        return submitted;
    }

    private readonly Dictionary<ReadbackCommands, (GraphPassId Pass, GraphBufferSlice Destination, ulong Tag)> _readbacks = [];

    internal void SetReadbackTag(GraphReadbackId readback, ulong tag)
    {
        EnsureEditable();
        ValidateStatic(readback.Value, StructureIndex.Structure.Passes);
        ReadbackCommands? commands = StructureIndex.Structure.Passes.Get(readback.Value).CallbackStorage as ReadbackCommands;
        DevelopmentCheck.Require(commands is not null, "The identity must select a readback.");
        commands!.Tag = tag;
    }

    private void PrepareReadbacks()
    {
        GraphStructure structure = StructureIndex.Structure;
        foreach (GraphIdentity identity in StructureIndex.ReadbackPasses)
        {
            GraphPass pass = structure.Passes.Get(identity);
            if (!IsPassEnabled(identity)) continue;
            var commands = (ReadbackCommands)pass.CallbackStorage!;
            FrameBufferTransfers transfers = Resources.GetBufferTransfers(Graph);
            ulong size = commands.Description.Range.Size;
            if (commands.Description.Input is { } input)
            {
                GraphBufferSlice source = ResolveBufferInput(input);
                ulong bufferSize = source.Buffer.Value.ScopeId == Graph.Identity
                    ? structure.Buffers.Get(source.Buffer.Value).Description.Size
                    : _dynamicBuffers[source.Buffer.Value.Index].Description.Size;
                BufferRange sourceRange = GraphStructureIndex.ResolveRange(source.Range, bufferSize);
                size = GraphStructureIndex.ResolveRange(commands.Description.Range, sourceRange.Size).Size;
                DevelopmentCheck.Require(size <= int.MaxValue, "A readback range must fit a byte span.");
            }
            var allocation = transfers.ReserveReadback(size);
            BufferRegion destination = allocation.Destination;
            GraphBufferId buffer = Import(destination.Buffer, allocation.BoundaryStates);
            GraphPassId id = new(identity);
            PassDefinition definition = GetPass(id);
            definition.Write(buffer, destination.Range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                WriteCoverage.Complete);
            _readbacks.Add(commands, (id, new GraphBufferSlice(buffer, destination.Range), commands.Tag));
        }
    }

    internal GraphBufferSlice GetReadbackDestination(ReadbackCommands commands) => _readbacks[commands].Destination;

    private void SaveReadbackResults()
    {
        foreach (var data in _frameReadbacks)
        {
            if (!_executor.TryGetPassCompletion(data.Pass, out _)) continue;
            Resources.GetBufferTransfers(Graph).AddReadbackResult(data.Buffer, data.Range, data.Receiver, data.Tag);
            _savedReadbacks.Add(data.Pass.Value);
        }
        foreach (var (commands, data) in _readbacks)
        {
            if (!_executor.TryGetPassCompletion(data.Pass, out _)) continue;
            Buffer buffer = _dynamicBuffers[data.Destination.Buffer.Value.Index].Resource!;
            Resources.GetBufferTransfers(Graph).AddReadbackResult(buffer, data.Destination.Range,
                commands.Description.Receiver, data.Tag);
            _savedReadbacks.Add(data.Pass.Value);
        }
    }
}

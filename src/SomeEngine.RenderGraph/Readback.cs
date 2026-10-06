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
        DevelopmentCheck.Require(source.Value.Owner == Graph.Identity && Structure.Buffers.Contains(source.Value),
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
    public void SetReadbackTag(GraphReadbackId readback, ulong tag) => State.SetReadbackTag(readback, tag);
    public void SetReadbackEnabled(GraphReadbackId readback, bool enabled) =>
        State.SetPassEnabled(new GraphPassId(readback.Value), enabled);
}

internal sealed partial class RenderGraphFrameState
{
    private readonly Dictionary<ReadbackCommands, (GraphPassId Pass, GraphBufferSlice Destination, ulong Tag)> _readbacks = [];

    internal void SetReadbackTag(GraphReadbackId readback, ulong tag)
    {
        EnsureAuthoring();
        ValidateStatic(readback.Value, StructureIndex.Structure.Passes);
        ReadbackCommands? commands = StructureIndex.Structure.Passes.Get(readback.Value).CallbackStorage as ReadbackCommands;
        DevelopmentCheck.Require(commands is not null, "The identity must select a readback.");
        commands!.Tag = tag;
    }

    private void PrepareReadbacks()
    {
        GraphStructure structure = StructureIndex.Structure;
        for (int index = 0; index < structure.Passes.Count; index++)
        {
            GraphPass pass = structure.Passes.Rows[index];
            if (!pass.Enabled || !structure.IsRegionEnabled(pass.Region) ||
                pass.CallbackStorage is not ReadbackCommands commands) continue;
            FrameBufferTransfers transfers = Slot.GetBufferTransfers(Graph);
            ulong size = commands.Description.Range.Size;
            if (commands.Description.Input is { } input)
            {
                GraphBufferSlice source = ResolveBufferInput(input);
                ulong bufferSize = source.Buffer.Value.Owner == Graph.Identity
                    ? structure.Buffers.Get(source.Buffer.Value).Description.Size
                    : _dynamicBuffers[source.Buffer.Value.Slot].Description.Size;
                BufferRange sourceRange = GraphStructureIndex.ResolveRange(source.Range, bufferSize);
                size = GraphStructureIndex.ResolveRange(commands.Description.Range, sourceRange.Size).Size;
                DevelopmentCheck.Require(size <= int.MaxValue, "A readback range must fit a byte span.");
            }
            var allocation = transfers.ReserveReadback(size);
            BufferRegion destination = allocation.Destination;
            GraphBufferId buffer = Import(destination.Buffer, allocation.BoundaryStates);
            GraphPassId id = new(structure.Passes.IdentityAt(Graph.Identity, index));
            PassDefinition definition = GetPass(id);
            definition.Write(buffer, destination.Range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                WriteCoverage.Complete);
            _readbacks.Add(commands, (id, new GraphBufferSlice(buffer, destination.Range), commands.Tag));
        }
    }

    internal GraphBufferSlice GetReadbackDestination(ReadbackCommands commands) => _readbacks[commands].Destination;

    private void SaveReadbackResults()
    {
        foreach (var (commands, data) in _readbacks)
        {
            if (!_executor.TryGetPassCompletion(data.Pass, out _)) continue;
            Buffer buffer = _dynamicBuffers[data.Destination.Buffer.Value.Slot].Resource!;
            Slot.GetBufferTransfers(Graph).AddReadbackResult(buffer, data.Destination.Range,
                commands.Description.Receiver, data.Tag);
        }
    }
}

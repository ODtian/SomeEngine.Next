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
    GraphBufferId Source, BufferRange Range, IGraphReadbackReceiver Receiver);

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
        commands.CopyBuffer(new BufferCopy(commands.GetBuffer(Description.Source), Description.Range.Offset,
            commands.GetBuffer(destination.Buffer), destination.Range.Offset, Description.Range.Size));
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
            var allocation = transfers.ReserveReadback(commands.Description.Range.Size);
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

namespace SomeEngine.RenderGraph;

internal sealed partial class FrameRecording
{
    private PassOrder _executionOrder => Connections.Order;
    private Dictionary<Queue, SortedSet<(long Position, int Pass)>> _queuePasses => Connections.QueuePasses;
    private int[] _sameQueueSuccessor => Connections.SameQueueSuccessor;
    private readonly Dictionary<(GraphAccessTargetKind Kind, int Index), ulong> _resourceLifetimeVersions = [];

    private void ApplySchedule()
    {
        Connections.Update(new FrameConnectionData(this));
        ConnectionsChanged(Connections.ChangedPasses);
        _frame.Graph.ConnectionsChanged(Connections.ChangedPasses, _frame.Resources);
        MaterializeConnections();
    }

    private void SetPassRow(int index, in FramePass row)
    {
        _rebuiltRowCount++;
        if (_passes[index].Identity != row.Identity || _passes[index].Enabled != row.Enabled)
            ChangeBindingViews(row.Identity);
        _passAccesses[index] ??= [];
        _parameterPassChanges.Add(index);
        InvalidateRasterRendering(index);
        ObservePass(index, row);
        FramePass value = row;
        value.Queue = _passes[index].Queue;
        _passes[index] = value;
        if (row.Definition is not null) _rowChanges.Accesses.UnionWith(row.Definition.Accesses);
    }

    private void MaterializeConnections()
    {
        for (int pass = 0; pass < _passes.Count; pass++)
        {
            ref FramePass row = ref CollectionsMarshal.AsSpan(_passes)[pass];
            row.Live = _live[pass];
            if (!ReferenceEquals(row.Queue, Connections.Queues[pass])) ChangeRecording(pass);
            row.Queue = Connections.Queues[pass];
        }
        foreach (var (key, uses) in Connections.ResourceLifetimes)
        {
            if (!ResourceExists(key)) continue;
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                ref FrameBuffer row = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
                row.FirstPass = uses.First; row.LastPass = uses.Last;
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                ref FrameTexture row = ref CollectionsMarshal.AsSpan(_textures)[key.Index];
                row.FirstPass = uses.First; row.LastPass = uses.Last;
            }
            if (_resourceLifetimeVersions.GetValueOrDefault(key) == uses.Version) continue;
            _resourceLifetimeVersions[key] = uses.Version;
            _placementResources.Add(key);
            MaterializeMissingResource(key);
        }
    }

    internal void ConnectionsChanged(IEnumerable<int> passes)
    {
        foreach (int pass in passes)
        {
            if (pass >= _passes.Count) continue;
            ChangeRecording(pass);
            ChangePassSynchronization(pass);
            MarkPassResources(pass, _stateConnectionChanges);
        }
    }
}

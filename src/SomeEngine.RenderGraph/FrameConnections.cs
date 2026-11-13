namespace SomeEngine.RenderGraph;

internal readonly record struct FramePassEdge(int Predecessor, int Consumer);
internal readonly record struct FrameAccessKey(bool Persistent, int Index, uint Generation);
internal readonly record struct FramePassInputs(bool Active, GraphPassKind Kind, PassQueueSelection Queue,
    PassOptions Options, long DeclarationOrdinal, bool Enabled, GraphIdentity Region,
    FrameSectionPosition Position, bool SectionBoundary)
{
    internal static FramePassInputs Read(in FramePass pass) => new(pass.Identity.IsValid, pass.Kind,
        pass.QueuePolicy, pass.Options, pass.DeclarationOrdinal, pass.Enabled, pass.Region,
        pass.SectionPosition, pass.SectionBoundary);
}

internal sealed class FrameAccessInputs
{
    private readonly FrameResourceAccess _access;
    private readonly BufferRange[] _ranges;

    internal FrameAccessInputs(in FrameResourceAccess access)
    {
        _access = access;
        _ranges = new BufferRange[access.BufferRanges.Count];
        int index = 0;
        foreach (BufferRange range in access.BufferRanges) _ranges[index++] = range;
    }
    internal (GraphAccessTargetKind Kind, int Index) Resource => (_access.TargetKind, _access.ResourceIndex);

    internal bool Matches(in FrameResourceAccess access)
    {
        if (_access.PassIndex != access.PassIndex || _access.TargetKind != access.TargetKind ||
            _access.ResourceIndex != access.ResourceIndex || _access.Mode != access.Mode ||
            _access.Coverage != access.Coverage || _access.Sync != access.Sync || _access.Access != access.Access ||
            _access.TextureLayout != access.TextureLayout || _access.TextureRange != access.TextureRange ||
            _access.QueryRange != access.QueryRange || _access.ResultContents != access.ResultContents ||
            _access.BufferContentScope != access.BufferContentScope ||
            _access.UnorderedAccessHazards != access.UnorderedAccessHazards || _ranges.Length != access.BufferRanges.Count)
            return false;
        int index = 0;
        foreach (BufferRange range in access.BufferRanges)
            if (_ranges[index++] != range) return false;
        return true;
    }
}

internal sealed class FrameResourceLifetime
{
    internal int First = -1, Last = -1;
    internal long[] Begin = [], End = [];
    internal ulong Version;
}

/// <summary>One graph-wide set of current frame connections and their execution positions.</summary>
internal sealed partial class FrameConnections
{
    internal bool Reused { get; private set; }
    internal readonly HashSet<int> ChangedPasses = [];
    internal readonly HashSet<int> AnalyzedPasses = [];
    internal readonly PassOrder Order = new();
    internal readonly Dictionary<Queue, SortedSet<(long Position, int Pass)>> QueuePasses = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<Queue, int> QueueLanes = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<int, HashSet<int>> PredecessorAdditions = [];
    internal readonly Dictionary<int, HashSet<int>> ValueAdditions = [];
    internal readonly List<int> Roots = [];
    internal IReadOnlyList<int>[] Predecessors = [];
    internal IReadOnlyList<int>[] Successors = [];
    internal IReadOnlyList<int>[] ValuePredecessors = [];
    internal bool[] Live = [];
    internal int[] SameQueuePredecessor = [], SameQueueSuccessor = [];
    internal long[][] StartFrontiers = [], EndFrontiers = [];
    internal int QueueLaneCount;
    internal Queue?[] Queues = [];
    internal ulong ScheduleVersion;
    internal FramePassInputs[] PassInputs = [];
    internal readonly Dictionary<FrameAccessKey, FrameAccessInputs> AccessInputs = [];
    internal FramePassEdge[] ExplicitOrders = [];
    internal readonly Dictionary<(GraphAccessTargetKind Kind, int Index), FrameResourceLifetime> ResourceLifetimes = [];
}

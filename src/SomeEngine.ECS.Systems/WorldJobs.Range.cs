using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    private readonly record struct RangeAccess(World? World, WorldValueKind Kind, int Chunk, int Start, int End);
    [ThreadStatic] private static RangeAccess s_range;
    [ThreadStatic] private static int s_rangeDepth;

    internal override bool HasRangeWrite => ReferenceEquals(s_range.World, _world)
        && s_rangeDepth == JobThreads.Depth;

    internal RangeScope EnterRange(in QueryRange range) => new(_world, range);

    internal readonly struct RangeScope : IDisposable
    {
        private readonly RangeAccess _previous;
        private readonly int _depth;
        internal RangeScope(World world, in QueryRange range)
        {
            _previous = s_range;
            _depth = s_rangeDepth;
            s_range = new(world, WorldValueKind.Table, range.Chunk.Id, range.Start, range.End);
            s_rangeDepth = JobThreads.Depth;
        }
        public void Dispose() { s_range = _previous; s_rangeDepth = _depth; }
    }

    internal override void RequireWriteRow(Chunk chunk, int row)
    {
        if (!HasRangeWrite || HasStructureWrite()) return;
        if (chunk.Id != s_range.Chunk || row < s_range.Start || row >= s_range.End)
            throw new InvalidOperationException("The entity is outside the current write range.");
    }

    private void RequireRangeValue(in WorldValueAccess value)
    {
        JobResource values = Resource(WorldValueKind.Table, value.Type);
        long start = ((long)s_range.Chunk << 32) + s_range.Start;
        Require(JobResourceAccess.Write(values, start, s_range.End - s_range.Start), false);
        if (value.Tracked)
            Require(JobResourceAccess.Write(Resource(WorldValueKind.Changes, value.Type), s_range.Chunk, 1), false);
    }
}

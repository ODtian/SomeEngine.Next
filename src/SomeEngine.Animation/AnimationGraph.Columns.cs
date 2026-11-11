using SomeEngine.ECS;

namespace SomeEngine.Animation;

public static partial class AnimationGraph
{
    /// <summary>Binds the common leaf clock and completion columns for one query chunk.</summary>
    public static LeafTimeColumn ReadTimeColumn(QueryChunk chunk) => new(chunk);

    /// <summary>Leaf time and completion borrowed from a chunk declared with WithTimeAccess.</summary>
    public readonly ref struct LeafTimeColumn
    {
        private readonly QueryChunk _chunk;
        private readonly BufferRows<GraphMotion> _motions;
        private readonly bool _hasStages, _hasMotions;

        internal LeafTimeColumn(QueryChunk chunk)
        {
            _chunk = chunk;
            _hasStages = chunk.Has<Buffer<TimelineStage>>();
            _hasMotions = chunk.Has<GraphInstance>() && chunk.Has<Buffer<GraphMotion>>();
            _motions = _hasMotions ? chunk.GetBuffers<GraphMotion>() : default;
        }

        public AnimationLeafTime Read(int row, scoped in Timeline timeline, in AnimationLeafPlacement placement)
        {
            LeafPhase phase = CreatePhase(in timeline, in placement);
            float weight = 1;
            uint starts = timeline.Starts;
            if (_hasMotions && placement.Motion != 0)
            {
                GraphMotion motion = _motions[row][placement.Motion - 1];
                SelectMotion(ref phase, in motion, _chunk.Read<GraphInstance>()[row].Data!, in placement);
                weight = motion.Weight;
                starts = motion.Starts;
            }
            else phase.Select(_hasStages ? _chunk.ReadBuffer<TimelineStage>(row).AsSpan() : default);
            return new(phase.Sample, weight, starts, _chunk.Optional<Timeline>(row).IsEnabled && phase.Active && weight > 0);
        }

        public void Submit(int row, scoped ref Timeline timeline, in AnimationLeafPlacement placement, bool running)
        {
            if (placement.Motion == 0) timeline.Submit(running);
            else _motions[row][placement.Motion - 1].Running |= running;
        }
    }
}

using SomeEngine.ECS.Queries;

namespace SomeEngine.Animation;

public static partial class AnimationGraph
{
    /// <summary>Binds the common leaf clock and completion columns for one query chunk.</summary>
    public static LeafTimeColumn ReadTimeColumn(QueryChunkView chunk) => new(chunk);

    /// <summary>Leaf time and completion borrowed from a chunk declared with WithTimeAccess.</summary>
    public readonly ref struct LeafTimeColumn
    {
        private readonly QueryChunkView _chunk;
        private readonly ComponentEnabledColumn _enabled;
        private readonly BufferReadColumn<TimelineStage> _stages;
        private readonly BufferWriteColumn<GraphMotion> _motions;
        private readonly bool _hasStages, _hasMotions;

        internal LeafTimeColumn(QueryChunkView chunk)
        {
            _chunk = chunk;
            _enabled = chunk.ReadEnabledColumn<Timeline>();
            _hasStages = chunk.HasBuffer<TimelineStage>();
            _stages = _hasStages ? chunk.ReadBufferColumn<TimelineStage>() : default;
            _hasMotions = chunk.Has<GraphInstance>() && chunk.HasBuffer<GraphMotion>();
            _motions = _hasMotions ? chunk.WriteBufferColumn<GraphMotion>() : default;
        }

        public AnimationLeafTime Read(int row, scoped in Timeline timeline, in AnimationLeafPlacement placement)
        {
            LeafPhase phase = CreatePhase(in timeline, in placement);
            float weight = 1;
            uint starts = timeline.Starts;
            if (_hasMotions && placement.Motion != 0)
            {
                GraphMotion motion = _motions[row].Read(placement.Motion - 1);
                SelectMotion(ref phase, in motion, _chunk.Read<GraphInstance>(row).Data!, in placement);
                weight = motion.Weight;
                starts = motion.Starts;
            }
            else phase.Select(_hasStages ? _stages[row].AsSpan() : default);
            return new(phase.Sample, weight, starts, _enabled[row] && phase.Active && weight > 0);
        }

        public void Submit(int row, scoped ref Timeline timeline, in AnimationLeafPlacement placement, bool running)
        {
            if (placement.Motion == 0) timeline.Submit(running);
            else _motions[row][placement.Motion - 1].Running |= running;
        }
    }
}

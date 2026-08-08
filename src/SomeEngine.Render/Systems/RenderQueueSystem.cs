using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Pipeline-owned sink for one view's completed CPU render queue. Implementations receive exact
/// state bins and draw payloads while the frame-system context and queue spans are both valid.
/// </summary>
public interface IRenderQueueConsumer<TView, TBinKey, TDraw>
    where TBinKey : notnull
{
    void Consume(
        ref RenderFrameSystemContext context,
        in TView view,
        RenderQueueView<TBinKey, TDraw> queue);
}

/// <summary>
/// Ordinary CPU pipeline frame system. Persistent structural classification and exact-layout
/// instance composition run in prepare systems; this system performs per-view visibility,
/// state-key grouping, transparent ordering, and immediate queue consumption. It owns only its
/// two queries and queue scratch policy, never a global renderer or a second entity store.
/// </summary>
public sealed class RenderQueueSystem<
    TView,
    TMembership,
    TBinKey,
    TDraw,
    TClassifier,
    TConsumer> : ISystem<RenderFrameSystemContext>
    where TView : struct, SomeEngine.ECS.IComponent
    where TMembership : struct, IBufferElement
    where TBinKey : notnull
    where TClassifier : struct,
        IRenderQueueClassifier<TView, TMembership, TBinKey, TDraw>
    where TConsumer : struct, IRenderQueueConsumer<TView, TBinKey, TDraw>
{
    private readonly QueryDefinition _views;
    private readonly QueryDefinition _entities;
    private readonly TClassifier _classifier;
    private readonly RenderQueueBuilder<TMembership, TBinKey, TDraw> _builder;
    private TConsumer _consumer;
    private QueryHandle _viewQuery;
    private QueryHandle _entityQuery;
    private bool _created;

    public RenderQueueSystem(
        QueryDefinition views,
        QueryDefinition entities,
        in TClassifier classifier,
        in TConsumer consumer,
        int stateGroupingPartitions,
        int rowsPerPacket = 0,
        JobScheduleOptions jobOptions = default)
    {
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _classifier = classifier;
        _consumer = consumer;
        _builder = new RenderQueueBuilder<TMembership, TBinKey, TDraw>(
            stateGroupingPartitions,
            rowsPerPacket,
            jobOptions);
    }

    public void OnCreate(ref RenderFrameSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The render queue system is already created.");

        QueryHandle views = default;
        try
        {
            views = context.World.Query(_views);
            _entityQuery = context.World.Query(_entities);
            _viewQuery = views;
            _created = true;
        }
        catch
        {
            if (views.IsValid)
                context.World.ReleaseQuery(views);
            throw;
        }
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        if (!_created)
            throw new InvalidOperationException("The render queue system was not created.");

        var recording = new ViewRecording(this, context, _consumer);
        context.World.ExecuteQuery(
            _viewQuery,
            ref recording,
            static (QueryCursor cursor, ref ViewRecording state) => state.Record(cursor));
        context = recording.Context;
        _consumer = recording.Consumer;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;

        List<Exception>? failures = null;
        Release(context.World, ref _entityQuery, ref failures);
        Release(context.World, ref _viewQuery, ref failures);
        _created = false;
        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "The render queue system could not release every owned query.",
                    failures);
        }
    }

    private static void Release(
        RenderWorld world,
        ref QueryHandle query,
        ref List<Exception>? failures)
    {
        if (!query.IsValid)
            return;
        try
        {
            world.ReleaseQuery(query);
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }
        query = default;
    }

    private ref struct ViewRecording
    {
        private readonly RenderQueueSystem<
            TView,
            TMembership,
            TBinKey,
            TDraw,
            TClassifier,
            TConsumer> _owner;

        internal ViewRecording(
            RenderQueueSystem<
                TView,
                TMembership,
                TBinKey,
                TDraw,
                TClassifier,
                TConsumer> owner,
            RenderFrameSystemContext context,
            TConsumer consumer)
        {
            _owner = owner;
            Context = context;
            Consumer = consumer;
        }

        internal RenderFrameSystemContext Context;

        internal TConsumer Consumer;

        internal void Record(QueryCursor cursor)
        {
            foreach (QueryRow row in cursor.Rows)
            {
                TView view = row.Read<TView>();
                var state = new QueueExecutionState(Context, view, Consumer);
                _owner._builder.Build(
                    Context.World,
                    _owner._entityQuery,
                    in view,
                    in _owner._classifier,
                    ref state,
                    static (
                        ref QueueExecutionState execution,
                        RenderQueueView<TBinKey, TDraw> queue) =>
                        execution.Consume(queue));
                Context = state.Context;
                Consumer = state.Consumer;
            }
        }
    }

    private ref struct QueueExecutionState
    {
        private readonly TView _view;

        internal QueueExecutionState(
            RenderFrameSystemContext context,
            TView view,
            TConsumer consumer)
        {
            Context = context;
            _view = view;
            Consumer = consumer;
        }

        internal RenderFrameSystemContext Context;

        internal TConsumer Consumer;

        internal void Consume(RenderQueueView<TBinKey, TDraw> queue) =>
            Consumer.Consume(ref Context, in _view, queue);
    }
}

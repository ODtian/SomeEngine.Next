using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Pipeline-owned prepare system that turns one RenderWorld entity query into exact-layout
/// physical instance batches. The query remains the semantic entity set; batches and rows are
/// replaceable transport state and are never written back as entity identities.
/// </summary>
public sealed class RenderInstanceCompositionSystem<TGroupKey, TComposer> :
    ISystem<RenderPrepareSystemContext>,
    IRenderInstanceBatchSource<TGroupKey>
    where TGroupKey : notnull
    where TComposer : struct, IRenderInstanceBatchComposer<TGroupKey>
{
    private readonly TComposer _composer;
    private readonly RenderInstanceBatchBuilder<TGroupKey> _builder;
    private QueryHandle _query;
    private bool _created;

    public RenderInstanceCompositionSystem(
        QueryDefinition entities,
        in TComposer composer,
        int groupingPartitions,
        int rowsPerPacket = 0,
        SomeEngine.Job.JobScheduleOptions jobOptions = default)
    {
        EntityQuery = entities ?? throw new ArgumentNullException(nameof(entities));
        _composer = composer;
        _builder = new RenderInstanceBatchBuilder<TGroupKey>(
            groupingPartitions,
            rowsPerPacket,
            jobOptions);
    }

    /// <summary>
    /// Exact query definition whose current dense order corresponds to
    /// <see cref="RenderInstanceBatches{TGroupKey}.RowsForEntity(int)"/>. A CPU queue consumer
    /// should use this same definition rather than independently reconstructing a broader query.
    /// </summary>
    public QueryDefinition EntityQuery { get; }

    public RenderInstanceBatches<TGroupKey>? Current => _builder.Current;

    public bool Changed { get; private set; }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The render-instance composition system is already created.");
        _query = context.World.Query(EntityQuery);
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            throw new InvalidOperationException("The render-instance composition system was not created.");
        Changed = _builder.Update(context, _query, in _composer);
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;

        List<Exception>? failures = null;
        try
        {
            _builder.Clear(context);
        }
        catch (Exception failure)
        {
            failures = [failure];
        }

        try
        {
            context.World.ReleaseQuery(_query);
        }
        catch (Exception failure)
        {
            (failures ??= []).Add(failure);
        }

        _query = default;
        _created = false;
        Changed = false;
        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException(
                    "Render-instance composition could not release every owned resource.",
                    failures);
        }
    }
}

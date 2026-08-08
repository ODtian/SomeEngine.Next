using System.Globalization;
using System.Reflection;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Fuzz.Tests;

internal struct FuzzAlpha : SomeEngine.ECS.IComponent
{
    public int Value;
}

internal struct FuzzBeta : SomeEngine.ECS.IComponent
{
    public int Value;
}

internal readonly struct FuzzTag : SomeEngine.ECS.Components.ITag;

internal struct FuzzEnableable : SomeEngine.ECS.IEnableableComponent
{
    public int Value;
}

internal struct FuzzSparse : SomeEngine.ECS.Components.ISparseComponent
{
    public int Value;
}

internal readonly record struct FuzzShared(int Value) : SomeEngine.ECS.Components.ISharedComponent;

internal readonly record struct FuzzIndexed(int Value) : SomeEngine.ECS.Components.IIndexedComponent<int>
{
    public int GetKey() => Value;
}

[BufferCapacity(4)]
internal struct FuzzBufferElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

[RelationSchema(
    RelationDirection.Directed,
    RelationCardinality.UniquePair,
    AllowSelfEdge = false)]
internal readonly record struct FuzzRelation(int Value) : SomeEngine.ECS.IComponent;

internal readonly struct FuzzHierarchyDomain : IHierarchyDomain;

internal sealed record FuzzRunResult(
    int StepCount,
    int SuccessfulBatches,
    int RejectedBatches,
    int RejectedImmediateOperations,
    string StateDigest);

internal sealed class FuzzFailureException : Exception
{
    internal FuzzFailureException(
        int stepIndex,
        string stage,
        string message,
        Exception? inner = null,
        string? subject = null)
        : base($"Step {stepIndex} [{stage}]: {message}", inner)
    {
        StepIndex = stepIndex;
        Stage = stage;
        Subject = subject;
    }

    internal int StepIndex { get; }

    internal string Stage { get; }

    internal string? Subject { get; }

    internal string Fingerprint =>
        $"{GetType().FullName}:{Stage}:{Subject ?? "-"}:{InnermostExceptionType(InnerException)}";

    private static string InnermostExceptionType(Exception? error)
    {
        if (error is null)
            return "-";
        while (error.InnerException is not null)
            error = error.InnerException;
        return error.GetType().FullName ?? error.GetType().Name;
    }
}

internal sealed class EcsFuzzRunner
{
    internal const int LongCampaignFullVerificationInterval = 128;

    private static readonly PropertyInfo PublishedStructureEpochProperty =
        typeof(World).GetProperty(
            "PublishedStructureEpoch",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(typeof(World).FullName, "PublishedStructureEpoch");

    private readonly World _world = new();
    private readonly Dictionary<int, Entity> _actualByLogical = new();
    private readonly Dictionary<Entity, int> _logicalByActual = new();
    private readonly Dictionary<(int Source, int Target), RelationEdge<FuzzRelation>> _actualRelations = new();
    private readonly QueryHandle _alphaQuery;
    private readonly QueryHandle _betaQuery;
    private readonly QueryHandle _alphaBetaQuery;
    private readonly QueryHandle _tagQuery;
    private readonly QueryHandle _enableableQuery;
    private readonly QueryHandle _enabledQuery;
    private readonly QueryHandle _disabledQuery;
    private readonly QueryHandle _indexedQuery;
    private readonly ReferenceWorld _reference = new();
    private int _successfulBatches;
    private int _rejectedBatches;
    private int _rejectedImmediateOperations;

    internal EcsFuzzRunner()
    {
        _alphaQuery = _world.Query(_world.QueryDefinition().Read<FuzzAlpha>());
        _betaQuery = _world.Query(_world.QueryDefinition().Read<FuzzBeta>());
        _alphaBetaQuery = _world.Query(
            _world.QueryDefinition().Read<FuzzAlpha>().Read<FuzzBeta>());
        _tagQuery = _world.Query(_world.QueryDefinition().All<FuzzTag>());
        _enableableQuery = _world.Query(_world.QueryDefinition().Read<FuzzEnableable>());
        _enabledQuery = _world.Query(
            _world.QueryDefinition().Read<FuzzEnableable>().Enabled<FuzzEnableable>());
        _disabledQuery = _world.Query(
            _world.QueryDefinition().Read<FuzzEnableable>().Disabled<FuzzEnableable>());
        _indexedQuery = _world.Query(_world.QueryDefinition().Read<FuzzIndexed>());
    }

    internal FuzzRunResult Run(EcsFuzzTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.SchemaVersion != EcsFuzzTrace.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported ECS fuzz trace schema {trace.SchemaVersion}.");
        }
        if (!string.Equals(
                trace.PrngAlgorithm,
                EcsFuzzTrace.FixedPrngAlgorithm,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported ECS fuzz PRNG algorithm '{trace.PrngAlgorithm}'.");
        }
        if (trace.Steps is null)
            throw new InvalidDataException("The ECS fuzz trace has no step array.");

        VerifyWorld(stepIndex: -1);
        for (int index = 0; index < trace.Steps.Length; index++)
        {
            FuzzStep step = trace.Steps[index]
                ?? throw new InvalidDataException($"Trace step {index} is null.");
            if (step.Commands is null || step.Commands.Length == 0)
                throw new InvalidDataException($"Trace step {index} has no commands.");

            try
            {
                ExecuteStep(index, step);
                VerifyWorld(index);
            }
            catch (FuzzFailureException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new FuzzFailureException(
                    index,
                    "unexpected-exception",
                    $"{step.Mode} step raised {error.GetType().Name}: {error.Message}",
                    error,
                    step.Mode.ToString());
            }
        }

        return new FuzzRunResult(
            trace.Steps.Length,
            _successfulBatches,
            _rejectedBatches,
            _rejectedImmediateOperations,
            CreateStateDigest());
    }

    internal FuzzRunResult RunGenerated(
        ulong seed,
        int stepCount,
        int fullVerificationInterval = LongCampaignFullVerificationInterval)
    {
        if (stepCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepCount));
        if (fullVerificationInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(fullVerificationInterval));

        var generator = new EcsFuzzTraceGenerator.StreamingGenerator(seed);
        VerifyWorld(stepIndex: -1);
        int lastVerifiedStep = -1;
        for (int index = 0; index < stepCount; index++)
        {
            try
            {
                FuzzStep step = generator.Next(index, _reference);
                ExecuteStep(index, step);
                if ((index + 1) % fullVerificationInterval == 0)
                {
                    VerifyWorld(index);
                    lastVerifiedStep = index;
                }
            }
            catch (FuzzFailureException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new FuzzFailureException(
                    index,
                    "streaming-campaign",
                    $"Generated campaign step raised {error.GetType().Name}: {error.Message}",
                    error);
            }
        }
        if (lastVerifiedStep != stepCount - 1)
            VerifyWorld(stepCount - 1);

        return new FuzzRunResult(
            stepCount,
            _successfulBatches,
            _rejectedBatches,
            _rejectedImmediateOperations,
            CreateStateDigest());
    }

    private void ExecuteStep(int stepIndex, FuzzStep step)
    {
        using ReferenceWorld.Transaction transaction = _reference.BeginTransaction();
        ModelOperationException? modelError = null;
        try
        {
            foreach (FuzzCommand command in step.Commands)
                _reference.Apply(command);
        }
        catch (ModelOperationException error)
        {
            modelError = error;
        }

        switch (step.Mode)
        {
            case FuzzStepMode.Immediate:
                if (step.Commands.Length != 1)
                {
                    Fail(
                        stepIndex,
                        "trace-shape",
                        $"Immediate step contained {step.Commands.Length} commands.");
                }
                ExecuteImmediate(stepIndex, step.Commands[0], transaction, modelError);
                break;

            case FuzzStepMode.CommandBuffer:
                ExecuteBatch(stepIndex, step.Commands, transaction, modelError);
                break;

            default:
                Fail(stepIndex, "trace-shape", $"Unknown step mode {step.Mode}.");
                break;
        }
    }

    private void ExecuteImmediate(
        int stepIndex,
        FuzzCommand command,
        ReferenceWorld.Transaction transaction,
        ModelOperationException? modelError)
    {
        Exception? actualError = Capture(() => ApplyImmediate(command));
        if (modelError is null)
        {
            if (actualError is not null)
            {
                Fail(
                    stepIndex,
                    "immediate-acceptance",
                    $"Reference model accepted {command.Kind}, but ECS rejected it: {actualError.Message}",
                    actualError,
                    command.Kind.ToString());
            }
            transaction.Commit();
            return;
        }

        if (actualError is not InvalidOperationException)
        {
            Fail(
                stepIndex,
                "immediate-rejection",
                actualError is null
                    ? $"Reference model rejected {command.Kind}, but ECS accepted it."
                    : $"Reference model expected InvalidOperationException, ECS raised {actualError.GetType().Name}.",
                actualError,
                command.Kind.ToString());
        }
        _rejectedImmediateOperations++;
    }

    private void ExecuteBatch(
        int stepIndex,
        FuzzCommand[] commandImage,
        ReferenceWorld.Transaction transaction,
        ModelOperationException? modelError)
    {
        WorldStructuralMetrics metricsBefore = _world.GetStructuralMetrics();
        long epochBefore = ReadPublishedStructureEpoch();
        var deferred = new Dictionary<int, DeferredEntity>();

        using var commands = new CommandBuffer(_world);
        Exception? recordingError = Capture(() =>
        {
            foreach (FuzzCommand command in commandImage)
                Record(commands, deferred, command);
        });
        if (recordingError is not null)
        {
            Fail(
                stepIndex,
                "batch-recording",
                $"Command image could not be recorded: {recordingError.Message}",
                recordingError);
        }

        Exception? playbackError = Capture(commands.Playback);
        WorldStructuralMetrics metricsAfter = _world.GetStructuralMetrics();
        long epochAfter = ReadPublishedStructureEpoch();

        if (modelError is null)
        {
            if (playbackError is not null)
            {
                Fail(
                    stepIndex,
                    "batch-acceptance",
                    $"Reference model accepted the batch, but ECS rejected it: {playbackError.Message}",
                    playbackError);
            }

            foreach ((int logicalId, DeferredEntity pending) in deferred.OrderBy(static pair => pair.Key))
            {
                if (!pending.TryResolve(out Entity entity))
                {
                    Fail(
                        stepIndex,
                        "deferred-publication",
                        $"Successful batch did not resolve logical entity {logicalId}.");
                }
                RegisterActual(logicalId, entity);
            }

            RequireCounterDelta(
                stepIndex,
                "successful-batch-metrics",
                metricsBefore,
                metricsAfter,
                started: 1,
                published: 1,
                aborted: 0);
            if (epochAfter != epochBefore + 1)
            {
                Fail(
                    stepIndex,
                    "successful-batch-epoch",
                    $"Published structure epoch changed from {epochBefore} to {epochAfter}; expected +1.");
            }

            ReconcileActualRelations(_reference);
            transaction.Commit();
            _successfulBatches++;
            return;
        }

        if (playbackError is not InvalidOperationException)
        {
            Fail(
                stepIndex,
                "batch-rejection",
                playbackError is null
                    ? "Reference model rejected the batch, but ECS published it."
                    : $"Reference model expected InvalidOperationException, ECS raised {playbackError.GetType().Name}.",
                playbackError);
        }

        foreach ((int logicalId, DeferredEntity pending) in deferred)
        {
            if (pending.TryResolve(out Entity resolved))
            {
                Fail(
                    stepIndex,
                    "failed-deferred-publication",
                    $"Failed batch resolved logical entity {logicalId} to {resolved}.");
            }
        }

        RequireCounterDelta(
            stepIndex,
            "failed-batch-metrics",
            metricsBefore,
            metricsAfter,
            started: 1,
            published: 0,
            aborted: 1);
        if (epochAfter != epochBefore)
        {
            Fail(
                stepIndex,
                "failed-batch-epoch",
                $"Failed batch changed published structure epoch from {epochBefore} to {epochAfter}.");
        }
        _rejectedBatches++;
    }

    private void ApplyImmediate(FuzzCommand command)
    {
        switch (command.Kind)
        {
            case FuzzCommandKind.CreateEntity:
                RegisterActual(command.EntityId, _world.CreateEntity());
                break;
            case FuzzCommandKind.DestroyEntity:
                _world.DestroyEntity(GetActual(command.EntityId));
                RemoveActualRelationsAt(command.EntityId);
                break;
            case FuzzCommandKind.AddAlpha:
                _world.Add(GetActual(command.EntityId), new FuzzAlpha { Value = command.Value });
                break;
            case FuzzCommandKind.ReplaceAlpha:
                _world.Replace(GetActual(command.EntityId), new FuzzAlpha { Value = command.Value });
                break;
            case FuzzCommandKind.RemoveAlpha:
                _world.Remove<FuzzAlpha>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddBeta:
                _world.Add(GetActual(command.EntityId), new FuzzBeta { Value = command.Value });
                break;
            case FuzzCommandKind.ReplaceBeta:
                _world.Replace(GetActual(command.EntityId), new FuzzBeta { Value = command.Value });
                break;
            case FuzzCommandKind.RemoveBeta:
                _world.Remove<FuzzBeta>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddTag:
                _world.AddTag<FuzzTag>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.RemoveTag:
                _world.RemoveTag<FuzzTag>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddEnableable:
                _world.Add(GetActual(command.EntityId), new FuzzEnableable { Value = command.Value });
                break;
            case FuzzCommandKind.ReplaceEnableable:
                _world.Replace(GetActual(command.EntityId), new FuzzEnableable { Value = command.Value });
                break;
            case FuzzCommandKind.RemoveEnableable:
                _world.Remove<FuzzEnableable>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.Enable:
                _world.Enable<FuzzEnableable>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.Disable:
                _world.Disable<FuzzEnableable>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddSparse:
                _world.AddSparse(GetActual(command.EntityId), new FuzzSparse { Value = command.Value });
                break;
            case FuzzCommandKind.ReplaceSparse:
                _world.ReplaceSparse(GetActual(command.EntityId), new FuzzSparse { Value = command.Value });
                break;
            case FuzzCommandKind.RemoveSparse:
                _world.RemoveSparse<FuzzSparse>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddShared:
                _world.AddShared(GetActual(command.EntityId), new FuzzShared(command.Value));
                break;
            case FuzzCommandKind.ReplaceShared:
                _world.ReplaceShared(GetActual(command.EntityId), new FuzzShared(command.Value));
                break;
            case FuzzCommandKind.RemoveShared:
                _world.RemoveShared<FuzzShared>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddIndexed:
                _world.Add(GetActual(command.EntityId), new FuzzIndexed(command.Value));
                break;
            case FuzzCommandKind.ReplaceIndexed:
                _world.Replace(GetActual(command.EntityId), new FuzzIndexed(command.Value));
                break;
            case FuzzCommandKind.RemoveIndexed:
                _world.Remove<FuzzIndexed>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AddBuffer:
                _world.AddBuffer<FuzzBufferElement>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.AppendBuffer:
            {
                int value = command.Value;
                _world.ExecuteBufferWrite<FuzzBufferElement, int>(
                    GetActual(command.EntityId),
                    ref value,
                    static (DynamicBuffer<FuzzBufferElement> buffer, ref int item) =>
                        buffer.Add(new FuzzBufferElement { Value = item }));
                break;
            }
            case FuzzCommandKind.SetBufferFirst:
            {
                int value = command.Value;
                _world.ExecuteBufferWrite<FuzzBufferElement, int>(
                    GetActual(command.EntityId),
                    ref value,
                    static (DynamicBuffer<FuzzBufferElement> buffer, ref int item) =>
                        buffer[0] = new FuzzBufferElement { Value = item });
                break;
            }
            case FuzzCommandKind.RemoveBuffer:
                _world.RemoveBuffer<FuzzBufferElement>(GetActual(command.EntityId));
                break;
            case FuzzCommandKind.SetParent:
                Hierarchy<FuzzHierarchyDomain>.SetParent(
                    _world,
                    GetActual(command.EntityId),
                    GetActual(command.OtherEntityId));
                break;
            case FuzzCommandKind.Detach:
                Hierarchy<FuzzHierarchyDomain>.Detach(_world, GetActual(command.EntityId));
                break;
            case FuzzCommandKind.CreateRelation:
            {
                var key = (command.EntityId, command.OtherEntityId);
                RelationEdge<FuzzRelation> edge = _world.CreateRelation(
                    GetActual(command.EntityId),
                    GetActual(command.OtherEntityId),
                    new FuzzRelation(command.Value));
                _actualRelations.Add(key, edge);
                break;
            }
            case FuzzCommandKind.DestroyRelation:
            {
                var key = (command.EntityId, command.OtherEntityId);
                RelationEdge<FuzzRelation> edge = _actualRelations[key];
                _world.DestroyRelation(edge);
                _actualRelations.Remove(key);
                break;
            }
            default:
                throw new InvalidDataException($"Unknown fuzz command {command.Kind}.");
        }
    }

    private void Record(
        CommandBuffer commands,
        Dictionary<int, DeferredEntity> deferred,
        FuzzCommand command)
    {
        if (command.Kind == FuzzCommandKind.CreateEntity)
        {
            deferred.Add(command.EntityId, commands.CreateEntity());
            return;
        }

        bool isDeferred = deferred.TryGetValue(command.EntityId, out DeferredEntity pending);
        Entity entity = isDeferred ? Entity.Null : GetActual(command.EntityId);
        switch (command.Kind)
        {
            case FuzzCommandKind.DestroyEntity:
                if (isDeferred) commands.DestroyEntity(pending);
                else commands.DestroyEntity(entity);
                break;
            case FuzzCommandKind.AddAlpha:
                var alphaAdd = new FuzzAlpha { Value = command.Value };
                if (isDeferred) commands.Add(pending, alphaAdd);
                else commands.Add(entity, alphaAdd);
                break;
            case FuzzCommandKind.ReplaceAlpha:
                var alphaReplace = new FuzzAlpha { Value = command.Value };
                if (isDeferred) commands.Replace(pending, alphaReplace);
                else commands.Replace(entity, alphaReplace);
                break;
            case FuzzCommandKind.RemoveAlpha:
                if (isDeferred) commands.Remove<FuzzAlpha>(pending);
                else commands.Remove<FuzzAlpha>(entity);
                break;
            case FuzzCommandKind.AddBeta:
                var betaAdd = new FuzzBeta { Value = command.Value };
                if (isDeferred) commands.Add(pending, betaAdd);
                else commands.Add(entity, betaAdd);
                break;
            case FuzzCommandKind.ReplaceBeta:
                var betaReplace = new FuzzBeta { Value = command.Value };
                if (isDeferred) commands.Replace(pending, betaReplace);
                else commands.Replace(entity, betaReplace);
                break;
            case FuzzCommandKind.RemoveBeta:
                if (isDeferred) commands.Remove<FuzzBeta>(pending);
                else commands.Remove<FuzzBeta>(entity);
                break;
            case FuzzCommandKind.AddTag:
                if (isDeferred) commands.AddTag<FuzzTag>(pending);
                else commands.AddTag<FuzzTag>(entity);
                break;
            case FuzzCommandKind.RemoveTag:
                if (isDeferred) commands.RemoveTag<FuzzTag>(pending);
                else commands.RemoveTag<FuzzTag>(entity);
                break;
            case FuzzCommandKind.AddEnableable:
                var enableableAdd = new FuzzEnableable { Value = command.Value };
                if (isDeferred) commands.Add(pending, enableableAdd);
                else commands.Add(entity, enableableAdd);
                break;
            case FuzzCommandKind.ReplaceEnableable:
                var enableableReplace = new FuzzEnableable { Value = command.Value };
                if (isDeferred) commands.Replace(pending, enableableReplace);
                else commands.Replace(entity, enableableReplace);
                break;
            case FuzzCommandKind.RemoveEnableable:
                if (isDeferred) commands.Remove<FuzzEnableable>(pending);
                else commands.Remove<FuzzEnableable>(entity);
                break;
            case FuzzCommandKind.AddIndexed:
                var indexedAdd = new FuzzIndexed(command.Value);
                if (isDeferred) commands.Add(pending, indexedAdd);
                else commands.Add(entity, indexedAdd);
                break;
            case FuzzCommandKind.ReplaceIndexed:
                var indexedReplace = new FuzzIndexed(command.Value);
                if (isDeferred) commands.Replace(pending, indexedReplace);
                else commands.Replace(entity, indexedReplace);
                break;
            case FuzzCommandKind.RemoveIndexed:
                if (isDeferred) commands.Remove<FuzzIndexed>(pending);
                else commands.Remove<FuzzIndexed>(entity);
                break;
            default:
                throw new InvalidDataException(
                    $"Fuzz command {command.Kind} is not supported inside CommandBuffer steps.");
        }
    }

    private void VerifyWorld(int stepIndex)
    {
        int modelEntityCount = _reference.Entities.Count();
        if (_actualByLogical.Count != modelEntityCount)
        {
            Fail(
                stepIndex,
                "identity-map",
                $"Reference has {modelEntityCount} logical entities, actual map has {_actualByLogical.Count}.");
        }

        int expectedWorldEntityCount = checked(_reference.AliveCount + _reference.RelationCount);
        if (_world.EntityCount != expectedWorldEntityCount)
        {
            Fail(
                stepIndex,
                "entity-count",
                $"Reference has {_reference.AliveCount} live entities and " +
                $"{_reference.RelationCount} relation edges; ECS has {_world.EntityCount} entities.");
        }

        foreach ((int logicalId, ModelEntity expected) in _reference.Entities.OrderBy(static pair => pair.Key))
        {
            if (!_actualByLogical.TryGetValue(logicalId, out Entity entity))
                Fail(stepIndex, "identity-map", $"Logical entity {logicalId} has no ECS identity.");

            bool alive = _world.IsAlive(entity);
            if (alive != expected.Alive)
            {
                Fail(
                    stepIndex,
                    "liveness",
                    $"Logical entity {logicalId}/{entity} alive={alive}, expected {expected.Alive}.");
            }
            if (!alive)
                continue;

            VerifyComponent(
                stepIndex,
                logicalId,
                entity,
                "Alpha",
                expected.Alpha,
                _world.Has<FuzzAlpha>(entity),
                static (world, target) => world.Read<FuzzAlpha>(target).Value);
            VerifyComponent(
                stepIndex,
                logicalId,
                entity,
                "Beta",
                expected.Beta,
                _world.Has<FuzzBeta>(entity),
                static (world, target) => world.Read<FuzzBeta>(target).Value);

            bool hasTag = _world.Has<FuzzTag>(entity);
            if (hasTag != expected.HasTag)
            {
                Fail(
                    stepIndex,
                    "tag-state",
                    $"Logical entity {logicalId}/{entity} tag={hasTag}, expected {expected.HasTag}.");
            }

            VerifyComponent(
                stepIndex,
                logicalId,
                entity,
                "Enableable",
                expected.Enableable,
                _world.Has<FuzzEnableable>(entity),
                static (world, target) => world.Read<FuzzEnableable>(target).Value);
            if (expected.Enableable is not null &&
                _world.IsEnabled<FuzzEnableable>(entity) != expected.EnableableEnabled)
            {
                Fail(
                    stepIndex,
                    "enableable-state",
                    $"Logical entity {logicalId}/{entity} has an incorrect enabled bit.",
                    subject: "Enableable");
            }

            VerifyOwnerState(stepIndex, logicalId, entity, expected);
        }

        VerifyQuery(
            stepIndex,
            "Alpha",
            _alphaQuery,
            _reference.EligibleIds(static entity => entity.Alive && entity.Alpha is not null));
        VerifyQuery(
            stepIndex,
            "Beta",
            _betaQuery,
            _reference.EligibleIds(static entity => entity.Alive && entity.Beta is not null));
        VerifyQuery(
            stepIndex,
            "Alpha+Beta",
            _alphaBetaQuery,
            _reference.EligibleIds(static entity =>
                entity.Alive && entity.Alpha is not null && entity.Beta is not null));
        VerifyQuery(
            stepIndex,
            "Tag",
            _tagQuery,
            _reference.EligibleIds(static entity => entity.Alive && entity.HasTag));
        VerifyQuery(
            stepIndex,
            "Enableable",
            _enableableQuery,
            _reference.EligibleIds(static entity => entity.Alive && entity.Enableable is not null));
        VerifyQuery(
            stepIndex,
            "Enabled",
            _enabledQuery,
            _reference.EligibleIds(static entity =>
                entity.Alive && entity.Enableable is not null && entity.EnableableEnabled));
        VerifyQuery(
            stepIndex,
            "Disabled",
            _disabledQuery,
            _reference.EligibleIds(static entity =>
                entity.Alive && entity.Enableable is not null && !entity.EnableableEnabled));
        VerifyQuery(
            stepIndex,
            "Indexed",
            _indexedQuery,
            _reference.EligibleIds(static entity => entity.Alive && entity.Indexed is not null));
        VerifyIndices(stepIndex);
        VerifyHierarchy(stepIndex);
        VerifyRelations(stepIndex);
    }

    private void VerifyOwnerState(
        int stepIndex,
        int logicalId,
        Entity entity,
        ModelEntity expected)
    {
        bool hasSparse = _world.HasSparse<FuzzSparse>(entity);
        if (hasSparse != (expected.Sparse is not null) ||
            hasSparse && _world.ReadSparse<FuzzSparse>(entity).Value != expected.Sparse)
        {
            Fail(stepIndex, "sparse-state", $"Logical entity {logicalId}/{entity} sparse state differed.");
        }

        bool hasShared = _world.HasShared<FuzzShared>(entity);
        if (hasShared != (expected.Shared is not null) ||
            hasShared && _world.GetShared<FuzzShared>(entity).Value != expected.Shared)
        {
            Fail(stepIndex, "shared-state", $"Logical entity {logicalId}/{entity} shared state differed.");
        }

        VerifyComponent(
            stepIndex,
            logicalId,
            entity,
            "Indexed",
            expected.Indexed,
            _world.Has<FuzzIndexed>(entity),
            static (world, target) => world.Read<FuzzIndexed>(target).Value);

        bool hasBuffer = _world.HasBuffer<FuzzBufferElement>(entity);
        if (hasBuffer != (expected.Buffer is not null))
        {
            Fail(stepIndex, "buffer-presence", $"Logical entity {logicalId}/{entity} buffer presence differed.");
        }
        if (hasBuffer)
        {
            int[] actual = Array.Empty<int>();
            _world.ExecuteBufferRead<FuzzBufferElement, int[]>(
                entity,
                ref actual,
                static (BufferView<FuzzBufferElement> buffer, ref int[] values) =>
                {
                    values = new int[buffer.Count];
                    for (int i = 0; i < buffer.Count; i++)
                        values[i] = buffer[i].Value;
                });
            if (!actual.SequenceEqual(expected.Buffer!))
            {
                Fail(stepIndex, "buffer-value", $"Logical entity {logicalId}/{entity} buffer values differed.");
            }
        }

        Entity expectedParent = expected.Parent is int parentId
            ? GetActual(parentId)
            : Entity.Null;
        Entity actualParent = Hierarchy<FuzzHierarchyDomain>.GetParent(_world, entity);
        if (actualParent != expectedParent)
        {
            Fail(stepIndex, "hierarchy-parent", $"Logical entity {logicalId}/{entity} parent differed.");
        }
    }

    private void VerifyIndices(int stepIndex)
    {
        int[] keys = _reference.Entities
            .Where(static pair => pair.Value.Indexed is not null)
            .Select(static pair => pair.Value.Indexed!.Value)
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
        foreach (int key in keys)
        {
            int[] expected = _reference.Entities
                .Where(pair => pair.Value.Alive && pair.Value.Indexed == key)
                .Select(static pair => pair.Key)
                .OrderBy(static value => value)
                .ToArray();
            int[] actual = _world.GetByIndex<FuzzIndexed, int>(key)
                .ToArray()
                .Select(entity => _logicalByActual.TryGetValue(entity, out int logicalId) ? logicalId : -1)
                .OrderBy(static value => value)
                .ToArray();
            if (!actual.SequenceEqual(expected))
                Fail(stepIndex, "index-bucket", $"Index key {key} returned an incorrect entity set.", subject: key.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void VerifyHierarchy(int stepIndex)
    {
        foreach ((int parentId, ModelEntity parent) in _reference.Entities.Where(static pair => pair.Value.Alive))
        {
            int[] expected = _reference.Entities
                .Where(pair => pair.Value.Alive && pair.Value.Parent == parentId)
                .Select(static pair => pair.Key)
                .OrderBy(static value => value)
                .ToArray();
            int[] actual = Hierarchy<FuzzHierarchyDomain>
                .GetChildren(_world, GetActual(parentId))
                .Span
                .ToArray()
                .Select(entity => _logicalByActual.TryGetValue(entity, out int logicalId) ? logicalId : -1)
                .OrderBy(static value => value)
                .ToArray();
            if (!actual.SequenceEqual(expected))
                Fail(stepIndex, "hierarchy-children", $"Logical parent {parentId} had an incorrect child set.");
        }
    }

    private void VerifyRelations(int stepIndex)
    {
        if (_actualRelations.Count != _reference.RelationCount)
            Fail(stepIndex, "relation-count", "Actual relation identity map did not match the oracle.");

        foreach ((int source, int target) in _reference.Relations)
        {
            if (!_actualRelations.TryGetValue((source, target), out RelationEdge<FuzzRelation> edge) ||
                !_world.IsAlive(edge.Entity))
            {
                Fail(stepIndex, "relation-identity", $"Relation {source}>{target} had no live edge.");
            }
            DirectedRelationEndpoints<FuzzRelation> endpoints =
                _world.GetDirectedRelationEndpoints(edge);
            if (endpoints.Source != GetActual(source) || endpoints.Target != GetActual(target) ||
                _world.Read<FuzzRelation>(edge.Entity).Value != _reference.RelationValue(source, target))
            {
                Fail(stepIndex, "relation-state", $"Relation {source}>{target} differed from the oracle.");
            }
        }

        foreach ((int logicalId, ModelEntity entity) in _reference.Entities.Where(static pair => pair.Value.Alive))
        {
            int expectedOutgoing = _reference.Relations.Count(pair => pair.Source == logicalId);
            int expectedIncoming = _reference.Relations.Count(pair => pair.Target == logicalId);
            if (_world.GetOutgoingRelations<FuzzRelation>(GetActual(logicalId)).Count != expectedOutgoing ||
                _world.GetIncomingRelations<FuzzRelation>(GetActual(logicalId)).Count != expectedIncoming)
            {
                Fail(stepIndex, "relation-adjacency", $"Logical entity {logicalId} adjacency count differed.");
            }
        }
    }

    private void VerifyComponent(
        int stepIndex,
        int logicalId,
        Entity entity,
        string name,
        int? expected,
        bool hasActual,
        Func<World, Entity, int> read)
    {
        if (hasActual != (expected is not null))
        {
            Fail(
                stepIndex,
                "component-presence",
                $"Logical entity {logicalId}/{entity} {name} presence={hasActual}, expected {expected is not null}.",
                subject: name);
        }
        if (expected is null)
            return;

        int actual = read(_world, entity);
        if (actual != expected.Value)
        {
            Fail(
                stepIndex,
                "component-value",
                $"Logical entity {logicalId}/{entity} {name}={actual}, expected {expected.Value}.",
                subject: name);
        }
    }

    private void VerifyQuery(int stepIndex, string name, QueryHandle query, int[] expectedIds)
    {
        var actualIds = new HashSet<int>();
        int rowCount = 0;
        _world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
            {
                rowCount++;
                if (!_logicalByActual.TryGetValue(row.Entity, out int logicalId))
                {
                    Fail(
                        stepIndex,
                        "query-identity",
                        $"{name} query returned unknown ECS entity {row.Entity}.",
                        subject: name);
                }
                if (!actualIds.Add(logicalId))
                {
                    Fail(
                        stepIndex,
                        "query-duplicate",
                        $"{name} query returned logical entity {logicalId} more than once.",
                        subject: name);
                }
            }
        });

        var expected = new HashSet<int>(expectedIds);
        if (rowCount != expected.Count || !actualIds.SetEquals(expected))
        {
            string actualText = string.Join(",", actualIds.OrderBy(static value => value));
            string expectedText = string.Join(",", expected.OrderBy(static value => value));
            Fail(
                stepIndex,
                "query-set",
                $"{name} query returned [{actualText}], expected [{expectedText}].",
                subject: name);
        }
    }

    private void RemoveActualRelationsAt(int logicalId)
    {
        foreach ((int source, int target) in _actualRelations.Keys
                     .Where(pair => pair.Source == logicalId || pair.Target == logicalId)
                     .ToArray())
        {
            _actualRelations.Remove((source, target));
        }
    }

    private void ReconcileActualRelations(ReferenceWorld candidate)
    {
        var expected = candidate.Relations.ToHashSet();
        foreach ((int source, int target) in _actualRelations.Keys.ToArray())
        {
            if (!expected.Contains((source, target)))
                _actualRelations.Remove((source, target));
        }
    }

    private void RegisterActual(int logicalId, Entity entity)
    {
        if (!_actualByLogical.TryAdd(logicalId, entity))
            throw new InvalidDataException($"Logical entity {logicalId} already has an ECS identity.");
        if (!_logicalByActual.TryAdd(entity, logicalId))
            throw new InvalidDataException($"ECS identity {entity} already belongs to another logical entity.");
    }

    private Entity GetActual(int logicalId)
    {
        if (!_actualByLogical.TryGetValue(logicalId, out Entity entity))
            throw new InvalidDataException($"Logical entity {logicalId} has no recorded ECS identity.");
        return entity;
    }

    private long ReadPublishedStructureEpoch()
    {
        object? value = PublishedStructureEpochProperty.GetValue(_world);
        return value is long epoch
            ? epoch
            : throw new InvalidDataException("World.PublishedStructureEpoch did not return Int64.");
    }

    private string CreateStateDigest()
    {
        string identities = string.Join(
            ",",
            _actualByLogical
                .OrderBy(static pair => pair.Key)
                .Select(static pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pair.Key}={pair.Value.Index}:{pair.Value.Generation}")));
        string relationIdentities = string.Join(
            ",",
            _actualRelations
                .OrderBy(static pair => pair.Key.Source)
                .ThenBy(static pair => pair.Key.Target)
                .Select(static pair => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{pair.Key.Source}>{pair.Key.Target}=" +
                    $"{pair.Value.Entity.Index}:{pair.Value.Entity.Generation}")));
        return $"{_reference.Digest()}|ids={identities}|relations={relationIdentities}" +
               $"|epoch={ReadPublishedStructureEpoch()}";
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void RequireCounterDelta(
        int stepIndex,
        string stage,
        WorldStructuralMetrics before,
        WorldStructuralMetrics after,
        long started,
        long published,
        long aborted)
    {
        long actualStarted = after.Started - before.Started;
        long actualPublished = after.Published - before.Published;
        long actualAborted = after.Aborted - before.Aborted;
        if (actualStarted != started || actualPublished != published || actualAborted != aborted)
        {
            Fail(
                stepIndex,
                stage,
                $"Structural metrics delta was started/published/aborted " +
                $"{actualStarted}/{actualPublished}/{actualAborted}, expected " +
                $"{started}/{published}/{aborted}.");
        }
    }

    private static void Fail(
        int stepIndex,
        string stage,
        string message,
        Exception? inner = null,
        string? subject = null) =>
        throw new FuzzFailureException(stepIndex, stage, message, inner, subject);
}

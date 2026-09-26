using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Owns an explicitly installed group of extraction systems for one RenderWorld. All systems
/// collect from one coherent main-world read and publish through one RenderWorld structural
/// transaction, so adding a feature never turns extraction into one global switch statement.
/// </summary>
public sealed class RenderExtractionSystems : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ExtractionTrace = new("ecs", "RenderExtraction");
    private readonly RenderWorld _renderWorld;
    private readonly List<IRenderExtractionSystem> _systems = [];
    private int[] _writtenComponents = [];
    internal QueryDefinition DestinationAccess { get; private set; } = null!;
    private readonly RenderExtractionContext _context;
    private readonly object _gate = new();
    private QueryDefinition? _sourceQuery;
    private QueryHandle _sourceQueryHandle;
    private World? _mainWorld;
    private World? _candidateMainWorld;
    private uint _lastSystemVersion;
    private uint _candidateSystemVersion;
    private EntityTableChanges? _sourceChanges;
    private EntityTableChangeBatch? _candidateSourceChanges;
    private bool _candidateFullReconciliation;
    private readonly HashSet<IRenderExtractionSystem> _changedSystems = [];
    private bool _candidatePending;
    private bool _extractionActive;
    private bool _disposed;

    public RenderExtractionSystems(RenderWorld renderWorld, params ReadOnlySpan<IRenderExtractionSystem> systems)
    {
        _renderWorld = renderWorld ?? throw new ArgumentNullException(nameof(renderWorld));
        _context = new RenderExtractionContext(renderWorld);
        try
        {
            foreach (IRenderExtractionSystem system in systems) Add(system);
        }
        catch { _context.Dispose(); throw; }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _systems.Count;
            }
        }
    }

    internal RenderWorld World => _renderWorld;

    internal bool HasPendingCandidate
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _candidatePending;
            }
        }
    }

    /// <summary>
    /// Resolves the persistent RenderWorld mirror for one authoritative main-world entity after
    /// extraction. This is intended for render-facing resources that keep a small prototype or
    /// template entity while storing the actual instance population outside ECS.
    /// </summary>
    public Entity RequireMirror(Entity source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive)
            {
                throw new InvalidOperationException(
                    "Render mirrors cannot be resolved while extraction is active.");
            }
            return _context.RequireMirror(source);
        }
    }

    /// <summary>Adds a feature-owned extractor before the first source snapshot is compiled.</summary>
    public void Add(IRenderExtractionSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_sourceQuery is not null || _extractionActive)
            {
                throw new InvalidOperationException(
                    "Extraction systems must be installed before the first extraction.");
            }
            _systems.Add(system);
        }
    }

    /// <summary>
    /// Collects one detached candidate from <paramref name="mainWorld"/>. The main world is read
    /// only and the live RenderWorld is not mutated. The candidate is published by the next
    /// RenderPrepareSystems update; a second candidate cannot overwrite pending work.
    /// </summary>
    public void Extract(World mainWorld)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ExtractionTrace);
        ArgumentNullException.ThrowIfNull(mainWorld);
        if (mainWorld is RenderWorld)
        {
            throw new ArgumentException(
                "A RenderWorld cannot be an authoritative extraction source; extraction " +
                "requires a main World.",
                nameof(mainWorld));
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive)
                throw new InvalidOperationException("Render extraction is already active.");
            if (_candidatePending)
            {
                throw new InvalidOperationException(
                    "The previous render extraction candidate has not reached a render update boundary.");
            }
            RequireAuthoritativeMainWorld(mainWorld);
            _sourceQuery ??= BuildSourceQuery();
            if (_systems.Count == 0)
            {
                _mainWorld ??= mainWorld;
                return;
            }
            _extractionActive = true;
            try
            {
                bool bindingCandidate = _mainWorld is null;
                if (!_sourceQueryHandle.IsValid)
                    _sourceQueryHandle = mainWorld.Query(_sourceQuery);
                try
                {
                    _sourceChanges ??= mainWorld.ObserveEntityTables();
                    ExtractCore(mainWorld);
                    _candidateMainWorld = mainWorld;
                    _candidatePending = true;
                }
                catch
                {
                    if (bindingCandidate)
                    {
                        mainWorld.ReleaseQuery(_sourceQueryHandle);
                        _sourceQueryHandle = default;
                        _sourceChanges?.Dispose();
                        _sourceChanges = null;
                    }
                    throw;
                }
            }
            finally
            {
                _extractionActive = false;
            }
        }
    }

    private QueryDefinition BuildSourceQuery()
    {
        var query = new RenderExtractionQuery();
        query.Write<RenderTransform>();
        query.Write<RenderPreviousTransform>();
        for (int index = 0; index < _systems.Count; index++)
            _systems[index].DeclareReads(query);
        _writtenComponents = query.WrittenComponents.ToArray();
        DestinationAccess = query.BuildDestinationAccess();
        return query.Build();
    }

    private void ExtractCore(World mainWorld)
    {
        _candidateSystemVersion = mainWorld.AcquireSystemTick();
        _context.BeginTransformChanges();
        foreach (IRenderExtractionSystem system in _systems) system.Reset();
        _changedSystems.Clear();
        try
        {
            RenderExtractionSystems state = this;
            mainWorld.ExecuteReadSnapshot(_sourceQueryHandle, _lastSystemVersion, ref state,
                static (QueryCursor query, ref RenderExtractionSystems systems) =>
                {
                    systems._candidateSourceChanges = systems._sourceChanges!.Read();
                    systems._candidateFullReconciliation = systems._mainWorld is null ||
                        systems._candidateSourceChanges.Count != 0;
                    systems._context.BeginSourceChanges(systems._candidateSourceChanges.Entities);
                    if (systems._candidateFullReconciliation) systems.Collect(query);
                    else systems.CollectDelta(query);
                });
            foreach (IRenderExtractionSystem system in _systems)
                if (system.CompleteCollection(_context, _candidateFullReconciliation))
                    _changedSystems.Add(system);
        }
        catch
        {
            _context.CancelTransformChanges();
            throw;
        }
    }

    /// <summary>
    /// Publishes the pending candidate while RenderPrepareSystems owns the render update boundary.
    /// No product caller outside that boundary receives this mutation path.
    /// </summary>
    internal bool ApplyPendingCandidate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive)
                throw new InvalidOperationException("Render extraction is still collecting a candidate.");
            if (!_candidatePending)
                return false;

            World candidateMainWorld = _candidateMainWorld
                ?? throw new InvalidOperationException("The pending extraction candidate lost its source World.");
            try
            {
                ApplyCandidateCore();
                _context.AcceptTransformChanges();
                _lastSystemVersion = _candidateSystemVersion;
                _sourceChanges!.Accept(_candidateSourceChanges!);
                _mainWorld ??= candidateMainWorld;
                ClearCandidate();
                return true;
            }
            catch
            {
                _context.CancelTransformChanges();
                if (_mainWorld is null && _sourceQueryHandle.IsValid)
                {
                    candidateMainWorld.ReleaseQuery(_sourceQueryHandle);
                    _sourceQueryHandle = default;
                    _sourceChanges?.Dispose();
                    _sourceChanges = null;
                }
                ClearCandidate();
                throw;
            }
        }
    }

    private void ApplyCandidateCore()
    {
        if (_candidateFullReconciliation)
        {
            if (_mainWorld is null) _context.RefreshMirrors();
            _renderWorld.ApplyExtraction(this);
            return;
        }

        if (_changedSystems.Count == 0 && !_context.HasTransformChanges) return;
        if (!_renderWorld.TryApplyExtractionChangesDirect(this))
            _renderWorld.ApplyExtractionChanges(this);
    }

    private void ClearCandidate()
    {
        _candidateMainWorld = null;
        _candidateSystemVersion = 0;
        _candidateSourceChanges = null;
        _candidateFullReconciliation = false;
        _changedSystems.Clear();
        _candidatePending = false;
    }

    private void RequireAuthoritativeMainWorld(World mainWorld)
    {
        if (_mainWorld is null || ReferenceEquals(_mainWorld, mainWorld))
            return;

        throw new InvalidOperationException(
            "A RenderWorld is bound to one authoritative main World for its lifetime.");
    }

    private void Collect(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.ChangedChunks(_candidateSourceChanges!))
        {
            _context.IncludeSources(chunk.Entities);
            for (int index = 0; index < _systems.Count; index++)
                _systems[index].Collect(chunk);
        }
    }

    private void CollectDelta(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            foreach (IRenderExtractionSystem system in _systems)
                if (system.CollectChanges(chunk, _context)) _changedSystems.Add(system);
        }
    }

    internal void ApplyCandidate()
    {
        _context.BeginCandidate();
        // The clock belongs to the detached root and rolls back with every module mutation.
        _renderWorld.AcquireSystemVersion();
        for (int index = 0; index < _systems.Count; index++)
            _systems[index].Apply(_context);
        _context.ApplyRetainedTransforms();
        _context.DestroyUnusedMirrors();
    }

    internal void ApplyChangesCandidate()
    {
        // The detached value candidate gives transform, mesh/material, and light changes one
        // publication boundary without rediscovering or reconciling persistent mirror structure.
        _renderWorld.AcquireSystemVersion();
        if (_renderWorld.HasExtractionValueHooks([
                SomeEngine.ECS.Registry.ComponentMetadata<RenderTransform>.Id,
                SomeEngine.ECS.Registry.ComponentMetadata<RenderPreviousTransform>.Id]))
            _context.ApplyTransformChanges();
        else
            _context.ApplyTransformValueChanges();
        foreach (IRenderExtractionSystem system in _changedSystems) system.ApplyChanges(_context);
    }

    internal bool TryApplyChangesDirect()
    {
        if (_changedSystems.Any(static system => !system.CanApplyChangesDirect) ||
            _renderWorld.HasExtractionValueHooks(_writtenComponents))
            return false;

        // All invariants are checked before the first in-place write. With the topology frontier
        // held and no user callbacks in the changed domains, the remaining operations contain no
        // ordinary failure point and do not need a detached structural root.
        foreach (IRenderExtractionSystem system in _changedSystems) system.ValidateChanges(_context);
        _renderWorld.AcquireSystemVersion();
        _context.ApplyTransformValueChanges();
        foreach (IRenderExtractionSystem system in _changedSystems) system.ApplyChanges(_context);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_extractionActive)
                throw new InvalidOperationException("Render extraction is still active.");
            _context.CancelTransformChanges();
            foreach (IRenderExtractionSystem system in _systems) system.Dispose();
            _context.Dispose();
            _sourceChanges?.Dispose();
            _sourceChanges = null;
            if (_sourceQueryHandle.IsValid)
            {
                (_mainWorld ?? _candidateMainWorld ?? throw new InvalidOperationException(
                    "A bound extraction query has no authoritative main World."))
                    .ReleaseQuery(_sourceQueryHandle);
                _sourceQueryHandle = default;
            }
            ClearCandidate();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Restricts extraction modules to optional source reads.</summary>
public sealed class RenderExtractionQuery
{
    private readonly QueryDefinitionBuilder _builder = new();
    internal HashSet<int> WrittenComponents { get; } = [];
    private readonly QueryDefinitionBuilder _destination = new QueryDefinitionBuilder().Optional<RenderSource>(QueryAccess.Read);

    /// <summary>Declares a destination component changed by this extractor.</summary>
    public void Write<T>() where T : struct, IComponent
    {
        if (WrittenComponents.Add(SomeEngine.ECS.Registry.ComponentMetadata<T>.Id))
            _destination.Optional<T>(QueryAccess.Write);
    }

    internal QueryDefinition BuildDestinationAccess() => _destination.Build();

    internal void ReadDestination<T>() where T : struct, IComponent =>
        _destination.Optional<T>(QueryAccess.Read);

    public void ReadOptional<T>() where T : struct
        => _builder.Optional<T>(QueryAccess.Read);

    public void ReadOptionalBuffer<T>() where T : struct, IBufferElement
        => _builder.OptionalBuffer<T>(QueryAccess.Read);

    internal QueryDefinition Build() => _builder.Build();
}

public interface IRenderExtractionSystem : IDisposable
{
    void DeclareReads(RenderExtractionQuery query);

    void Reset();

    /// <summary>Collects current data from an affected source chunk; unrelated chunks are not replayed.</summary>
    void Collect(QueryChunkView chunk);

    /// <summary>Applies collected data and reconciles only the changed mirrors exposed by the context.</summary>
    void Apply(RenderExtractionContext context);

    /// <summary>Collects only values changed since the preceding extraction.</summary>
    bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context);

    /// <summary>Collects changes to source assets referenced by the collected Game World entities.</summary>
    bool CompleteCollection(RenderExtractionContext context, bool fullCollection) => false;

    /// <summary>Applies collected values to existing mirrors without changing their membership.</summary>
    void ApplyChanges(RenderExtractionContext context);

    /// <summary>
    /// Enables direct value application when all destination components are declared and
    /// validation completes before writes. Other extractors use the ECS transaction boundary.
    /// </summary>
    bool CanApplyChangesDirect => false;

    void ValidateChanges(RenderExtractionContext context) { }

    void IDisposable.Dispose() { }
}

/// <summary>An existing render mirror affected by the current source changes.</summary>
public readonly record struct RenderMirror(Entity RenderEntity, Entity Source);

/// <summary>Shared entity-identity and mutation services for one candidate publication.</summary>
public sealed class RenderExtractionContext : IDisposable
{
    private readonly RenderWorld _world;
    private readonly QueryHandle _renderSources;
    private readonly QueryHandle _transformWrites;
    private readonly Dictionary<Entity, Entity> _mirrorBySource = [];
    private readonly Dictionary<Entity, Entity> _previousMirrors = [];
    private readonly HashSet<Entity> _sourceChanges = [];
    internal IEnumerable<Entity> ChangedSources => _sourceChanges;
    private readonly HashSet<Entity> _retainedSources = [];
    private readonly List<RenderMirror> _mirrors = [];
    private readonly Dictionary<Entity, RenderTransform> _transformChanges = [];
    private readonly Dictionary<Entity, RenderTransform> _retainedTransforms = [];
    // Transform dictionaries and query rows already contribute each destination once.
    private List<Entity> _movingTransforms = [];
    private List<Entity> _nextMovingTransforms = [];
    private readonly HashSet<Entity> _stoppedTransforms = [];
    private readonly List<TransformWrite> _transformUpdates = [];
    private readonly List<TransformChunkChanges> _transformChunks = [];

    private readonly record struct TransformWrite(
        Entity Entity,
        RenderTransform Current,
        bool WriteCurrent,
        bool WritePrevious,
        int RowIndex = -1);

    private readonly record struct TransformChunkChanges(int First, int Count, int CurrentCount, int PreviousCount);

    internal RenderExtractionContext(RenderWorld world)
    {
        _world = world;
        _renderSources = world.Query(new QueryDefinitionBuilder().Read<RenderSource>());
        try
        {
            _transformWrites = world.Query(new QueryDefinitionBuilder()
                .ReadWrite<RenderTransform>().ReadWrite<RenderPreviousTransform>());
        }
        catch
        {
            world.ReleaseQuery(_renderSources);
            throw;
        }
    }

    public RenderWorld World => _world;

    public ReadOnlySpan<RenderMirror> Mirrors => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_mirrors);

    public Entity RequireMirror(Entity source)
    {
        if (!_mirrorBySource.TryGetValue(source, out Entity entity))
            throw new InvalidOperationException($"RenderWorld has no persistent mirror for {source}.");
        return entity;
    }

    internal void BeginSourceChanges(IEnumerable<Entity> sources)
    {
        _sourceChanges.Clear();
        _sourceChanges.UnionWith(sources);
    }

    internal void IncludeSources(ReadOnlySpan<Entity> sources)
    {
        foreach (Entity source in sources) _sourceChanges.Add(source);
    }

    public void UpdateExisting<T>(Entity entity, in T value)
        where T : struct, SomeEngine.ECS.IComponent, IEquatable<T>
    {
        RequireExisting<T>(entity);

        T current = _world.Read<T>(entity);
        if (!current.Equals(value))
            _world.Replace(entity, value);
    }

    internal void RequireExisting<T>(Entity entity)
        where T : struct, SomeEngine.ECS.IComponent
    {
        if (!_world.Has<T>(entity))
        {
            throw new InvalidOperationException(
                $"Render mirror {entity} is missing required extracted component " +
                $"{typeof(T).Name} on a value-only extraction path.");
        }
    }

    internal void BeginCandidate()
    {
        _previousMirrors.Clear();
        _mirrors.Clear();
        foreach (Entity source in _sourceChanges)
            if (_mirrorBySource.TryGetValue(source, out Entity entity))
                _mirrors.Add(new RenderMirror(entity, source));
        _retainedSources.Clear();
        _retainedTransforms.Clear();
        _nextMovingTransforms.Clear();
    }

    /// <summary>
    /// Retains a mirror's transform during full collection. All extractors finish retaining
    /// their data before transforms are applied or removed, so shared mirrors do not depend
    /// on extractor order and their motion history advances once.
    /// </summary>
    public void RetainTransform(Entity entity, in RenderTransform value) =>
        _retainedTransforms[entity] = value;

    internal void ApplyRetainedTransforms()
    {
        foreach (Entity entity in _movingTransforms)
            if (_world.IsAlive(entity) && _world.Has<RenderTransform>(entity))
                UpdateExisting(entity, new RenderPreviousTransform(_world.Read<RenderTransform>(entity)));
        foreach ((Entity entity, RenderTransform value) in _retainedTransforms)
            UpsertTransform(entity, value);
        foreach (RenderMirror mirror in _mirrors)
        {
            if (_retainedTransforms.ContainsKey(mirror.RenderEntity)) continue;
            RemoveIfExists<RenderTransform>(mirror.RenderEntity);
            RemoveIfExists<RenderPreviousTransform>(mirror.RenderEntity);
        }
    }

    internal void RefreshMirrors()
    {
        _mirrorBySource.Clear();
        _mirrors.Clear();
        RenderExtractionContext state = this;
        _world.ExecuteQuery(
            _renderSources,
            ref state,
            static (QueryCursor cursor, ref RenderExtractionContext context) =>
                context.CollectMirrors(cursor));
    }

    private void CollectMirrors(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            ReadOnlySpan<Entity> entities = chunk.Entities;
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            for (int row = 0; row < entities.Length; row++)
            {
                Entity source = sources[row].Entity;
                Entity renderEntity = entities[row];
                if (_mirrorBySource.ContainsKey(source))
                {
                    throw new InvalidOperationException(
                        $"RenderWorld contains more than one mirror for {source}.");
                }
                _mirrorBySource.Add(source, renderEntity);
                _sourceChanges.Add(source);
            }
        }
    }

    public Entity RetainMirror(Entity source)
    {
        _retainedSources.Add(source);
        if (_mirrorBySource.TryGetValue(source, out Entity entity))
            return entity;

        entity = _world.CreateEntity();
        _world.Add(entity, new RenderSource(source));
        _previousMirrors.TryAdd(source, Entity.Null);
        _mirrorBySource.Add(source, entity);
        return entity;
    }

    internal bool HasTransformChanges => _transformChanges.Count != 0 || _movingTransforms.Count != 0;

    internal void BeginTransformChanges()
    {
        _transformChanges.Clear();
        _nextMovingTransforms.Clear();
    }

    internal void CollectTransformChanges(QueryChunkView chunk)
    {
        ReadOnlySpan<WorldTransform> transforms = chunk.Read<WorldTransform>();
        ReadOnlySpan<Entity> entities = chunk.Entities;
        ReadOnlySpan<uint> versions = chunk.ReadWriteVersions<WorldTransform>();
        for (int row = 0; row < entities.Length; row++)
            if (unchecked((int)(versions[row] - chunk.LastSystemVersion)) > 0)
                CollectTransformChange(entities[row], new RenderTransform(transforms[row].Qvvs));
    }

    /// <summary>Collects a changed source transform for the same publication as feature data.</summary>
    public void CollectTransformChange(Entity source, in RenderTransform value) =>
        _transformChanges[RequireMirror(source)] = value;

    internal void ApplyTransformChanges()
    {
        _nextMovingTransforms.Clear();
        foreach (Entity entity in _movingTransforms)
            if (_world.IsAlive(entity) && _world.Has<RenderTransform>(entity))
                UpdateExisting(entity, new RenderPreviousTransform(_world.Read<RenderTransform>(entity)));
        foreach ((Entity entity, RenderTransform value) in _transformChanges)
            UpsertTransform(entity, value);
    }

    internal void ApplyTransformValueChanges()
    {
        if (!HasTransformChanges) return;
        RenderExtractionContext state = this;
        _world.ExecuteQuery(_transformWrites, ref state,
            static (QueryCursor query, ref RenderExtractionContext context) => context.ApplyTransformValues(query));
    }

    private void ApplyTransformValues(QueryCursor query)
    {
        // Dense motion is cheaper to read and write by column. Sparse edits keep
        // the entity lookup path so their cost does not scale with the whole world.
        if (_transformChanges.Count >= 64 && _transformChanges.Count >= _mirrorBySource.Count / 2)
        {
            ApplyTransformColumns(query);
            return;
        }
        // Resolve each source once, validate every destination and compute the writes before
        // changing either column. The query protects both columns and entity membership until
        // all writes finish. This scratch contains only this update's changed values.
        _transformUpdates.Clear();
        _nextMovingTransforms.Clear();
        try
        {
            foreach ((Entity entity, RenderTransform value) in _transformChanges)
            {
                if (!query.TryGetRow(entity, out QueryRow row))
                    throw new InvalidOperationException("A render mirror is missing its transform or motion history.");
                RenderTransform current = row.Read<RenderTransform>();
                var previous = new RenderPreviousTransform(current);
                bool writeCurrent = current != value;
                bool writePrevious = row.Read<RenderPreviousTransform>() != previous;
                if (writeCurrent || writePrevious)
                    _transformUpdates.Add(new(entity, value, writeCurrent, writePrevious));
                if (writeCurrent) _nextMovingTransforms.Add(entity);
            }

            foreach (Entity entity in _movingTransforms)
            {
                if (_transformChanges.ContainsKey(entity)) continue;
                if (!query.TryGetRow(entity, out QueryRow row))
                {
                    if (_world.IsAlive(entity) && _world.Has<RenderTransform>(entity))
                        RequireExisting<RenderPreviousTransform>(entity);
                    continue;
                }
                var previous = new RenderPreviousTransform(row.Read<RenderTransform>());
                if (row.Read<RenderPreviousTransform>() != previous)
                    _transformUpdates.Add(new(entity, default, false, true));
            }

            foreach (TransformWrite update in _transformUpdates)
            {
                _ = query.TryGetRow(update.Entity, out QueryRow row);
                if (update.WritePrevious) row.Write<RenderPreviousTransform>() = new(row.Read<RenderTransform>());
                if (update.WriteCurrent) row.Write<RenderTransform>() = update.Current;
            }
        }
        finally
        {
            _transformUpdates.Clear();
        }
    }

    private void ApplyTransformColumns(QueryCursor query)
    {
        _transformUpdates.Clear();
        _transformChunks.Clear();
        _nextMovingTransforms.Clear();
        try
        {
            // Check stopped or removed mirrors before either column is changed.
            _stoppedTransforms.Clear();
            foreach (Entity entity in _movingTransforms)
            {
                if (_transformChanges.ContainsKey(entity)) continue;
                _stoppedTransforms.Add(entity);
                if (!query.TryGetRow(entity, out _) &&
                    _world.IsAlive(entity) && _world.Has<RenderTransform>(entity))
                    RequireExisting<RenderPreviousTransform>(entity);
            }

            int matchedChanges = 0;
            foreach (QueryChunkView chunk in query.Chunks)
            {
                ReadOnlySpan<Entity> entities = chunk.Entities;
                ReadOnlySpan<RenderTransform> current = chunk.Read<RenderTransform>();
                ReadOnlySpan<RenderPreviousTransform> previous = chunk.Read<RenderPreviousTransform>();
                int first = _transformUpdates.Count, currentCount = 0, previousCount = 0;
                for (int row = 0; row < entities.Length; row++)
                {
                    Entity entity = entities[row];
                    bool changed = _transformChanges.TryGetValue(entity, out RenderTransform value);
                    if (changed) matchedChanges++;
                    else if (!_stoppedTransforms.Contains(entity)) continue;
                    var history = new RenderPreviousTransform(current[row]);
                    bool writeCurrent = changed && current[row] != value;
                    bool writePrevious = previous[row] != history;
                    if (writeCurrent)
                    {
                        currentCount++;
                        _nextMovingTransforms.Add(entity);
                    }
                    if (writePrevious) previousCount++;
                    if (writeCurrent || writePrevious)
                        _transformUpdates.Add(new(entity, value, writeCurrent, writePrevious, row));
                }
                _transformChunks.Add(new(first, _transformUpdates.Count - first, currentCount, previousCount));
            }
            if (matchedChanges != _transformChanges.Count)
                throw new InvalidOperationException("A render mirror is missing its transform or motion history.");

            // Query membership cannot change inside this callback. Revisit chunks
            // in the same order without resolving each entity a second time.
            int chunkIndex = 0;
            foreach (QueryChunkView chunk in query.Chunks)
            {
                TransformChunkChanges changes = _transformChunks[chunkIndex++];
                if (changes.Count == 0) continue;
                ReadOnlySpan<RenderTransform> before = chunk.Read<RenderTransform>();
                Span<RenderTransform> current = changes.CurrentCount == chunk.Count ? chunk.Write<RenderTransform>() : default;
                Span<RenderPreviousTransform> previous = changes.PreviousCount == chunk.Count ? chunk.Write<RenderPreviousTransform>() : default;
                int end = changes.First + changes.Count;
                for (int index = changes.First; index < end; index++)
                {
                    TransformWrite update = _transformUpdates[index];
                    if (update.WritePrevious)
                    {
                        var history = new RenderPreviousTransform(before[update.RowIndex]);
                        if (!previous.IsEmpty) previous[update.RowIndex] = history;
                        else chunk.Write<RenderPreviousTransform>(update.RowIndex) = history;
                    }
                    if (update.WriteCurrent)
                    {
                        if (!current.IsEmpty) current[update.RowIndex] = update.Current;
                        else chunk.Write<RenderTransform>(update.RowIndex) = update.Current;
                    }
                }
            }
        }
        finally
        {
            _transformUpdates.Clear();
            _transformChunks.Clear();
            _stoppedTransforms.Clear();
        }
    }

    internal void AcceptTransformChanges()
    {
        _previousMirrors.Clear();
        (_movingTransforms, _nextMovingTransforms) = (_nextMovingTransforms, _movingTransforms);
        _nextMovingTransforms.Clear();
        _transformChanges.Clear();
        _retainedTransforms.Clear();
    }

    internal void CancelTransformChanges()
    {
        foreach ((Entity source, Entity previous) in _previousMirrors)
            if (previous == Entity.Null) _mirrorBySource.Remove(source);
            else _mirrorBySource[source] = previous;
        _previousMirrors.Clear();
        _transformChanges.Clear();
        _retainedTransforms.Clear();
        _nextMovingTransforms.Clear();
    }

    internal void DestroyUnusedMirrors()
    {
        for (int index = 0; index < _mirrors.Count; index++)
        {
            RenderMirror mirror = _mirrors[index];
            if (!_retainedSources.Contains(mirror.Source))
            {
                _world.DestroyEntity(mirror.RenderEntity);
                _previousMirrors.TryAdd(mirror.Source, mirror.RenderEntity);
                _mirrorBySource.Remove(mirror.Source);
            }
        }
    }

    public void RemoveIfExists<T>(Entity entity)
        where T : struct, SomeEngine.ECS.IComponent
    {
        if (_world.Has<T>(entity))
            _world.Remove<T>(entity);
    }

    public void Upsert<T>(Entity entity, in T value)
        where T : struct, SomeEngine.ECS.IComponent, IEquatable<T>
    {
        if (!_world.Has<T>(entity))
        {
            _world.Add(entity, value);
            return;
        }

        T current = _world.Read<T>(entity);
        if (!current.Equals(value))
            _world.Replace(entity, value);
    }

    internal void UpsertTransform(Entity entity, in RenderTransform value)
    {
        if (!_world.Has<RenderTransform>(entity))
        {
            _world.Add(entity, value);
            _world.Add(entity, new RenderPreviousTransform(value));
            return;
        }

        RenderTransform current = _world.Read<RenderTransform>(entity);
        Upsert(entity, new RenderPreviousTransform(current));
        if (current != value)
        {
            _world.Replace(entity, value);
            _nextMovingTransforms.Add(entity);
        }
    }

    public void Dispose()
    {
        _world.ReleaseQuery(_transformWrites);
        _world.ReleaseQuery(_renderSources);
    }
}

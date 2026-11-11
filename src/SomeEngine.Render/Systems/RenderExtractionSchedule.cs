using SomeEngine.ECS;
using SomeEngine.Job;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render.Components;
using SomeEngine.Resources;
using System.Collections.Immutable;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Owns an explicitly installed group of extraction systems for one RenderWorld. All systems
/// collect from one coherent main-world read and publish through one RenderWorld structural
/// transaction, so adding a feature never turns extraction into one global switch statement.
/// </summary>
public sealed partial class RenderExtractionSystems : IDisposable
{
    private static readonly SomeEngine.Diagnostics.TraceSite ExtractionTrace = new("ecs", "RenderExtraction");
    private readonly RenderWorld _renderWorld;
    private readonly List<IRenderExtractionSystem> _systems = [];
    private readonly RenderExtractionContext _context;
    private readonly object _gate = new();
    private RenderExtractionQuery? _source;
    private Query? _sourceRows;
    private EventReader<MembershipChange>[] _sourceReaders = [];
    private ulong[] _candidateThrough = [];
    private readonly HashSet<Entity> _sourceChanges = [];
    private World? _mainWorld, _candidateMainWorld;
    private ulong _lastSystemVersion, _candidateSystemVersion;
    private bool _candidateFullReconciliation, _hasCandidate, _extractionActive, _disposed;
    private readonly HashSet<IRenderExtractionSystem> _changedSystems = [];
    private ImmutableDictionary<Texture, TextureVersions> _textureVersions = ImmutableDictionary<Texture, TextureVersions>.Empty;
    private ImmutableDictionary<long, RenderMaterialReference> _materialReferences = ImmutableDictionary<long, RenderMaterialReference>.Empty;
    private bool _texturesChanged, _materialsChanged;
    internal QueryDescription DestinationAccess => _source!.DestinationAccess;

    public RenderExtractionSystems(RenderWorld renderWorld, params ReadOnlySpan<IRenderExtractionSystem> systems)
    {
        _renderWorld = renderWorld ?? throw new ArgumentNullException(nameof(renderWorld));
        _context = new RenderExtractionContext(renderWorld);
        foreach (IRenderExtractionSystem system in systems) Add(system);
    }

    public int Count { get { lock (_gate) { ThrowIfDisposed(); return _systems.Count; } } }
    internal RenderWorld World => _renderWorld;
    internal bool HasPendingCandidate { get { lock (_gate) { ThrowIfDisposed(); return _hasCandidate; } } }

    public Entity RequireMirror(Entity source)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive) throw new InvalidOperationException("Render mirrors require a completed extraction boundary.");
            return _context.RequireMirror(source);
        }
    }

    public void Add(IRenderExtractionSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_source is not null || _extractionActive) throw new InvalidOperationException("Install extractors before collecting their sources.");
            _systems.Add(system);
        }
    }

    public void Extract(World mainWorld)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(ExtractionTrace);
        ArgumentNullException.ThrowIfNull(mainWorld);
        if (RenderWorld.IsRenderingWorld(mainWorld)) throw new ArgumentException("Extraction requires the Game World.", nameof(mainWorld));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive || _hasCandidate) throw new InvalidOperationException("Complete the previous render extraction before collecting again.");
            if (_mainWorld is not null && !ReferenceEquals(_mainWorld, mainWorld))
                throw new InvalidOperationException("A Rendering World reads one Game World.");
            _source ??= BuildSourceQuery();
            if (_systems.Count == 0) { _mainWorld ??= mainWorld; return; }
            _extractionActive = true;
            try
            {
                if (_sourceRows is null) BindSources(mainWorld);
                ExtractCore(mainWorld);
                _candidateMainWorld = mainWorld;
                _hasCandidate = true;
            }
            catch
            {
                _context.CancelTransformChanges();
                if (_mainWorld is null) ReleaseSources();
                throw;
            }
            finally { _extractionActive = false; }
        }
    }

    private RenderExtractionQuery BuildSourceQuery()
    {
        var query = new RenderExtractionQuery();
        query.Write<RenderTransform>(); query.Write<RenderPreviousTransform>();
        query.Write<RenderTextureVersions>(); query.Write<RenderMaterialReference>();
        query.ReadDestination<RenderMaterialIndex>(); query.ReadOptional<LightCookie>();
        foreach (IRenderExtractionSystem system in _systems) system.DeclareReads(query);
        return query;
    }

    private void BindSources(World world)
    {
        _sourceRows = world.Query(_source!.Build());
        _sourceReaders = new EventReader<MembershipChange>[_source.Membership.Count];
        _candidateThrough = new ulong[_sourceReaders.Length];
        for (int i = 0; i < _sourceReaders.Length; i++)
            _sourceReaders[i] = world.WatchMembership(world.Query(_source.Membership[i]));
    }

    private void ExtractCore(World mainWorld)
    {
        _context.BeginTransformChanges();
        foreach (IRenderExtractionSystem system in _systems) system.Reset();
        _changedSystems.Clear(); _sourceChanges.Clear();
        using (QueryScope source = _sourceRows!.Value.Since(_lastSystemVersion).OpenRead())
        {
            _candidateSystemVersion = mainWorld.Version;
            for (int i = 0; i < _sourceReaders.Length; i++)
            {
                EventReadRange range = _sourceReaders[i].Capture();
                using EventRead<MembershipChange> read = _sourceReaders[i].Open(range);
                foreach (MembershipChange change in read) _sourceChanges.Add(change.Entity);
                _candidateThrough[i] = range.Through;
            }
            _candidateFullReconciliation = _mainWorld is null || _sourceChanges.Count != 0;
            _context.BeginSourceChanges(_sourceChanges);
            CollectRanges(source, mainWorld.Execution);
            foreach (IRenderExtractionSystem system in _systems)
                if (system.CompleteSourceCollection(mainWorld, _candidateFullReconciliation)) _changedSystems.Add(system);
            var resources = TextureExtraction.Collect(source);
            foreach (IRenderExtractionSystem system in _systems) system.CollectResources(source, resources);
            _textureVersions = resources.Textures; _materialReferences = resources.Materials;
            _context.MaterialReferences = _materialReferences;
            _materialsChanged = _materialReferences.Values.Any(value => _renderWorld.TryReadMaterial(value.Material.MaterialId, out _)
                && _renderWorld.ReadMaterialRevision(value.Material.MaterialId) != value.Revision);
            var before = _renderWorld.TextureVersions;
            _texturesChanged = before.Count != _textureVersions.Count
                || _textureVersions.Any(pair => !before.TryGetValue(pair.Key, out var version) || version != pair.Value);
        }
        foreach (IRenderExtractionSystem system in _systems)
            if (system.CompleteCollection(_context, _candidateFullReconciliation)) _changedSystems.Add(system);
    }

    internal bool ApplyPendingCandidate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_extractionActive) throw new InvalidOperationException("Source collection must finish before the render update.");
            if (!_hasCandidate) return false;
            _extractionActive = true;
            try
            {
                ApplyCandidateCore();
                _context.AcceptTransformChanges();
                _lastSystemVersion = _candidateSystemVersion;
                for (int i = 0; i < _sourceReaders.Length; i++) _sourceReaders[i].Advance(_candidateThrough[i]);
                _mainWorld ??= _candidateMainWorld;
                ClearCandidate();
                return true;
            }
            catch
            {
                _context.CancelTransformChanges();
                if (_mainWorld is null) ReleaseSources();
                ClearCandidate();
                throw;
            }
            finally { _extractionActive = false; }
        }
    }

    internal void SelectTarget(World target) => _context.SelectWorld(target);

    private void ApplyCandidateCore()
    {
        if (_candidateFullReconciliation)
        {
            if (_mainWorld is null) _context.RefreshMirrors();
            _renderWorld.ApplyExtraction(this);
        }
        else if (_changedSystems.Count != 0 || _context.HasTransformChanges || _texturesChanged || _materialsChanged)
            if (!_renderWorld.TryApplyExtractionChangesDirect(this)) _renderWorld.ApplyExtractionChanges(this);
    }

    internal void ApplyCandidate()
    {
        _context.BeginCandidate();
        using var batch = _context.World.OpenWriteBatch();
        foreach (IRenderExtractionSystem system in _systems) system.Apply(_context);
        if (_texturesChanged) _renderWorld.UpdateTextureVersions(_context.World, _textureVersions);
        if (_materialsChanged) _renderWorld.UpdateMaterials(_context.World, _materialReferences.Values);
        _context.ApplyRetainedTransforms(); _context.DestroyUnusedMirrors();
    }

    internal void ApplyChangesCandidate()
    {
        using var batch = _context.World.OpenWriteBatch();
        _context.ApplyTransformValueChanges();
        foreach (IRenderExtractionSystem system in _changedSystems) system.ApplyChanges(_context);
        if (_texturesChanged) _renderWorld.UpdateTextureVersions(_context.World, _textureVersions);
        if (_materialsChanged) _renderWorld.UpdateMaterials(_context.World, _materialReferences.Values);
    }

    internal bool TryApplyChangesDirect()
    {
        if (_changedSystems.Any(static system => !system.CanApplyChangesDirect)) return false;
        foreach (IRenderExtractionSystem system in _changedSystems) system.ValidateChanges(_context);
        ApplyChangesCandidate();
        return true;
    }

    private void ClearCandidate()
    {
        _candidateMainWorld = null; _candidateSystemVersion = 0; _candidateFullReconciliation = false;
        _changedSystems.Clear(); _sourceChanges.Clear(); _hasCandidate = false;
        _textureVersions = ImmutableDictionary<Texture, TextureVersions>.Empty;
        _materialReferences = ImmutableDictionary<long, RenderMaterialReference>.Empty;
        _context.MaterialReferences = _materialReferences; _texturesChanged = _materialsChanged = false;
    }

    private void ReleaseSources()
    {
        foreach (EventReader<MembershipChange> reader in _sourceReaders) reader?.Dispose();
        _sourceReaders = []; _candidateThrough = []; _sourceRows = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_extractionActive) throw new InvalidOperationException("Finish extraction before disposing it.");
            _context.CancelTransformChanges();
            foreach (RangeCollection? collection in _rangeCollections) collection?.Dispose();
            foreach (IRenderExtractionSystem system in _systems) system.Dispose();
            _context.Dispose(); ReleaseSources(); ClearCandidate(); _disposed = true;
        }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Restricts extraction modules to optional source reads.</summary>
public sealed class RenderExtractionQuery
{
    private readonly QueryDescription _builder = new();
    internal QueryDescription DestinationAccess { get; } = new QueryDescription().Optional<RenderSource>();
    internal List<QueryDescription> Membership { get; } = [];
    private readonly HashSet<int> _membershipTypes = [];

    public void Write<T>() where T : struct, IComponent => DestinationAccess.Optional<T>();
    internal void ReadDestination<T>() where T : struct, IComponent => DestinationAccess.Optional<T>();
    public void ReadOptional<T>() where T : struct
    {
        _builder.Optional<T>();
        if (_membershipTypes.Add(ComponentId<T>.Value)) Membership.Add(new QueryDescription().WithStored<T>());
    }
    public void ReadOptionalBuffer<T>() where T : struct, IBufferElement => ReadOptional<Buffer<T>>();
    internal QueryDescription Build() => _builder;
}

public interface IRenderExtractionSystem : IDisposable
{
    void DeclareReads(RenderExtractionQuery query);

    void Reset();

    /// <summary>Collects referenced resources from all current sources in the same read window.</summary>
    void CollectResources(scoped QueryScope source, RenderExtractionResources resources) { }

    /// <summary>Collects current data from an affected source chunk; unrelated chunks are not replayed.</summary>
    void Collect(QueryChunk chunk);

    /// <summary>Applies collected data and reconciles only the changed mirrors exposed by the context.</summary>
    void Apply(RenderExtractionContext context);

    /// <summary>Collects only values changed since the preceding extraction.</summary>
    bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context);

    /// <summary>
    /// Finishes source reads inside the same mutation-free extraction window, for data such as
    /// ordered hierarchy children. Apply callbacks consume only the collected values.
    /// </summary>
    bool CompleteSourceCollection(World sourceWorld, bool fullCollection) => false;

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
    private readonly RenderWorld _renderWorld;
    private World _world = null!;
    private Query _renderSources;
    private Query _transformWrites;
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
        _renderWorld = world;
        SelectWorld(world.World);
    }

    internal void SelectWorld(World world)
    {
        _world = world;
        _renderSources = world.Query(new QueryDescription().With<RenderSource>());
        _transformWrites = world.Query(new QueryDescription().With<RenderTransform>().With<RenderPreviousTransform>());
    }

    public World World => _world;

    /// <summary>Sets a mirror's material bindings and retains their shared rendering-world references.</summary>
    internal IReadOnlyDictionary<long, RenderMaterialReference> MaterialReferences { get; set; } =
        ImmutableDictionary<long, RenderMaterialReference>.Empty;

    public void SetMaterials(Entity entity, ReadOnlySpan<Material> materials) =>
        _renderWorld.SetMaterials(_world, entity, materials, MaterialReferences);

    /// <summary>Removes a mirror's bindings and releases its shared material references.</summary>
    public void RemoveMaterials(Entity entity) => _renderWorld.RemoveMaterials(_world, entity);

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
            _world.Set(entity, value);
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
        foreach ((Entity entity, RenderTransform value) in _transformChanges)
            if (!_retainedTransforms.ContainsKey(entity)) UpsertTransform(entity, value);
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
        using QueryScope source = _renderSources.OpenRead();
        foreach (QueryChunk chunk in source)
        {
            ReadOnlySpan<Entity> entities = chunk.Entities;
            ReadOnlySpan<RenderSource> sources = chunk.Read<RenderSource>();
            for (int row = 0; row < entities.Length; row++)
            {
                Entity entity = sources[row].Entity;
                if (!_mirrorBySource.TryAdd(entity, entities[row]))
                    throw new InvalidOperationException($"The Rendering World has multiple mirrors for {entity}.");
                _sourceChanges.Add(entity);
            }
        }
    }

    public Entity RetainMirror(Entity source)
    {
        _retainedSources.Add(source);
        if (_mirrorBySource.TryGetValue(source, out Entity entity))
            return entity;

        entity = _world.Create();
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

    internal void CollectTransformChanges(QueryChunk chunk, ulong since)
    {
        ReadOnlySpan<WorldTransform> transforms = chunk.Read<WorldTransform>();
        for (int row = 0; row < chunk.Count; row++)
            if (chunk.WasWritten<WorldTransform>(row, since))
                CollectTransformChange(chunk.Entities[row], new RenderTransform(transforms[row].Qvvs));
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
        using QueryScope query = _transformWrites.Open();
        ApplyTransformValues(query);
    }

    private void ApplyTransformValues(scoped QueryScope query)
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
                if (!_world.IsAlive(entity) || !_world.Has<RenderTransform>(entity) || !_world.Has<RenderPreviousTransform>(entity))
                    throw new InvalidOperationException("A render mirror requires its transform and motion history.");
                RenderTransform current = _world.Read<RenderTransform>(entity);
                var previous = new RenderPreviousTransform(current);
                bool writeCurrent = current != value;
                bool writePrevious = _world.Read<RenderPreviousTransform>(entity) != previous;
                if (writeCurrent || writePrevious)
                    _transformUpdates.Add(new(entity, value, writeCurrent, writePrevious));
                if (writeCurrent) _nextMovingTransforms.Add(entity);
            }

            foreach (Entity entity in _movingTransforms)
            {
                if (_transformChanges.ContainsKey(entity)) continue;
                if (!_world.IsAlive(entity) || !_world.Has<RenderTransform>(entity) || !_world.Has<RenderPreviousTransform>(entity))
                {
                    if (_world.IsAlive(entity) && _world.Has<RenderTransform>(entity))
                        RequireExisting<RenderPreviousTransform>(entity);
                    continue;
                }
                var previous = new RenderPreviousTransform(_world.Read<RenderTransform>(entity));
                if (_world.Read<RenderPreviousTransform>(entity) != previous)
                    _transformUpdates.Add(new(entity, default, false, true));
            }

            foreach (TransformWrite update in _transformUpdates)
            {
                if (update.WritePrevious) _world.Get<RenderPreviousTransform>(update.Entity) = new(_world.Read<RenderTransform>(update.Entity));
                if (update.WriteCurrent) _world.Get<RenderTransform>(update.Entity) = update.Current;
            }
        }
        finally
        {
            _transformUpdates.Clear();
        }
    }

    private void ApplyTransformColumns(scoped QueryScope query)
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
                if (_world.IsAlive(entity) && _world.Has<RenderTransform>(entity) && !_world.Has<RenderPreviousTransform>(entity))
                    RequireExisting<RenderPreviousTransform>(entity);
            }

            int matchedChanges = 0;
            foreach (QueryChunk chunk in query)
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
            foreach (QueryChunk chunk in query)
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
                _renderWorld.RemoveMaterials(_world, mirror.RenderEntity);
                _world.Destroy(mirror.RenderEntity);
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
            _world.Set(entity, value);
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
            _world.Set(entity, value);
            _nextMovingTransforms.Add(entity);
        }
    }

    public void Dispose()
    {
        _mirrorBySource.Clear();
        _mirrors.Clear();
    }
}

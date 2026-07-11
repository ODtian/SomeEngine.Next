using System.Collections.Concurrent;

namespace SomeEngine.RenderGraph;

internal sealed class CompilationCache : IDisposable
{
    // Increment whenever compiler semantics can change without changing canonical graph bytes.
    internal const ulong CompilerSemanticGeneration = 5;

    private readonly IDevice _device;
    private readonly DeviceDomain _domain;
    private readonly int _coordinatorThread;
    private readonly int _entryLimit;
    private readonly long _payloadByteBudget;
    private readonly bool _compileOptimizedPlansAsynchronously;
    private readonly GraphCompiler _compiler;
    private readonly ConcurrentQueue<CompilationFlight> _completed = new();
    private readonly Dictionary<GraphSignature, List<CompilationFlight>> _active = new();
    private readonly Dictionary<GraphSignature, List<CompilationCacheEntry>> _resident = new();
    private readonly List<CompilationCacheEntry> _retiring = new();
    private readonly Action<CompilationEvent> _report;
    private readonly Action<RenderGraphCompilationDiagnostic>? _reportDiagnostic;
    private readonly ulong _compilerPolicy;
    private long _accessOrdinal;
    private long _residentPayloadBytes;
    private long _retiringPayloadBytes;
    private int _residentEntryCount;
    private bool _disposed;

    public CompilationCache(
        IDevice device,
        int entryLimit,
        long payloadByteBudget,
        bool compileOptimizedPlansAsynchronously,
        Action<CompilationEvent> report,
        GraphCompiler? compiler = null,
        Action<RenderGraphCompilationDiagnostic>? reportDiagnostic = null,
        ulong compilerPolicy = 0)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (entryLimit < 0) throw new ArgumentOutOfRangeException(nameof(entryLimit));
        if (payloadByteBudget < 0) throw new ArgumentOutOfRangeException(nameof(payloadByteBudget));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _reportDiagnostic = reportDiagnostic;
        _compilerPolicy = compilerPolicy;
        _domain = device.Domain;
        _coordinatorThread = Environment.CurrentManagedThreadId;
        _entryLimit = entryLimit;
        _payloadByteBudget = payloadByteBudget;
        _compileOptimizedPlansAsynchronously = compileOptimizedPlansAsynchronously;
        _compiler = compiler ?? Compiler.Compile;
    }

    internal int ResidentEntryCount => _residentEntryCount;
    internal long ResidentPayloadBytes => _residentPayloadBytes;
    internal long RetiringPayloadBytes => _retiringPayloadBytes;
    internal int RetiringEntryCount => _retiring.Count;

    public CompiledGraphLease Acquire(FrozenGraph invocationGraph, DeviceCompilationSnapshot compilation)
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(invocationGraph);
        ArgumentNullException.ThrowIfNull(compilation);
        CompilationEnvironment environment = CreateEnvironment(compilation);

        DrainCore(compilation);
        CompilationCacheEntry? entry = FindResident(invocationGraph.Canonical, environment);
        if (entry is not null)
        {
            _report(CompilationEvent.CacheHit);
            _report(entry.Graph.Optimized
                ? CompilationEvent.OptimizedPlanSelected
                : CompilationEvent.ConservativePlanSelected);
            Touch(entry);
            entry.ActiveLeaseCount = checked(entry.ActiveLeaseCount + 1);
            if (!entry.Graph.Optimized && !entry.OptimizedCompilationFailed)
                EnsureOptimizedFlight(entry, invocationGraph, compilation);
            return new CompiledGraphLease(this, entry);
        }

        _report(CompilationEvent.CacheMiss);
        CompilationCacheKey key = new(invocationGraph.Canonical, environment);
        CompiledGraph conservative;
        try
        {
            conservative = _compiler(invocationGraph, compilation, optimized: false);
            CompiledGraphContract.Validate(invocationGraph, conservative, compilation, optimized: false);
        }
        catch (ConservativePlanUnavailableException exception)
        {
            return AcquireRequiredOptimized(key, invocationGraph, compilation, exception);
        }
        _report(CompilationEvent.ConservativePlanCompiled);
        _report(CompilationEvent.ConservativePlanSelected);
        entry = new CompilationCacheEntry(key, conservative, EstimatePayloadBytes(key, conservative));
        entry.ActiveLeaseCount = 1;
        if (!CanRetain(entry))
        {
            BeginUnretainedRetirement(entry);
            return new CompiledGraphLease(this, entry);
        }
        AddResident(entry);
        Touch(entry);
        TrimResidentSet();
        if (entry.State == CompilationCacheEntryState.Resident)
        {
            EnsureOptimizedFlight(entry, invocationGraph, compilation);
        }
        return new CompiledGraphLease(this, entry);
    }

    public void Drain()
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainCore(_device.Compilation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureCoordinator();
        if (_resident.Values.SelectMany(static bucket => bucket).Any(static entry => entry.ActiveLeaseCount != 0) ||
            _retiring.Any(static entry => entry.ActiveLeaseCount != 0))
        {
            throw new InvalidOperationException("Cannot dispose the compilation cache while an invocation still owns a compiled-plan lease.");
        }

        CompilationFlight[] flights = _active.Values.SelectMany(static bucket => bucket).ToArray();
        foreach (CompilationFlight flight in flights)
        {
            flight.Handle.Complete();
            flight.Enqueue(_completed);
            if (flight.Error is not null)
            {
                ReportDiagnostic(
                    flight.Key,
                    CompilationFailureStage.OptimizedCompilation,
                    flight.Error,
                    flight.PassNames);
                _report(CompilationEvent.CandidateFailed);
            }
            else if (flight.Result is null || flight.ContractError is not null)
            {
                Exception contractFailure = flight.ContractError ??
                    new InvalidOperationException("The optimized graph compiler returned no result.");
                ReportDiagnostic(
                    flight.Key,
                    CompilationFailureStage.ResultContract,
                    contractFailure,
                    flight.PassNames);
                _report(CompilationEvent.CandidateFailed);
            }
        }

        // Shutdown is not a plan-selection or eviction boundary. Joined candidates can no longer
        // serve an invocation, and CompiledGraph is a pure managed lowering payload, so terminal
        // cleanup releases it without publishing candidates or polluting runtime policy metrics.
        foreach (CompilationCacheEntry entry in _resident.Values.SelectMany(static bucket => bucket))
            entry.State = CompilationCacheEntryState.Retired;
        foreach (CompilationCacheEntry entry in _retiring)
            entry.State = CompilationCacheEntryState.Retired;
        _resident.Clear();
        _retiring.Clear();
        _residentEntryCount = 0;
        _residentPayloadBytes = 0;
        _retiringPayloadBytes = 0;
        _active.Clear();
        _completed.Clear();
        _disposed = true;
    }

    internal void Release(CompilationCacheEntry entry)
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.ActiveLeaseCount <= 0)
            throw new InvalidOperationException("Compilation-cache lease count underflow.");

        entry.ActiveLeaseCount--;
        TrimResidentSet();
        CollectRetired();
    }

    private void DrainCore(DeviceCompilationSnapshot currentCompilation)
    {
        foreach (List<CompilationFlight> bucket in _active.Values)
        foreach (CompilationFlight flight in bucket)
        {
            if (flight.Handle.IsCompleted) flight.Enqueue(_completed);
        }

        List<CompilationFlight> candidates = [];
        // Fix the publication boundary before draining. A worker callback that arrives after this
        // snapshot remains queued for the next coordinator boundary instead of racing the current
        // invocation's plan selection.
        int candidateCountAtBoundary = _completed.Count;
        for (int index = 0; index < candidateCountAtBoundary; index++)
        {
            if (_completed.TryDequeue(out CompilationFlight? flight)) candidates.Add(flight);
        }
        candidates.Sort(static (left, right) => left.Key.CompareTo(right.Key));
        CompilationEnvironment currentEnvironment = CreateEnvironment(currentCompilation);
        foreach (CompilationFlight candidate in candidates)
        {
            RemoveFlight(candidate);
            if (candidate.ConsumedByRequiredJoin) continue;
            if (candidate.Error is not null)
            {
                CompilationCacheEntry? failedEntry = FindResident(candidate.Key);
                if (failedEntry is not null) failedEntry.OptimizedCompilationFailed = true;
                ReportDiagnostic(
                    candidate.Key,
                    CompilationFailureStage.OptimizedCompilation,
                    candidate.Error,
                    candidate.PassNames);
                _report(CompilationEvent.CandidateFailed);
                continue;
            }

            CompiledGraph? result = candidate.Result;
            CompilationCacheEntry? current = FindResident(candidate.Key);
            if (result is null || candidate.ContractError is not null)
            {
                if (current is not null) current.OptimizedCompilationFailed = true;
                Exception contractFailure = candidate.ContractError ??
                    new InvalidOperationException("The optimized graph compiler returned no result.");
                ReportDiagnostic(
                    candidate.Key,
                    CompilationFailureStage.ResultContract,
                    contractFailure,
                    candidate.PassNames);
                _report(CompilationEvent.CandidateFailed);
                continue;
            }
            if (candidate.Key.Environment != currentEnvironment || current is null || current.Graph.Optimized)
            {
                _report(CompilationEvent.CandidateDropped);
                continue;
            }

            CompilationCacheEntry published = new(
                candidate.Key,
                result,
                EstimatePayloadBytes(candidate.Key, result))
            {
                LastAccessOrdinal = current.LastAccessOrdinal,
            };
            if (!CanRetain(published))
            {
                current.OptimizedCompilationFailed = true;
                _report(CompilationEvent.CandidateDropped);
                continue;
            }
            RemoveResident(current);
            BeginRetirement(current);
            AddResident(published);
            _report(CompilationEvent.CandidatePublished);
        }

        CollectRetired();
        TrimResidentSet();
        CollectRetired();
    }

    private void EnsureOptimizedFlight(
        CompilationCacheEntry entry,
        FrozenGraph invocationGraph,
        DeviceCompilationSnapshot compilation)
    {
        // Policy zero is the conservative lowering itself: compiling it again with only the
        // Optimized marker changed produces no useful candidate.
        if (!_compileOptimizedPlansAsynchronously || _compilerPolicy == 0) return;
        CompilationFlight? flight = GetOrStartOptimizedFlight(
            entry.Key,
            invocationGraph,
            compilation,
            out _);
        if (flight is null) entry.OptimizedCompilationFailed = true;
    }

    private CompiledGraphLease AcquireRequiredOptimized(
        CompilationCacheKey key,
        FrozenGraph invocationGraph,
        DeviceCompilationSnapshot compilation,
        ConservativePlanUnavailableException conservativeFailure)
    {
        CompilationFlight? flight = GetOrStartOptimizedFlight(
            key,
            invocationGraph,
            compilation,
            out Exception? schedulingFailure);
        if (flight is null)
        {
            throw new InvalidOperationException(
                "The conservative plan was unavailable and the required optimized compilation could not be scheduled.",
                schedulingFailure ?? conservativeFailure);
        }

        try
        {
            flight.Handle.Complete();
        }
        catch (Exception exception)
        {
            ReportDiagnostic(
                key,
                CompilationFailureStage.Scheduling,
                exception,
                invocationGraph.Passes.Select(static pass => pass.Name).ToArray());
            _report(CompilationEvent.CandidateFailed);
            flight.MarkConsumedByRequiredJoin();
            RemoveFlight(flight);
            throw new InvalidOperationException(
                "The conservative plan was unavailable and the required optimized compilation did not complete.",
                exception);
        }

        flight.MarkConsumedByRequiredJoin();
        RemoveFlight(flight);
        CompilationEnvironment currentEnvironment = CreateEnvironment(_device.Compilation);
        if (key.Environment != currentEnvironment)
            throw new InvalidOperationException("Device compilation semantics changed during the required optimized compilation.");
        if (flight.Error is not null)
        {
            ReportDiagnostic(
                key,
                CompilationFailureStage.OptimizedCompilation,
                flight.Error,
                flight.PassNames);
            _report(CompilationEvent.CandidateFailed);
            throw new InvalidOperationException(
                "The conservative plan was unavailable and the required optimized compilation failed.",
                new AggregateException(conservativeFailure, flight.Error));
        }
        CompiledGraph? result = flight.Result;
        if (result is null || flight.ContractError is not null)
        {
            Exception contractFailure = flight.ContractError ??
                new InvalidOperationException("The required optimized graph compiler returned no result.");
            ReportDiagnostic(
                key,
                CompilationFailureStage.ResultContract,
                contractFailure,
                flight.PassNames);
            _report(CompilationEvent.CandidateFailed);
            throw contractFailure;
        }
        CompiledGraph optimized = result;

        CompilationCacheEntry entry = new(key, optimized, EstimatePayloadBytes(key, optimized))
        {
            ActiveLeaseCount = 1,
        };
        _report(CompilationEvent.OptimizedPlanSelected);
        if (!CanRetain(entry))
        {
            BeginUnretainedRetirement(entry);
            return new CompiledGraphLease(this, entry);
        }
        AddResident(entry);
        Touch(entry);
        TrimResidentSet();
        _report(CompilationEvent.CandidatePublished);
        return new CompiledGraphLease(this, entry);
    }

    private CompilationFlight? GetOrStartOptimizedFlight(
        CompilationCacheKey key,
        FrozenGraph invocationGraph,
        DeviceCompilationSnapshot compilation,
        out Exception? schedulingFailure)
    {
        schedulingFailure = null;
        CompilationFlight? existing = FindFlight(key);
        if (existing is not null)
        {
            _report(CompilationEvent.SingleFlightJoin);
            return existing;
        }

        CompilationFlight created = new(
            key,
            invocationGraph.DetachForCompilation(),
            compilation,
            _compiler);
        try
        {
            created.Schedule(_completed);
        }
        catch (Exception exception)
        {
            schedulingFailure = exception;
            try
            {
                created.Handle.Complete();
            }
            catch
            {
                // A failed scheduler has no usable candidate to join.
            }
            ReportDiagnostic(
                key,
                CompilationFailureStage.Scheduling,
                exception,
                invocationGraph.Passes.Select(static pass => pass.Name).ToArray());
            _report(CompilationEvent.CandidateFailed);
            return null;
        }

        if (!_active.TryGetValue(key.Signature, out List<CompilationFlight>? bucket))
        {
            bucket = [];
            _active.Add(key.Signature, bucket);
        }
        bucket.Add(created);
        _report(CompilationEvent.FlightStarted);
        return created;
    }

    private CompilationCacheEntry? FindResident(GraphCanonicalData canonical, in CompilationEnvironment environment)
    {
        if (!_resident.TryGetValue(canonical.Signature, out List<CompilationCacheEntry>? bucket)) return null;
        foreach (CompilationCacheEntry entry in bucket)
        {
            if (entry.State == CompilationCacheEntryState.Resident && entry.Key.Matches(canonical, environment)) return entry;
        }
        return null;
    }

    private CompilationCacheEntry? FindResident(CompilationCacheKey key)
    {
        if (!_resident.TryGetValue(key.Signature, out List<CompilationCacheEntry>? bucket)) return null;
        foreach (CompilationCacheEntry entry in bucket)
        {
            if (entry.State == CompilationCacheEntryState.Resident && entry.Key.ExactEquals(key)) return entry;
        }
        return null;
    }

    private CompilationFlight? FindFlight(CompilationCacheKey key)
    {
        if (!_active.TryGetValue(key.Signature, out List<CompilationFlight>? bucket)) return null;
        foreach (CompilationFlight flight in bucket)
        {
            if (flight.Key.ExactEquals(key)) return flight;
        }
        return null;
    }

    private void AddResident(CompilationCacheEntry entry)
    {
        if (!_resident.TryGetValue(entry.Key.Signature, out List<CompilationCacheEntry>? bucket))
        {
            bucket = [];
            _resident.Add(entry.Key.Signature, bucket);
        }
        bucket.Add(entry);
        _residentEntryCount = checked(_residentEntryCount + 1);
        _residentPayloadBytes = checked(_residentPayloadBytes + entry.PayloadBytes);
    }

    private void RemoveResident(CompilationCacheEntry entry)
    {
        if (!_resident.TryGetValue(entry.Key.Signature, out List<CompilationCacheEntry>? bucket) || !bucket.Remove(entry))
            throw new InvalidOperationException("Compilation-cache resident entry was not present in its exact-key bucket.");
        if (bucket.Count == 0) _resident.Remove(entry.Key.Signature);
        _residentEntryCount--;
        _residentPayloadBytes -= entry.PayloadBytes;
    }

    private void RemoveFlight(CompilationFlight flight)
    {
        if (!_active.TryGetValue(flight.Key.Signature, out List<CompilationFlight>? bucket) || !bucket.Remove(flight)) return;
        if (bucket.Count == 0) _active.Remove(flight.Key.Signature);
    }

    private void TrimResidentSet()
    {
        while (_residentEntryCount > _entryLimit || _residentPayloadBytes > _payloadByteBudget)
        {
            CompilationCacheEntry? victim = null;
            foreach (CompilationCacheEntry candidate in _resident.Values.SelectMany(static bucket => bucket))
            {
                if (candidate.ActiveLeaseCount != 0) continue;
                if (victim is null || CompareEvictionOrder(candidate, victim) < 0) victim = candidate;
            }
            // A CompiledGraph contains only managed lowering data. The invocation's CPU lease is
            // its sole correctness pin; native resource retirement belongs to the RHI.
            if (victim is null) return;
            Evict(victim);
        }
    }

    private void Evict(CompilationCacheEntry entry)
    {
        RemoveResident(entry);
        BeginRetirement(entry);
        _report(CompilationEvent.EntryEvicted);
    }

    private void BeginRetirement(CompilationCacheEntry entry)
    {
        if (entry.State != CompilationCacheEntryState.Resident)
            throw new InvalidOperationException("Only a resident compilation entry can begin retirement.");
        entry.State = CompilationCacheEntryState.Retiring;
        _retiring.Add(entry);
        _retiringPayloadBytes = checked(_retiringPayloadBytes + entry.PayloadBytes);
    }

    private void BeginUnretainedRetirement(CompilationCacheEntry entry)
    {
        if (entry.State != CompilationCacheEntryState.Resident)
            throw new InvalidOperationException("A new unretained compilation entry has an invalid lifecycle state.");
        entry.State = CompilationCacheEntryState.Retiring;
        _retiring.Add(entry);
        _retiringPayloadBytes = checked(_retiringPayloadBytes + entry.PayloadBytes);
    }

    private void CollectRetired()
    {
        for (int index = _retiring.Count - 1; index >= 0; index--)
        {
            CompilationCacheEntry entry = _retiring[index];
            if (entry.ActiveLeaseCount != 0) continue;
            _retiring.RemoveAt(index);
            _retiringPayloadBytes -= entry.PayloadBytes;
            entry.State = CompilationCacheEntryState.Retired;
            _report(CompilationEvent.EntryRetired);
        }
    }

    private void Touch(CompilationCacheEntry entry) => entry.LastAccessOrdinal = checked(++_accessOrdinal);

    private bool CanRetain(CompilationCacheEntry entry) =>
        _entryLimit != 0 &&
        _payloadByteBudget != 0 &&
        entry.PayloadBytes <= _payloadByteBudget;

    private CompilationEnvironment CreateEnvironment(DeviceCompilationSnapshot compilation) => new(
        _domain,
        compilation.SemanticGeneration,
        CompilerSemanticGeneration,
        _compilerPolicy);

    private static long EstimatePayloadBytes(CompilationCacheKey key, CompiledGraph graph) =>
        checked(key.CanonicalByteCount + graph.EstimatedRetainedBytes);

    private static int CompareEvictionOrder(CompilationCacheEntry left, CompilationCacheEntry right)
    {
        int access = left.LastAccessOrdinal.CompareTo(right.LastAccessOrdinal);
        return access != 0 ? access : left.Key.CompareTo(right.Key);
    }

    private void EnsureCoordinator()
    {
        if (Environment.CurrentManagedThreadId != _coordinatorThread)
            throw new InvalidOperationException("Compilation-cache lookup, publication, eviction, and retirement belong to the render coordinator thread.");
    }

    private void ReportDiagnostic(
        CompilationCacheKey key,
        CompilationFailureStage stage,
        Exception exception,
        IReadOnlyList<string> passNames)
    {
        if (_reportDiagnostic is null) return;
        try
        {
            _reportDiagnostic(new RenderGraphCompilationDiagnostic(
                key.SignatureText,
                stage,
                passNames,
                exception));
        }
        catch
        {
            // A telemetry/diagnostic consumer cannot invalidate a correct conservative plan.
        }
    }
}

internal readonly record struct CompilationEnvironment(
    DeviceDomain Domain,
    ulong DeviceSemanticGeneration,
    ulong CompilerSemanticGeneration,
    ulong CompilerPolicy = 0);

internal sealed class CompilationCacheKey : IComparable<CompilationCacheKey>
{
    private readonly byte[] _canonicalBytes;

    public CompilationCacheKey(GraphCanonicalData canonical, in CompilationEnvironment environment)
        : this(canonical.Signature, environment, canonical.Bytes)
    {
    }

    internal CompilationCacheKey(
        GraphSignature signature,
        in CompilationEnvironment environment,
        ReadOnlySpan<byte> canonicalBytes)
    {
        Signature = signature;
        Environment = environment;
        _canonicalBytes = canonicalBytes.ToArray();
    }

    public GraphSignature Signature { get; }
    public CompilationEnvironment Environment { get; }
    public int CanonicalByteCount => _canonicalBytes.Length;
    public string SignatureText =>
        $"{Signature.Word0:x16}{Signature.Word1:x16}{Signature.Word2:x16}{Signature.Word3:x16}";

    public bool Matches(GraphCanonicalData canonical, in CompilationEnvironment environment) =>
        Signature == canonical.Signature &&
        Environment == environment &&
        _canonicalBytes.AsSpan().SequenceEqual(canonical.Bytes);

    public bool ExactEquals(CompilationCacheKey other) =>
        Signature == other.Signature &&
        Environment == other.Environment &&
        _canonicalBytes.AsSpan().SequenceEqual(other._canonicalBytes);

    public int CompareTo(CompilationCacheKey? other)
    {
        if (other is null) return 1;
        // A CompilationCache is permanently scoped to one DeviceDomain. ExactEquals still checks
        // the domain defensively; deterministic ordering only needs fields that can differ within
        // one cache and avoids exposing an ordering for the intentionally opaque domain token.
        int deviceGeneration = Environment.DeviceSemanticGeneration.CompareTo(other.Environment.DeviceSemanticGeneration);
        if (deviceGeneration != 0) return deviceGeneration;
        int compilerGeneration = Environment.CompilerSemanticGeneration.CompareTo(other.Environment.CompilerSemanticGeneration);
        if (compilerGeneration != 0) return compilerGeneration;
        int compilerPolicy = Environment.CompilerPolicy.CompareTo(other.Environment.CompilerPolicy);
        if (compilerPolicy != 0) return compilerPolicy;
        int signature = Signature.CompareTo(other.Signature);
        return signature != 0 ? signature : _canonicalBytes.AsSpan().SequenceCompareTo(other._canonicalBytes);
    }
}

internal enum CompilationCacheEntryState : byte
{
    Resident,
    Retiring,
    Retired,
}

internal sealed class CompilationCacheEntry
{
    public CompilationCacheEntry(CompilationCacheKey key, CompiledGraph graph, long payloadBytes)
    {
        Key = key;
        Graph = graph;
        PayloadBytes = payloadBytes;
    }

    public CompilationCacheKey Key { get; }
    public CompiledGraph Graph { get; }
    public long PayloadBytes { get; }
    public long LastAccessOrdinal { get; set; }
    public int ActiveLeaseCount { get; set; }
    public bool OptimizedCompilationFailed { get; set; }
    public CompilationCacheEntryState State { get; set; }
}

internal sealed class CompiledGraphLease
{
    private CompilationCache? _owner;
    private CompilationCacheEntry? _entry;

    public CompiledGraphLease(CompilationCache owner, CompilationCacheEntry entry)
    {
        _owner = owner;
        _entry = entry;
        Graph = entry.Graph;
    }

    public CompiledGraph Graph { get; }

    public void Release()
    {
        CompilationCache owner = _owner ?? throw new InvalidOperationException("The compiled-graph lease has already been released.");
        CompilationCacheEntry entry = _entry!;
        owner.Release(entry);
        _owner = null;
        _entry = null;
    }
}

internal sealed class CompilationFlight
{
    private readonly FrozenGraph _graph;
    private readonly DeviceCompilationSnapshot _device;
    private readonly GraphCompiler _compiler;
    private int _enqueued;
    private int _consumedByRequiredJoin;

    public CompilationFlight(
        CompilationCacheKey key,
        FrozenGraph graph,
        DeviceCompilationSnapshot device,
        GraphCompiler compiler)
    {
        Key = key;
        _graph = graph;
        _device = device;
        _compiler = compiler;
        PassNames = graph.Passes.Select(static pass => pass.Name).ToArray();
    }

    public CompilationCacheKey Key { get; }
    public CompiledGraph? Result { get; private set; }
    public Exception? Error { get; private set; }
    public Exception? ContractError { get; private set; }
    public string[] PassNames { get; }
    public JobHandle Handle { get; private set; }
    public bool ConsumedByRequiredJoin => Volatile.Read(ref _consumedByRequiredJoin) != 0;

    public void Schedule(ConcurrentQueue<CompilationFlight> completions)
    {
        Handle = JobSystem.Schedule(new CompileJob(this));
        JobSystem.OnCompleted(Handle, static (_, state) =>
        {
            CompletionState completion = (CompletionState)state!;
            completion.Flight.Enqueue(completion.Queue);
        }, new CompletionState(completions, this));
    }

    public void Enqueue(ConcurrentQueue<CompilationFlight> completions)
    {
        if (Interlocked.Exchange(ref _enqueued, 1) == 0) completions.Enqueue(this);
    }

    public void MarkConsumedByRequiredJoin() =>
        Interlocked.Exchange(ref _consumedByRequiredJoin, 1);

    private void Compile()
    {
        try
        {
            Result = _compiler(_graph, _device, optimized: true);
            if (Result is not null)
            {
                try
                {
                    CompiledGraphContract.Validate(_graph, Result, _device, optimized: true);
                }
                catch (Exception exception)
                {
                    ContractError = exception;
                }
            }
        }
        catch (Exception exception)
        {
            Error = exception;
        }
    }

    private readonly struct CompileJob : IJob
    {
        private readonly CompilationFlight _flight;
        public CompileJob(CompilationFlight flight) => _flight = flight;
        public void Execute() => _flight.Compile();
    }

    private sealed record CompletionState(ConcurrentQueue<CompilationFlight> Queue, CompilationFlight Flight);
}

internal delegate CompiledGraph GraphCompiler(
    FrozenGraph graph,
    DeviceCompilationSnapshot device,
    bool optimized);

internal sealed class ConservativePlanUnavailableException : Exception
{
    public ConservativePlanUnavailableException(string message) : base(message) { }
    public ConservativePlanUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}

internal enum CompilationEvent : byte
{
    CacheHit,
    CacheMiss,
    ConservativePlanCompiled,
    ConservativePlanSelected,
    OptimizedPlanSelected,
    FlightStarted,
    SingleFlightJoin,
    CandidatePublished,
    CandidateDropped,
    CandidateFailed,
    EntryEvicted,
    EntryRetired,
}

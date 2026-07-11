using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Xunit;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CompilationCacheTests
{
    [Fact]
    public void Second_equal_immediate_invocation_hits_but_still_records_and_submits()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });

        _ = ExecuteTransientWrite(device, graph, 64);
        _ = ExecuteTransientWrite(device, graph, 64);

        RenderGraphStatistics statistics = graph.Statistics;
        Assert.Equal(1, statistics.CacheMisses);
        Assert.Equal(1, statistics.CacheHits);
        Assert.Equal(1, statistics.ConservativeCompilations);
        Assert.Equal(2, statistics.CommandListsRecorded);
        Assert.Equal(2, statistics.Submissions);
    }

    [Fact]
    public void Zero_retention_preserves_immediate_outputs_without_exposing_a_lookup_entry()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompilationCacheEntryLimit = 0,
            CompilationCachePayloadByteBudget = 0,
            CompileOptimizedPlansAsynchronously = true,
        });
        byte[] first = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(value + 3))).ToArray();
        byte[] second = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(value * 7 + 5))).ToArray();

        ExecuteImportedCopy(device, graph, first);
        ExecuteImportedCopy(device, graph, second);

        RenderGraphStatistics statistics = graph.Statistics;
        Assert.Equal(2, statistics.CacheMisses);
        Assert.Equal(0, statistics.CacheHits);
        Assert.Equal(2, statistics.ConservativeCompilations);
        Assert.Equal(2, statistics.ConservativePlanSelections);
        Assert.Equal(0, statistics.OptimizedPlanSelections);
        Assert.Equal(0, statistics.OptimizedFlightsStarted);
        Assert.Equal(0, statistics.ResidentCacheEntries);
        Assert.Equal(0, statistics.ResidentCachePayloadBytes);
    }

    [Fact]
    public void Cache_hit_uses_current_imported_handles_and_not_the_first_invocations_payload()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });
        byte[] firstBytes = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(value + 1))).ToArray();
        byte[] secondBytes = Enumerable.Range(0, 64).Select(static value => unchecked((byte)(255 - value))).ToArray();

        ExecuteImportedCopy(device, graph, firstBytes);
        ExecuteImportedCopy(device, graph, secondBytes);

        Assert.Equal(1, graph.Statistics.CacheMisses);
        Assert.Equal(1, graph.Statistics.CacheHits);
    }

    [Fact]
    public void Invocation_failure_does_not_poison_the_retained_plan_or_capture_stale_state()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
        });

        _ = ExecuteMaybeFail(device, graph, fail: false);
        Assert.ThrowsAny<Exception>(() => ExecuteMaybeFail(device, graph, fail: true));
        _ = ExecuteMaybeFail(device, graph, fail: false);

        Assert.Equal(1, graph.Statistics.CacheMisses);
        Assert.Equal(2, graph.Statistics.CacheHits);
        Assert.Equal(1, graph.Statistics.ConservativeCompilations);
        Assert.Equal(2, graph.Statistics.Submissions);
    }

    [Fact]
    public void Exact_key_requires_canonical_bytes_semantic_generations_and_compiler_policy()
    {
        using NullDevice device = new();
        GraphSignature collision = new(1, 2, 3, 4);
        CompilationEnvironment environment = new(
            device.Domain,
            device.Compilation.SemanticGeneration,
            CompilationCache.CompilerSemanticGeneration);
        CompilationCacheKey first = new(collision, environment, [1, 2, 3]);
        CompilationCacheKey same = new(collision, environment, [1, 2, 3]);
        CompilationCacheKey differentBytes = new(collision, environment, [1, 2, 4]);
        CompilationCacheKey differentDeviceGeneration = new(
            collision,
            environment with { DeviceSemanticGeneration = checked(environment.DeviceSemanticGeneration + 1) },
            [1, 2, 3]);
        CompilationCacheKey differentCompilerGeneration = new(
            collision,
            environment with { CompilerSemanticGeneration = checked(environment.CompilerSemanticGeneration + 1) },
            [1, 2, 3]);
        CompilationCacheKey differentCompilerPolicy = new(
            collision,
            environment with { CompilerPolicy = checked(environment.CompilerPolicy + 1) },
            [1, 2, 3]);

        Assert.True(first.ExactEquals(same));
        Assert.False(first.ExactEquals(differentBytes));
        Assert.False(first.ExactEquals(differentDeviceGeneration));
        Assert.False(first.ExactEquals(differentCompilerGeneration));
        Assert.False(first.ExactEquals(differentCompilerPolicy));
    }

    [Fact]
    public void Signature_collision_is_rejected_by_the_real_resident_lookup()
    {
        using NullDevice device = new();
        FrozenGraph first = CreateFrozenWrite(device, 64);
        FrozenGraph structurallyDifferent = CreateFrozenWrite(device, 128);
        FrozenGraph colliding = new(
            structurallyDifferent.Token,
            structurallyDifferent.Resources,
            structurallyDifferent.BufferViews,
            structurallyDifferent.TextureViews,
            structurallyDifferent.Passes,
            new GraphCanonicalData(
                structurallyDifferent.Canonical.Bytes.ToArray(),
                first.Canonical.Signature));
        List<CompilationEvent> events = [];
        using CompilationCache cache = new(device, 8, 1024 * 1024, false, events.Add);

        AcquireAndRelease(cache, first, device.Compilation);
        AcquireAndRelease(cache, colliding, device.Compilation);
        AcquireAndRelease(cache, first, device.Compilation);

        Assert.Equal(2, events.Count(static value => value == CompilationEvent.CacheMiss));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(2, cache.ResidentEntryCount);
    }

    [Fact]
    public void Exact_repeat_reuses_one_retained_conservative_plan()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        List<CompilationEvent> events = [];
        using CompilationCache cache = new(device, 8, 1024 * 1024, false, events.Add);

        CompiledGraphLease first = cache.Acquire(frozen, device.Compilation);
        first.Release();
        CompiledGraphLease second = cache.Acquire(frozen, device.Compilation);
        second.Release();

        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheMiss));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.ConservativePlanCompiled));
        Assert.Equal(1, cache.ResidentEntryCount);
    }

    [Fact]
    public void Exact_requests_join_one_controlled_async_flight_and_publish_only_on_coordinator()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        using ManualResetEventSlim enteredOptimizedCompile = new();
        using ManualResetEventSlim allowOptimizedCompile = new();
        List<CompilationEvent> events = [];
        CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot compilation, bool optimized)
        {
            if (optimized)
            {
                enteredOptimizedCompile.Set();
                allowOptimizedCompile.Wait();
            }
            return Compiler.Compile(graph, compilation, optimized);
        }

        CompilationCache cache = new(
            device,
            8,
            1024 * 1024,
            true,
            events.Add,
            Compile,
            compilerPolicy: 1);
        try
        {
            CompiledGraphLease first = cache.Acquire(frozen, device.Compilation);
            first.Release();
            Assert.True(enteredOptimizedCompile.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, events.Count(static value => value == CompilationEvent.CandidatePublished));

            CompiledGraphLease joined = cache.Acquire(frozen, device.Compilation);
            joined.Release();
            Assert.Equal(1, events.Count(static value => value == CompilationEvent.FlightStarted));
            Assert.Equal(1, events.Count(static value => value == CompilationEvent.SingleFlightJoin));
            Assert.Equal(0, events.Count(static value => value == CompilationEvent.CandidatePublished));

            allowOptimizedCompile.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Drain();
                return events.Contains(CompilationEvent.CandidatePublished);
            }, TimeSpan.FromSeconds(5)));
        }
        finally
        {
            allowOptimizedCompile.Set();
            cache.Dispose();
        }
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CandidatePublished));
    }

    [Fact]
    public void Failed_async_candidate_keeps_the_exact_conservative_fallback_resident()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        List<CompilationEvent> events = [];
        List<RenderGraphCompilationDiagnostic> diagnostics = [];
        CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot compilation, bool optimized)
        {
            if (optimized) throw new InvalidOperationException("expected optimized compile failure");
            return Compiler.Compile(graph, compilation, optimized: false);
        }

        using CompilationCache cache = new(
            device,
            8,
            1024 * 1024,
            true,
            events.Add,
            Compile,
            diagnostics.Add,
            compilerPolicy: 1);
        AcquireAndRelease(cache, frozen, device.Compilation);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.Drain();
            return events.Contains(CompilationEvent.CandidateFailed);
        }, TimeSpan.FromSeconds(5)));

        AcquireAndRelease(cache, frozen, device.Compilation);
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheMiss));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.ConservativePlanCompiled));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.FlightStarted));
        Assert.Equal(0, events.Count(static value => value == CompilationEvent.CandidatePublished));
        Assert.Equal(1, cache.ResidentEntryCount);
        RenderGraphCompilationDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(CompilationFailureStage.OptimizedCompilation, diagnostic.Stage);
        Assert.Equal("expected optimized compile failure", diagnostic.Exception.Message);
        Assert.Contains("write", diagnostic.PassNames);
        Assert.Equal(64, diagnostic.CanonicalSignature.Length);
    }

    [Fact]
    public void Conservative_hard_constraint_joins_and_binds_the_exact_required_optimized_plan()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        List<CompilationEvent> events = [];
        CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot compilation, bool optimized)
        {
            if (!optimized)
                throw new ConservativePlanUnavailableException("conservative placement cannot satisfy the hard constraint");
            return Compiler.Compile(graph, compilation, optimized: true);
        }

        using CompilationCache cache = new(
            device,
            8,
            1024 * 1024,
            false,
            events.Add,
            Compile,
            compilerPolicy: 1);
        CompiledGraphLease required = cache.Acquire(frozen, device.Compilation);
        Assert.True(required.Graph.Optimized);
        required.Release();

        CompiledGraphLease hit = cache.Acquire(frozen, device.Compilation);
        Assert.True(hit.Graph.Optimized);
        hit.Release();

        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheMiss));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(0, events.Count(static value => value == CompilationEvent.ConservativePlanCompiled));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.FlightStarted));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CandidatePublished));
        Assert.Equal(2, events.Count(static value => value == CompilationEvent.OptimizedPlanSelected));
    }

    [Fact]
    public void Oversized_optimized_candidate_keeps_the_conservative_resident_without_retrying_each_hit()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        CompiledGraph baseline = Compiler.Compile(frozen, device.Compilation, optimized: false);
        long exactConservativeBudget = checked(frozen.Canonical.Bytes.Length + baseline.EstimatedRetainedBytes);
        List<CompilationEvent> events = [];
        CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot compilation, bool optimized)
        {
            CompiledGraph compiled = Compiler.Compile(graph, compilation, optimized);
            if (!optimized) return compiled;
            return new CompiledGraph(
                compiled.Queues,
                compiled.ActivePassOrdinals,
                compiled.RootPasses,
                compiled.RetainingPasses,
                compiled.LiveResources,
                compiled.LiveBufferViews,
                compiled.LiveTextureViews,
                compiled.ExecutionBatches,
                compiled.RecordUnits,
                compiled.PassToRecordUnit,
                compiled.Aliasing,
                compiled.Raster,
                compiled.Culling,
                compiled.Dependencies,
                compiled.BeforeBarriers,
                compiled.AfterBarriers,
                Enumerable.Repeat(
                    new CompiledHeap(1, MemoryType.DeviceLocal, ResourceHeapClass.Buffer, 0),
                    4096).ToArray(),
                compiled.Placements,
                compiled.Rendering,
                optimized: true);
        }

        using CompilationCache cache = new(
            device,
            8,
            exactConservativeBudget,
            true,
            events.Add,
            Compile,
            compilerPolicy: 1);
        AcquireAndRelease(cache, frozen, device.Compilation);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.Drain();
            return events.Contains(CompilationEvent.CandidateDropped);
        }, TimeSpan.FromSeconds(5)));
        AcquireAndRelease(cache, frozen, device.Compilation);

        Assert.Equal(1, events.Count(static value => value == CompilationEvent.FlightStarted));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CandidateDropped));
        Assert.Equal(0, events.Count(static value => value == CompilationEvent.CandidatePublished));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(1, cache.ResidentEntryCount);
    }

    [Fact]
    public void Entry_limit_uses_deterministic_coordinator_access_lru()
    {
        using NullDevice device = new();
        FrozenGraph firstGraph = CreateFrozenWrite(device, 64);
        FrozenGraph secondGraph = CreateFrozenWrite(device, 128);
        FrozenGraph thirdGraph = CreateFrozenWrite(device, 256);
        List<CompilationEvent> events = [];
        using CompilationCache cache = new(device, 2, long.MaxValue, false, events.Add);

        AcquireAndRelease(cache, firstGraph, device.Compilation);
        AcquireAndRelease(cache, secondGraph, device.Compilation);
        AcquireAndRelease(cache, firstGraph, device.Compilation); // first is now newer than second.
        AcquireAndRelease(cache, thirdGraph, device.Compilation); // evicts second.
        AcquireAndRelease(cache, secondGraph, device.Compilation); // exact miss proves the victim.

        Assert.Equal(4, events.Count(static value => value == CompilationEvent.CacheMiss));
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.CacheHit));
        Assert.Equal(2, events.Count(static value => value == CompilationEvent.EntryEvicted));
        Assert.Equal(2, cache.ResidentEntryCount);
    }

    [Fact]
    public void Individually_oversized_plan_is_never_visible_to_lookup_but_its_lease_remains_valid()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        List<CompilationEvent> events = [];
        using CompilationCache cache = new(device, 8, 1, false, events.Add);

        CompiledGraphLease lease = cache.Acquire(frozen, device.Compilation);
        Assert.Equal(0, cache.ResidentEntryCount);
        Assert.Equal(1, cache.RetiringEntryCount);
        Assert.DoesNotContain(CompilationEvent.EntryEvicted, events);
        lease.Release();

        Assert.Equal(0, cache.ResidentEntryCount);
        Assert.Equal(0, cache.RetiringEntryCount);
        Assert.DoesNotContain(CompilationEvent.EntryEvicted, events);
        Assert.Contains(CompilationEvent.EntryRetired, events);
        AcquireAndRelease(cache, frozen, device.Compilation);
        Assert.Equal(2, events.Count(static value => value == CompilationEvent.CacheMiss));
    }

    [Fact]
    public void Dispose_rejects_an_active_invocation_lease_and_remains_recoverable()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        CompilationCache cache = new(device, 8, 1024 * 1024, false, static _ => { });
        CompiledGraphLease lease = cache.Acquire(frozen, device.Compilation);

        Assert.Throws<InvalidOperationException>(cache.Dispose);
        lease.Release();
        cache.Dispose();
    }

    [Fact]
    public void Published_replacement_retires_an_inactive_managed_plan_without_waiting_for_gpu_completion()
    {
        using NullDevice device = new(new NullOptions { AutoCompleteSubmissions = false });
        FrozenGraph firstGraph = CreateFrozenWrite(device, 64);
        List<CompilationEvent> events = [];
        using CompilationCache cache = new(
            device,
            1,
            long.MaxValue,
            true,
            events.Add,
            compilerPolicy: 1);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics);
        CommandListHandle commandList = commands.Finish();
        GpuCompletion pending = device.Submit(QueueType.Graphics, [commandList]);

        CompiledGraphLease first = cache.Acquire(firstGraph, device.Compilation);
        first.Release();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.Drain();
            return events.Contains(CompilationEvent.CandidatePublished);
        }, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, cache.RetiringEntryCount);
        Assert.Equal(0, cache.RetiringPayloadBytes);
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.EntryRetired));

        // The unrelated native submission is deliberately still incomplete: cached plans own no
        // native resources and therefore have no GPU-fence retirement dependency.
        Assert.False(device.Wait(pending, TimeSpan.Zero));
        device.AdvanceCompletion(pending);

        AcquireAndRelease(cache, firstGraph, device.Compilation);
        cache.Drain();
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.FlightStarted));
    }

    [Fact]
    public void Published_replacement_keeps_the_old_plan_alive_until_its_active_lease_releases()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim allow = new();
        List<CompilationEvent> events = [];
        CompiledGraph Compile(FrozenGraph graph, DeviceCompilationSnapshot compilation, bool optimized)
        {
            if (optimized)
            {
                entered.Set();
                allow.Wait();
            }
            return Compiler.Compile(graph, compilation, optimized);
        }

        using CompilationCache cache = new(
            device,
            8,
            long.MaxValue,
            true,
            events.Add,
            Compile,
            compilerPolicy: 1);
        CompiledGraphLease oldLease = cache.Acquire(frozen, device.Compilation);
        Assert.False(oldLease.Graph.Optimized);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        allow.Set();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.Drain();
            return events.Contains(CompilationEvent.CandidatePublished);
        }, TimeSpan.FromSeconds(5)));

        Assert.Equal(1, cache.RetiringEntryCount);
        Assert.Equal(0, events.Count(static value => value == CompilationEvent.EntryRetired));
        CompiledGraphLease replacement = cache.Acquire(frozen, device.Compilation);
        Assert.True(replacement.Graph.Optimized);
        replacement.Release();
        Assert.Equal(1, cache.RetiringEntryCount);

        oldLease.Release();
        Assert.Equal(0, cache.RetiringEntryCount);
        Assert.Equal(1, events.Count(static value => value == CompilationEvent.EntryRetired));
    }

    [Fact]
    public void Graph_invocation_releases_its_managed_plan_after_submission_without_waiting_for_gpu_completion()
    {
        using NullDevice device = new(new NullOptions { AutoCompleteSubmissions = false });
        using RenderGraph graph = new(device, new RenderGraphOptions
        {
            CompilationCacheEntryLimit = 1,
            CompilationCachePayloadByteBudget = long.MaxValue,
            CompileOptimizedPlansAsynchronously = false,
        });

        GraphExecution first = ExecuteTransientWrite(device, graph, 64);
        _ = ExecuteTransientWrite(device, graph, 128);
        RenderGraphStatistics beforeCompletion = graph.Statistics;
        Assert.Equal(1, beforeCompletion.CacheEvictions);
        Assert.Equal(1, beforeCompletion.CacheRetirements);
        Assert.Equal(0, beforeCompletion.RetiringCacheEntries);
        Assert.Equal(1, beforeCompletion.ResidentCacheEntries);

        device.AdvanceCompletion(Assert.Single(first.Completions));
        GraphBuilder drain = graph.Begin();
        drain.Dispose();

        RenderGraphStatistics afterCompletion = graph.Statistics;
        Assert.Equal(beforeCompletion.CacheEvictions, afterCompletion.CacheEvictions);
        Assert.Equal(beforeCompletion.CacheRetirements, afterCompletion.CacheRetirements);
        Assert.Equal(0, afterCompletion.RetiringCacheEntries);
        Assert.Equal(1, afterCompletion.ResidentCacheEntries);
    }

    [Fact]
    public void Lookup_is_coordinator_thread_owned()
    {
        using NullDevice device = new();
        FrozenGraph frozen = CreateFrozenWrite(device, 64);
        using CompilationCache cache = new(device, 8, 1024 * 1024, false, static _ => { });

        Exception? workerFailure = null;
        Thread worker = new(() =>
        {
            try
            {
                _ = cache.Acquire(frozen, device.Compilation);
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        });
        worker.Start();
        worker.Join();
        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(workerFailure);
        Assert.Contains("coordinator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Public_statistics_are_coordinator_thread_owned()
    {
        using NullDevice device = new();
        using RenderGraph graph = new(device);
        Exception? workerFailure = null;
        Thread worker = new(() =>
        {
            try
            {
                _ = graph.Statistics;
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        });

        worker.Start();
        worker.Join();
        InvalidOperationException exception = Assert.IsType<InvalidOperationException>(workerFailure);
        Assert.Contains("coordinator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AcquireAndRelease(
        CompilationCache cache,
        FrozenGraph graph,
        DeviceCompilationSnapshot compilation)
    {
        CompiledGraphLease lease = cache.Acquire(graph, compilation);
        lease.Release();
    }

    private static FrozenGraph CreateFrozenWrite(IDevice device, ulong size)
    {
        GraphRecording recording = new();
        BufferId buffer = recording.AddBuffer(
            new BufferDesc(size, BufferUsage.CopyDestination),
            default);
        int pass = recording.AddPass("write", new QueueSelection(QueueType.Copy));
        _ = recording.AddBufferAccess(
            pass,
            buffer,
            ResourceEffect.Write,
            BufferUse.CopyDestination,
            BufferRange.Whole,
            PriorContents.Discard,
            WriteCoverage.Full);
        recording.SetExecution(pass, EmptyPass);
        return recording.Freeze(device);
    }

    private static GraphExecution ExecuteTransientWrite(NullDevice device, RenderGraph graph, ulong size)
    {
        BufferHandle output = device.CreateBuffer(new BufferDesc(size, BufferUsage.CopyDestination));
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId buffer = builder.CreateBuffer(new BufferDesc(size, BufferUsage.CopyDestination));
            BufferId observable = builder.ImportBuffer(
                output,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass("write", new QueueSelection(QueueType.Copy));
            _ = pass.Write(buffer, BufferUse.CopyDestination);
            _ = pass.Write(observable, BufferUse.CopyDestination);
            pass.Execute(EmptyPass);
            return graph.Execute(ref builder);
        }
        finally
        {
            device.DestroyBuffer(output);
        }
    }

    private static GraphExecution ExecuteMaybeFail(NullDevice device, RenderGraph graph, bool fail)
    {
        BufferHandle output = device.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId buffer = builder.CreateBuffer(new BufferDesc(64, BufferUsage.CopyDestination));
            BufferId observable = builder.ImportBuffer(
                output,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass("write", new QueueSelection(QueueType.Copy));
            _ = pass.Write(buffer, BufferUse.CopyDestination);
            _ = pass.Write(observable, BufferUse.CopyDestination);
            pass.Execute((ICommandContext _, in PassResources _) =>
            {
                if (fail) throw new InvalidOperationException("expected callback failure");
            });
            return graph.Execute(ref builder);
        }
        finally
        {
            device.DestroyBuffer(output);
        }
    }

    private static void ExecuteImportedCopy(NullDevice device, RenderGraph graph, byte[] expected)
    {
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopySource),
            MemoryType.Upload);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc((ulong)expected.Length, BufferUsage.CopyDestination),
            MemoryType.Readback);
        device.WriteBuffer(upload, 0, expected);
        try
        {
            GraphBuilder builder = graph.Begin();
            BufferId source = builder.ImportBuffer(upload, BufferUse.CopySource, BufferUse.CopySource);
            BufferId destination = builder.ImportBuffer(
                readback,
                BufferUse.CopyDestination,
                BufferUse.CopyDestination,
                contentsAvailable: false);
            PassBuilder pass = builder.AddPass("copy", new QueueSelection(QueueType.Copy));
            BufferAccess sourceAccess = pass.Read(source, BufferUse.CopySource);
            BufferAccess destinationAccess = pass.Write(destination, BufferUse.CopyDestination);
            pass.Execute((ICommandContext commands, in PassResources resources) =>
                commands.CopyBuffer(
                    resources.Get(sourceAccess),
                    0,
                    resources.Get(destinationAccess),
                    0,
                    (ulong)expected.Length));
            GraphExecution execution = graph.Execute(ref builder);
            Assert.True(execution.Wait(TimeSpan.Zero));

            byte[] actual = new byte[expected.Length];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(expected, actual);
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyBuffer(upload);
            device.CollectGarbage();
        }
    }

    private static void EmptyPass(ICommandContext commands, in PassResources resources) { }
}

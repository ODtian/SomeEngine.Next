namespace SomeEngine.Job.Tests;

public sealed class ResourceSafetyTests
{
    private readonly ResourceData _data = new();

    public ResourceSafetyTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 4 });
        JobSystem.SafetyMode = JobSafetyMode.Checked;
        _data.Reset();
    }

    [Fact]
    public void CollectedContainerBindingRetriesTokenReleaseAfterActiveOwnerCompletes()
    {
        (JobResourceAccess access, WeakReference container) =
            CreateEphemeralContainerAccess();
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();
        JobHandle owner = JobSystem.Schedule(
            new ResourceData.BlockingSignalJob(started, gate),
            access);
        Assert.True(started.Wait(1_000));

        // The first finalizer attempt observes the live owner and must re-register itself.
        ForceFullCollection();
        Assert.False(container.IsAlive);

        gate.Set();
        owner.Complete();
        ForceFullCollection();

        // A successful retry released the token identity; retaining only the old access must not
        // keep the ResourceState alive or make the stale capability usable.
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceData.NoOpJob(), access));
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (JobResourceAccess Access, WeakReference Container)
        CreateEphemeralContainerAccess()
    {
        var container = new int[1];
        var weak = new WeakReference(container);
        JobResourceAccess access = JobResourceAccess.Write(container);
        return (access, weak);
    }

    private void ForceFullCollection()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [Fact]
    public void ValidResourceSchedulesAndStaleHandleIsRejected()
    {
        var resource = JobSystem.CreateResource("valid-resource");

        JobSystem.Schedule(new ResourceData.NoOpJob(), JobResourceAccess.Read(resource)).Complete();
        JobSystem.ReleaseResource(resource);

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceData.NoOpJob(), JobResourceAccess.Read(resource)));
    }

    [Fact]
    public void DoubleReleaseIsExplicitlyRejected()
    {
        var resource = JobSystem.CreateResource("double-release");

        JobSystem.ReleaseResource(resource);

        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResource(resource));
    }

    [Fact]
    public void ResourcePoolPressureDoesNotLeakHandles()
    {
        var stale = JobSystem.CreateResource("stale");
        JobSystem.ReleaseResource(stale);

        for (var i = 0; i < 512; i++)
        {
            var resource = JobSystem.CreateResource($"pooled-{i}");
            JobSystem.Schedule(new ResourceData.NoOpJob(), JobResourceAccess.Write(resource)).Complete();
            JobSystem.ReleaseResource(resource);
        }

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceData.NoOpJob(), JobResourceAccess.Read(stale)));
    }

    [Fact]
    public void AccessDeclarationsSupportReadWriteExclusiveAndDirectSchedule()
    {
        var resource = JobSystem.CreateResource("declared");

        JobSystem.Schedule(new ResourceData.CountJob(_data)).Complete();
        JobSystem.Schedule(new ResourceData.CountJob(_data), JobResourceAccess.Read(resource)).Complete();
        JobSystem.Schedule(new ResourceData.CountJob(_data), JobResourceAccess.Write(resource)).Complete();
        JobSystem.Schedule(new ResourceData.CountJob(_data), JobResourceAccess.Exclusive(resource)).Complete();

        Assert.Equal(4, _data.Counter);
    }

    [Fact]
    public void ExplicitDependencyAndAccessDependencyBothGateJob()
    {
        var resource = JobSystem.CreateResource("composed");
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var manualStarted = _data.Events.CreateSignal();
        using var manualGate = _data.Events.CreateSignal();
        using var dependentStarted = _data.Events.CreateSignal();
        using var dependentGate = _data.Events.CreateSignal();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var manual = JobSystem.Schedule(new ResourceData.BlockingRecordJob(_data, 2, manualStarted, manualGate));
        var dependent = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, dependentStarted, dependentGate),
            new[] { JobResourceAccess.Read(resource) },
            manual);

        Assert.True(writerStarted.Wait(1_000));
        Assert.True(manualStarted.Wait(1_000));
        AssertNotRecorded(3);

        manualGate.Set();
        manual.Complete();

        writerGate.Set();
        Assert.True(dependentStarted.Wait(1_000));
        dependentGate.Set();
        dependent.Complete();
        writer.Complete();

        AssertRecorded(3);
        _data.Events.AssertBefore(writerGate, dependentStarted);
        _data.Events.AssertBefore(manualGate, dependentStarted);
    }

    [Fact]
    public void ReadReadAccessesCanRunTogether()
    {
        var resource = JobSystem.CreateResource("read-shared");
        using var firstStarted = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, gate),
            JobResourceAccess.Read(resource));
        var second = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, secondStarted, gate),
            JobResourceAccess.Read(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.True(secondStarted.Wait(1_000));

        gate.Set();
        JobSystem.CombineDependencies(new[] { first, second }).Complete();
    }

    [Fact]
    public void WriteReadOrdersReaderAfterWriter()
    {
        var resource = JobSystem.CreateResource("write-read");
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));

        writerGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        readerGate.Set();
        reader.Complete();
        writer.Complete();
        _data.Events.AssertBefore(writerGate, readerStarted);
    }

    [Fact]
    public void ReadWriteOrdersWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("read-write");
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(readerStarted.Wait(1_000));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();
        writer.Complete();
        reader.Complete();
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void WriteWriteOrdersSecondWriterAfterFirstWriter()
    {
        var resource = JobSystem.CreateResource("write-write");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var secondGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var second = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, secondStarted, secondGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(secondStarted.Wait(1_000));
        secondGate.Set();
        second.Complete();
        first.Complete();
        _data.Events.AssertBefore(firstGate, secondStarted);
    }

    [Fact]
    public void WritePendingReadThenWriteOrdersSecondWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("write-read-write");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var secondWriterStarted = _data.Events.CreateSignal();
        using var secondWriterGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var secondWriter = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, secondWriterStarted, secondWriterGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));

        readerGate.Set();
        Assert.True(secondWriterStarted.Wait(1_000));
        secondWriterGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, secondWriter }).Complete();
        _data.Events.AssertBefore(firstGate, readerStarted);
        _data.Events.AssertBefore(readerGate, secondWriterStarted);
    }

    [Fact]
    public void ReleasedWriterRebuildsFrontierSoLaterWriterWaitsForActiveReader()
    {
        var resource = JobSystem.CreateResource("release-rebuild-read-frontier");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        first.Complete();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
        _data.Events.AssertBefore(firstGate, readerStarted);
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void FailedPartialRegistrationRestoresClearedReaderFrontier()
    {
        var resource = JobSystem.CreateResource("rollback-reader-frontier");
        var stale = JobSystem.CreateResource("rollback-stale");
        JobSystem.ReleaseResource(stale);
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(readerStarted.Wait(1_000));

        JobResourceAccess[] accesses =
        [
            JobResourceAccess.Write(resource),
            JobResourceAccess.Read(stale)
        ];

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceData.NoOpJob(), accesses));

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void UnrangedFrontierResumesAfterRangedAccessCompletes()
    {
        var resource = JobSystem.CreateResource("range-frontier-reentry");
        using var rangedStarted = _data.Events.CreateSignal();
        using var rangedGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var ranged = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, rangedStarted, rangedGate),
            JobResourceAccess.Write(resource, 0, 16));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(rangedStarted.Wait(1_000));

        rangedGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        ranged.Complete();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
        _data.Events.AssertBefore(rangedGate, readerStarted);
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void WriteThenMultiplePendingReadersThenWriteWaitsForAllReaders()
    {
        var resource = JobSystem.CreateResource("write-read-read-write");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var firstReaderStarted = _data.Events.CreateSignal();
        using var firstReaderGate = _data.Events.CreateSignal();
        using var secondReaderStarted = _data.Events.CreateSignal();
        using var secondReaderGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var firstReader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, firstReaderStarted, firstReaderGate),
            JobResourceAccess.Read(resource));
        var secondReader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, secondReaderStarted, secondReaderGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 4, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(firstReaderStarted.Wait(1_000));
        Assert.True(secondReaderStarted.Wait(1_000));

        firstReaderGate.Set();

        secondReaderGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { first, firstReader, secondReader, writer }).Complete();
        _data.Events.AssertBefore(firstGate, firstReaderStarted);
        _data.Events.AssertBefore(firstGate, secondReaderStarted);
        _data.Events.AssertBefore(firstReaderGate, writerStarted);
        _data.Events.AssertBefore(secondReaderGate, writerStarted);
    }

    [Fact]
    public void WritePendingReadThenExclusiveOrdersExclusiveAfterReader()
    {
        var resource = JobSystem.CreateResource("write-read-exclusive");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var exclusiveStarted = _data.Events.CreateSignal();
        using var exclusiveGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var exclusive = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, exclusiveStarted, exclusiveGate),
            JobResourceAccess.Exclusive(resource));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));

        readerGate.Set();
        Assert.True(exclusiveStarted.Wait(1_000));
        exclusiveGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, exclusive }).Complete();
        _data.Events.AssertBefore(firstGate, readerStarted);
        _data.Events.AssertBefore(readerGate, exclusiveStarted);
    }

    [Fact]
    public void TokenWritePendingReadThenWriteOrdersSecondWriterAfterReader()
    {
        var token = JobSystem.CreateResourceToken("token-write-read-write");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var secondWriterStarted = _data.Events.CreateSignal();
        using var secondWriterGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(token));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(token));
        var secondWriter = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, secondWriterStarted, secondWriterGate),
            JobResourceAccess.Write(token));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));

        readerGate.Set();
        Assert.True(secondWriterStarted.Wait(1_000));
        secondWriterGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, secondWriter }).Complete();
        _data.Events.AssertBefore(firstGate, readerStarted);
        _data.Events.AssertBefore(readerGate, secondWriterStarted);
    }

    [Fact]
    public void ConcurrentWriteSchedulingSameResourceProducesDeterministicResult()
    {
        var resource = JobSystem.CreateResource("concurrent-writes");
        var handles = new List<JobHandle>();
        var handlesLock = new Lock();
        var threads = new Thread[4];

        for (var i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                for (var j = 0; j < 25; j++)
                {
                    var handle = JobSystem.Schedule(
                        new ResourceData.NonAtomicIncrementJob(_data),
                        JobResourceAccess.Write(resource));
                    lock (handlesLock)
                    {
                        handles.Add(handle);
                    }
                }
            });
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        JobSystem.CombineDependencies(handles.ToArray()).Complete();

        Assert.Equal(100, _data.UnsafeCounter);
    }

    [Fact]
    public void ManagedTokenSerializesSameTokenAndDifferentTokensRunIndependently()
    {
        var firstToken = JobSystem.CreateResourceToken("managed-service-a");
        var secondToken = JobSystem.CreateResourceToken("managed-service-b");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var sameTokenStarted = _data.Events.CreateSignal();
        using var sameTokenGate = _data.Events.CreateSignal();
        using var otherTokenStarted = _data.Events.CreateSignal();
        using var otherTokenGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(firstToken));
        var sameToken = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, sameTokenStarted, sameTokenGate),
            JobResourceAccess.Write(firstToken));
        var otherToken = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 3, otherTokenStarted, otherTokenGate),
            JobResourceAccess.Write(secondToken));

        Assert.True(firstStarted.Wait(1_000));
        Assert.True(otherTokenStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(sameTokenStarted.Wait(1_000));
        sameTokenGate.Set();
        otherTokenGate.Set();
        JobSystem.CombineDependencies(new[] { first, sameToken, otherToken }).Complete();
        _data.Events.AssertBefore(firstGate, sameTokenStarted);
    }

    [Fact]
    public void StaleTokenIsRejectedAndTokenComposesWithResourceAccess()
    {
        var staleToken = JobSystem.CreateResourceToken("stale-token");
        JobSystem.ReleaseResourceToken(staleToken);

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceData.NoOpJob(), JobResourceAccess.Read(staleToken)));

        var token = JobSystem.CreateResourceToken("live-token");
        var resource = JobSystem.CreateResource("live-resource");
        JobResourceAccess[] accesses =
        [
            JobResourceAccess.Write(token),
            JobResourceAccess.Read(resource)
        ];

        JobSystem.Schedule(new ResourceData.RefContainingRecordJob(_data.ManagedLog, "ran"), accesses).Complete();

        Assert.Equal(["ran"], _data.ManagedLog);
    }

    [Fact]
    public void ScopeOwnedResourceLivesThroughChildAndReleasesAfterParentCompletion()
    {
        JobSystem.Schedule(new ResourceData.ParentCreatesScopeResourceAndChildUsesIt(_data)).Complete();

        Assert.True(_data.ChildUsedScopeResource);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.NoOpJob(),
                JobResourceAccess.Read(_data.LastScopeResource)));
    }

    [Fact]
    public void ScopeOwnedResourceLivesThroughGrandchild()
    {
        JobSystem.Schedule(new ResourceData.ParentCreatesScopeResourceAndGrandchildUsesIt(_data)).Complete();

        Assert.True(_data.GrandchildUsedScopeResource);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.NoOpJob(),
                JobResourceAccess.Read(_data.LastScopeResource)));
    }

    [Fact]
    public void ScopeOwnedResourceReleasesOnExceptionPath()
    {
        var handle = JobSystem.Schedule(new ResourceData.ParentCreatesScopeResourceThenThrows(_data));

        Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.NoOpJob(),
                JobResourceAccess.Read(_data.LastScopeResource)));
    }

    [Fact]
    public void CheckedModeRejectsReleaseWhileResourceIsInUse()
    {
        var resource = JobSystem.CreateResource("in-use");
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var handle = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, started, gate),
            JobResourceAccess.Write(resource));

        Assert.True(started.Wait(1_000));
        Assert.Throws<JobResourceSafetyException>(() => JobSystem.ReleaseResource(resource));

        gate.Set();
        handle.Complete();
    }

    [Fact]
    public void StrictModeReportsJobAndResourceIdentity()
    {
        JobSystem.SafetyMode = JobSafetyMode.Strict;
        var resource = JobSystem.CreateResource("strict-resource");
        JobSystem.ReleaseResource(resource);

        var ex = Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.StrictDiagnosticJob(),
                JobResourceAccess.Read(resource)));

        Assert.Equal(JobSafetyMode.Strict, ex.SafetyMode);
        Assert.EndsWith(nameof(ResourceData.StrictDiagnosticJob), ex.JobTypeName);
        Assert.Equal("strict-resource", ex.ResourceName);
    }

    [Fact]
    public void StaleHandleAfterResourceIdReuseDoesNotReportNewResourceName()
    {
        JobSystem.SafetyMode = JobSafetyMode.Strict;
        var stale = JobSystem.CreateResource("old-resource");
        JobSystem.ReleaseResource(stale);
        var reused = JobSystem.CreateResource("new-resource");

        var ex = Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.StrictDiagnosticJob(),
                JobResourceAccess.Read(stale)));

        Assert.Null(ex.ResourceName);
    }

    [Fact]
    public void StaleHandleAfterReleasedReuseDoesNotReportLaterResourceName()
    {
        JobSystem.SafetyMode = JobSafetyMode.Strict;
        var stale = JobSystem.CreateResource("old-resource");
        JobSystem.ReleaseResource(stale);
        var reused = JobSystem.CreateResource("new-resource");
        JobSystem.ReleaseResource(reused);

        var ex = Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceData.StrictDiagnosticJob(),
                JobResourceAccess.Read(stale)));

        Assert.Null(ex.ResourceName);
    }

    [Fact]
    public void FastModePermitsStaleUncheckedAccess()
    {
        JobSystem.SafetyMode = JobSafetyMode.Fast;
        var resource = JobSystem.CreateResource("fast-stale");
        JobSystem.ReleaseResource(resource);

        JobSystem.Schedule(new ResourceData.CountJob(_data), JobResourceAccess.Read(resource)).Complete();

        Assert.Equal(1, _data.Counter);
    }

    [Fact]
    public void NonOverlappingWriteRangesRunTogether()
    {
        var resource = JobSystem.CreateResource("ranges");
        using var firstStarted = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, gate),
            JobResourceAccess.Write(resource, 0, 16));
        var second = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, secondStarted, gate),
            JobResourceAccess.Write(resource, 16, 16));

        Assert.True(firstStarted.Wait(1_000));
        Assert.True(secondStarted.Wait(1_000));

        gate.Set();
        JobSystem.CombineDependencies(new[] { first, second }).Complete();
    }

    [Fact]
    public void OverlappingWriteRangesAreOrdered()
    {
        var resource = JobSystem.CreateResource("overlap");
        using var firstStarted = _data.Events.CreateSignal();
        using var firstGate = _data.Events.CreateSignal();
        using var secondStarted = _data.Events.CreateSignal();
        using var secondGate = _data.Events.CreateSignal();

        var first = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, firstStarted, firstGate),
            JobResourceAccess.Write(resource, 0, 16));
        var second = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, secondStarted, secondGate),
            JobResourceAccess.Write(resource, 8, 16));

        Assert.True(firstStarted.Wait(1_000));

        firstGate.Set();
        Assert.True(secondStarted.Wait(1_000));
        secondGate.Set();
        second.Complete();
        first.Complete();
        _data.Events.AssertBefore(firstGate, secondStarted);
    }

    [Fact]
    public void ReadRangeAfterWriteRangeIsOrdered()
    {
        var resource = JobSystem.CreateResource("range-read");
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, writerStarted, writerGate),
            JobResourceAccess.Write(resource, 5, 10));
        var reader = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 2, readerStarted, readerGate),
            JobResourceAccess.Read(resource, 7, 2));

        Assert.True(writerStarted.Wait(1_000));

        writerGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        readerGate.Set();
        reader.Complete();
        writer.Complete();
        _data.Events.AssertBefore(writerGate, readerStarted);
    }

    [Fact]
    public void RangeEndOverflowIsRejected()
    {
        var resource = JobSystem.CreateResource("range-overflow");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JobResourceAccess.Write(resource, long.MaxValue - 4, 8));
    }

    [Fact]
    public void ParallelJobAccessesResourceAndWaitsForPriorWriter()
    {
        var resource = JobSystem.CreateResource("parallel-resource");
        var values = new int[32];
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var parallelStarted = _data.Events.CreateSignal();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var parallel = JobSystem.ScheduleParallel(
            new ResourceData.MarkIndexJob(values, parallelStarted),
            values.Length,
            4,
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));

        Assert.All(values, value => Assert.Equal(0, value));

        writerGate.Set();
        parallel.Complete();
        writer.Complete();

        Assert.All(values, value => Assert.Equal(1, value));
        _data.Events.AssertBefore(writerGate, parallelStarted);
    }

    [Fact]
    public void ChildResourceAccessDelaysParentCompletion()
    {
        var resource = JobSystem.CreateResource("child-resource");
        using var started = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var parent = JobSystem.Schedule(new ResourceData.ParentSchedulesBlockingChild(_data, resource, started, gate));

        Assert.True(started.Wait(1_000));
        Assert.False(parent.IsCompleted);

        gate.Set();
        parent.Complete();

        Assert.True(_data.ChildUsedScopeResource);
    }

    [Fact]
    public void RangeAccessFromChildScopeIsTracked()
    {
        var resource = JobSystem.CreateResource("child-range-resource");
        using var childStarted = _data.Events.CreateSignal();
        using var childGate = _data.Events.CreateSignal();

        var parent = JobSystem.Schedule(
            new ResourceData.ParentSchedulesBlockingRangeChild(_data, resource, childStarted, childGate));

        Assert.True(childStarted.Wait(1_000));

        var overlappingWriter = JobSystem.Schedule(
            new ResourceData.RecordJob(_data, 42),
            JobResourceAccess.Write(resource, 8, 4));

        AssertNotRecorded(42);
        childGate.Set();
        overlappingWriter.Complete();
        parent.Complete();

        AssertRecorded(42);
    }

    [Fact]
    public void ChildAccessToParentDeclaredResourceWaitsForParentBody()
    {
        var resource = JobSystem.CreateResource("parent-body-resource");

        JobSystem.Schedule(
            new ResourceData.ParentWithAccessSchedulesChildOnSameResource(_data, resource),
            JobResourceAccess.Write(resource)).Complete();

        Assert.True(_data.ChildUsedScopeResource);
    }

    [Fact]
    public void ChildReadThenWriteOnParentDeclaredResourceOrdersWriterAfterChildReader()
    {
        var resource = JobSystem.CreateResource("parent-read-write-resource");
        using var childrenScheduled = _data.Events.CreateSignal();
        using var readerStarted = _data.Events.CreateSignal();
        using var readerGate = _data.Events.CreateSignal();
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();

        var parent = JobSystem.Schedule(
            new ResourceData.ParentWithAccessSchedulesReadThenWriteChildren(
                _data, resource,
                childrenScheduled,
                readerStarted,
                readerGate,
                writerStarted,
                writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(childrenScheduled.Wait(1_000));
        Assert.True(readerStarted.Wait(1_000));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();
        parent.Complete();
        _data.Events.AssertBefore(readerGate, writerStarted);
    }

    [Fact]
    public void ExternalSuccessorWaitsForParentBodyInsteadOfAttachedChildCompletion()
    {
        var parentResource = JobSystem.CreateResource("parent-work-release");
        var childResource = JobSystem.CreateResource("attached-child-work-release");
        using var parentStarted = _data.Events.CreateSignal();
        using var allowChildSchedule = _data.Events.CreateSignal();
        using var childScheduled = _data.Events.CreateSignal();
        using var parentBodyReturning = _data.Events.CreateSignal();
        using var childStarted = _data.Events.CreateSignal();
        using var successorStarted = _data.Events.CreateSignal();
        using var successorGate = _data.Events.CreateSignal();

        var parent = JobSystem.Schedule(
            new ResourceData.ParentSchedulesChildAfterGate(
                childResource,
                parentStarted,
                allowChildSchedule,
                childScheduled,
                parentBodyReturning,
                childStarted),
            JobResourceAccess.Write(parentResource));

        Assert.True(parentStarted.Wait(1_000));

        JobResourceAccess[] successorAccesses =
        [
            JobResourceAccess.Write(parentResource),
            JobResourceAccess.Write(childResource)
        ];
        var successor = JobSystem.Schedule(
            new ResourceData.BlockingSignalJob(successorStarted, successorGate),
            successorAccesses);

        try
        {
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(parentBodyReturning.Wait(1_000));

            // The successor owns childResource before the parent creates its attached child.
            // The child therefore waits for the successor, while the successor waits only for
            // the parent's resource-owning work body -- not for the parent's attached child.
            Assert.True(successorStarted.Wait(1_000));

            Assert.False(parent.IsCompleted);
        }
        finally
        {
            allowChildSchedule.Set();
            successorGate.Set();
        }

        successor.Complete();
        parent.Complete();
        Assert.True(childStarted.IsSet);
        _data.Events.AssertBefore(parentBodyReturning, successorStarted);
        _data.Events.AssertBefore(successorGate, childStarted);
    }

    [Fact]
    public void ExplicitResourceSuccessorReservesThenActivatesAfterLaterAttachedChild()
    {
        var parentResource = JobSystem.CreateResource("deferred-parent-resource");
        var childResource = JobSystem.CreateResource("deferred-child-resource");
        using var parentStarted = _data.Events.CreateSignal();
        using var allowChildSchedule = _data.Events.CreateSignal();
        using var childScheduled = _data.Events.CreateSignal();
        using var childStarted = _data.Events.CreateSignal();
        using var childGate = _data.Events.CreateSignal();
        using var successorStarted = _data.Events.CreateSignal();

        var parent = JobSystem.Schedule(
            new ResourceData.ParentSchedulesConflictingChildAfterGate(
                childResource,
                parentStarted,
                allowChildSchedule,
                childScheduled,
                childStarted,
                childGate),
            JobResourceAccess.Write(parentResource));
        Assert.True(parentStarted.Wait(1_000));

        JobResourceAccess[] successorAccesses =
        [
            JobResourceAccess.Write(parentResource),
            JobResourceAccess.Write(childResource)
        ];
        var successor = JobSystem.Schedule(
            new ResourceData.SignalJob(successorStarted),
            successorAccesses,
            parent);

        // A deferred access is absent from the conflict frontier, but it still retains the
        // resource identity for the complete lifetime of the pending schedule.
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.ReleaseResource(childResource));

        try
        {
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(childStarted.Wait(1_000));

            Assert.False(parent.IsCompleted);

            childGate.Set();
            Assert.True(successorStarted.Wait(1_000));
            successor.Complete();
            parent.Complete();
        }
        finally
        {
            allowChildSchedule.Set();
            childGate.Set();
        }

        JobSystem.ReleaseResource(parentResource);
        JobSystem.ReleaseResource(childResource);
        _data.Events.AssertBefore(childGate, successorStarted);
    }

    [Fact]
    public void ParallelExplicitResourceSuccessorActivatesAfterLaterAttachedChild()
    {
        var parentResource = JobSystem.CreateResource("parallel-deferred-parent");
        var childResource = JobSystem.CreateResource("parallel-deferred-child");
        using var parentStarted = _data.Events.CreateSignal();
        using var allowChildSchedule = _data.Events.CreateSignal();
        using var childScheduled = _data.Events.CreateSignal();
        using var childStarted = _data.Events.CreateSignal();
        using var childGate = _data.Events.CreateSignal();
        using var successorStarted = _data.Events.CreateSignal();
        var values = new int[8];

        var parent = JobSystem.Schedule(
            new ResourceData.ParentSchedulesConflictingChildAfterGate(
                childResource,
                parentStarted,
                allowChildSchedule,
                childScheduled,
                childStarted,
                childGate),
            JobResourceAccess.Write(parentResource));
        Assert.True(parentStarted.Wait(1_000));

        JobResourceAccess[] successorAccesses =
        [
            JobResourceAccess.Write(parentResource),
            JobResourceAccess.Write(childResource)
        ];
        var successor = JobSystem.ScheduleParallel(
            new ResourceData.MarkIndexJob(values, successorStarted),
            values.Length,
            batchSize: 1,
            successorAccesses,
            parent);

        try
        {
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(childStarted.Wait(1_000));

            childGate.Set();
            Assert.True(successorStarted.Wait(1_000));
            successor.Complete();
            parent.Complete();
            Assert.All(values, static value => Assert.Equal(1, value));
        }
        finally
        {
            allowChildSchedule.Set();
            childGate.Set();
        }

        JobSystem.ReleaseResource(parentResource);
        JobSystem.ReleaseResource(childResource);
        _data.Events.AssertBefore(childGate, successorStarted);
    }

    [Fact]
    public void FaultedExplicitDependencyCancelsDeferredResourceReservation()
    {
        var resource = JobSystem.CreateResource("faulted-deferred-reservation");
        using var dependencyStarted = _data.Events.CreateSignal();
        using var dependencyGate = _data.Events.CreateSignal();
        using var successorStarted = _data.Events.CreateSignal();

        var dependency = JobSystem.Schedule(
            new ResourceData.BlockingThrowJob(dependencyStarted, dependencyGate));
        Assert.True(dependencyStarted.Wait(1_000));

        var successor = JobSystem.Schedule(
            new ResourceData.SignalJob(successorStarted),
            JobResourceAccess.Write(resource),
            dependency);

        // The access has not entered the conflict frontier, but the pending schedule must
        // retain its identity until the explicit dependency either succeeds or faults.
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.ReleaseResource(resource));

        try
        {
            dependencyGate.Set();
            var successorFault = Assert.Throws<InvalidOperationException>(() => successor.Complete());

            Assert.False(successorStarted.IsSet);

            // Fault propagation cancels the reservation rather than leaking a permanent use.
            JobSystem.ReleaseResource(resource);
            var dependencyFault = Assert.Throws<InvalidOperationException>(() => dependency.Complete());

        }
        finally
        {
            dependencyGate.Set();
        }
    }

    [Fact]
    public void FaultedResourceHazardOrdersButDoesNotCancelSuccessor()
    {
        JobResource resource = JobSystem.CreateResource("faulted-resource-hazard");
        using var predecessorStarted = _data.Events.CreateSignal();
        using var predecessorGate = _data.Events.CreateSignal();
        using var successorStarted = _data.Events.CreateSignal();
        JobHandle predecessor = JobSystem.Schedule(
            new ResourceData.BlockingThrowJob(predecessorStarted, predecessorGate),
            JobResourceAccess.Write(resource));
        Assert.True(predecessorStarted.Wait(TimeSpan.FromSeconds(5)));
        JobHandle successor = JobSystem.Schedule(
            new ResourceData.SignalJob(successorStarted),
            JobResourceAccess.Write(resource));

        try
        {
            predecessorGate.Set();

            successor.Complete();
            Assert.True(successorStarted.IsSet);
            InvalidOperationException predecessorFault =
                Assert.Throws<InvalidOperationException>(() => predecessor.Complete());

        }
        finally
        {
            predecessorGate.Set();
        }

        JobSystem.ReleaseResource(resource);
        _data.Events.AssertBefore(predecessorGate, successorStarted);
    }

    [Fact]
    public void ManagedJobsShareResourceDependencies()
    {
        var resource = JobSystem.CreateResource("mixed-lanes");
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var managedStarted = _data.Events.CreateSignal();

        var writer = JobSystem.Schedule(
            new ResourceData.BlockingRecordJob(_data, 1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var managed = JobSystem.Schedule(
            new ResourceData.RefContainingRecordJob(_data.ManagedLog, "managed", managedStarted),
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));

        Assert.Empty(_data.ManagedLog);

        writerGate.Set();
        managed.Complete();
        writer.Complete();

        Assert.Equal(["managed"], _data.ManagedLog);
        _data.Events.AssertBefore(writerGate, managedStarted);
    }

    [Fact]
    public void TypedListAccessesParticipateInResourceOrdering()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        _data.Reset();
        var values = new List<int> { 0 };

        var writer = JobSystem.Schedule(
            new ResourceData.WriteListValue(values, 42),
            JobResourceAccess.Write(values));
        var reader = JobSystem.Schedule(
            new ResourceData.ReadListValue(_data, values),
            JobResourceAccess.Read(values));

        reader.Complete();
        writer.Complete();

        Assert.Equal(42, _data.ContainerObservedValue);
    }

    [Fact]
    public void TypedContainerAccessesSupportStackDictionaryQueueAndArrayRanges()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
        var stack = new Stack<int>();
        var dictionary = new Dictionary<string, int> { ["value"] = 0 };
        var queue = new Queue<int>();
        var array = new int[2];
        using var firstStarted = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var stackWriter = JobSystem.Schedule(
            new ResourceData.PushStackValue(stack, 7),
            JobResourceAccess.Write(stack));
        var stackReader = JobSystem.Schedule(
            new ResourceData.ReadStackValue(_data, stack),
            JobResourceAccess.Read(stack));
        stackReader.Complete();
        stackWriter.Complete();

        var dictionaryWriter = JobSystem.Schedule(
            new ResourceData.WriteDictionaryValue(dictionary, "value", 17),
            JobResourceAccess.Write(dictionary));
        var dictionaryReader = JobSystem.Schedule(
            new ResourceData.ReadDictionaryValue(_data, dictionary, "value"),
            JobResourceAccess.Read(dictionary));
        dictionaryReader.Complete();
        dictionaryWriter.Complete();

        var queueWriter = JobSystem.Schedule(
            new ResourceData.EnqueueQueueValue(queue, 23),
            JobResourceAccess.Write(queue));
        var queueReader = JobSystem.Schedule(
            new ResourceData.ReadQueueValue(_data, queue),
            JobResourceAccess.Read(queue));
        queueReader.Complete();
        queueWriter.Complete();

        var firstArrayWriter = JobSystem.Schedule(
            new ResourceData.BlockingWriteArrayValue(array, index: 0, value: 11, firstStarted, gate),
            JobResourceAccess.Write(array, start: 0, length: 1));
        var secondArrayWriter = JobSystem.Schedule(
            new ResourceData.WriteArrayValue(array, index: 1, value: 13),
            JobResourceAccess.Write(array, start: 1, length: 1));

        Assert.True(firstStarted.Wait(1_000));
        secondArrayWriter.Complete();
        Assert.Equal(13, array[1]);

        gate.Set();
        firstArrayWriter.Complete();

        Assert.Equal(23, _data.ContainerObservedValue);
        Assert.Equal(17, dictionary["value"]);
        Assert.Equal(11, array[0]);
    }

    [Fact]
    public void MemoryAccessesUseArrayBackedSliceRanges()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
        var array = new int[3];
        using var firstStarted = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        var firstWriter = JobSystem.Schedule(
            new ResourceData.BlockingWriteArrayValue(array, index: 0, value: 31, firstStarted, gate),
            JobResourceAccess.Write(array.AsMemory(0, 1)));
        var secondWriter = JobSystem.Schedule(
            new ResourceData.WriteArrayValue(array, index: 1, value: 37),
            JobResourceAccess.Write(array.AsMemory(1, 1)));

        Assert.True(firstStarted.Wait(1_000));
        secondWriter.Complete();
        Assert.Equal(37, array[1]);

        var readOnlyReader = JobSystem.Schedule(
            new ResourceData.ReadArrayValue(_data, array, index: 0),
            JobResourceAccess.Read(new ReadOnlyMemory<int>(array, 0, 1)));

        Assert.False(readOnlyReader.IsCompleted);

        gate.Set();
        readOnlyReader.Complete();
        firstWriter.Complete();

        Assert.Equal(31, _data.ContainerObservedValue);
    }

    [Fact]
    public void SpanAccessesUseExplicitArrayOwner()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
        var array = new int[2];
        var other = new int[1];
        using var firstStarted = _data.Events.CreateSignal();
        using var gate = _data.Events.CreateSignal();

        Assert.Throws<ArgumentException>(() => JobResourceAccess.Read(array, other.AsSpan()));

        var firstWriter = JobSystem.Schedule(
            new ResourceData.BlockingWriteArrayValue(array, index: 0, value: 43, firstStarted, gate),
            JobResourceAccess.Write(array, array.AsSpan(0, 1)));
        var secondWriter = JobSystem.Schedule(
            new ResourceData.WriteArrayValue(array, index: 1, value: 47),
            JobResourceAccess.Write(array, array.AsSpan(1, 1)));

        Assert.True(firstStarted.Wait(1_000));
        secondWriter.Complete();
        Assert.Equal(47, array[1]);

        var reader = JobSystem.Schedule(
            new ResourceData.ReadArrayValue(_data, array, index: 0),
            JobResourceAccess.Read(array, (ReadOnlySpan<int>)array.AsSpan(0, 1)));

        Assert.False(reader.IsCompleted);

        gate.Set();
        reader.Complete();
        firstWriter.Complete();

        Assert.Equal(43, _data.ContainerObservedValue);
    }

    [Fact]
    public void CustomStructContainerProviderUsesTypedStaticAccess()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        _data.Reset();
        var container = new ResourceData.CustomCounterContainer(
            [0],
            JobSystem.CreateResourceToken("custom-struct-container"));

        try
        {
            var writer = JobSystem.Schedule(
                new ResourceData.WriteCustomCounter(container, 41),
                JobResourceAccess.Write<ResourceData.CustomCounterContainer, ResourceData.CustomCounterProvider>(ref container));
            var reader = JobSystem.Schedule(
                new ResourceData.ReadCustomCounter(_data, container),
                JobResourceAccess.Read<ResourceData.CustomCounterContainer, ResourceData.CustomCounterProvider>(ref container));

            reader.Complete();
            writer.Complete();
        }
        finally
        {
            JobSystem.ReleaseResourceToken(container.Token);
        }

        Assert.Equal(41, _data.ContainerObservedValue);
    }

    private void AssertRecorded(int value)
    {
        lock (_data.LogLock)
        {
            Assert.Contains(value, _data.Log);
        }
    }

    private void AssertNotRecorded(int value)
    {
        lock (_data.LogLock)
        {
            Assert.DoesNotContain(value, _data.Log);
        }
    }

    private sealed class ResourceData
    {
        internal readonly ExecutionEvents Events = new();

        internal readonly Lock LogLock = new();
        internal readonly List<int> Log = [];
        internal readonly List<string> ManagedLog = [];
        internal int Counter;
        internal int UnsafeCounter;
        internal int ContainerObservedValue;
        internal JobResource LastScopeResource;
        internal bool ChildUsedScopeResource;
        internal bool GrandchildUsedScopeResource;

        internal void Reset()
        {
            lock (LogLock)
            {
                Log.Clear();
            }

            ManagedLog.Clear();
            Counter = 0;
            UnsafeCounter = 0;
            ContainerObservedValue = 0;
            LastScopeResource = default;
            ChildUsedScopeResource = false;
            GrandchildUsedScopeResource = false;
        }

        internal void AddLog(int value)
        {
            lock (LogLock)
            {
                Log.Add(value);
            }
        }

        internal struct NoOpJob : IJob
        {
            public void Execute()
            {
            }
        }

        internal struct CountJob : IJob
        {
            private readonly ResourceData _data;

            internal CountJob(ResourceData data) => _data = data;

            public void Execute()
            {
                Interlocked.Increment(ref _data.Counter);
            }
        }

        internal readonly struct RecordJob : IJob
        {
            private readonly ResourceData _data;

            private readonly int _value;

            internal RecordJob(ResourceData data, int value)
            {
                _data = data;
                _value = value;
            }

            public void Execute()
            {
                _data.AddLog(_value);
            }
        }

        internal readonly struct BlockingRecordJob : IJob
        {
            private readonly ResourceData _data;

            private readonly int _value;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingRecordJob(ResourceData data, int value, JobSignal started, JobSignal gate)
            {
                _data = data;
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                _data.AddLog(_value);
            }
        }

        internal struct NonAtomicIncrementJob : IJob
        {
            private readonly ResourceData _data;

            internal NonAtomicIncrementJob(ResourceData data) => _data = data;

            public void Execute()
            {
                var value = _data.UnsafeCounter;
                _data.UnsafeCounter = value + 1;
            }
        }

        internal readonly struct RefContainingRecordJob : IJob
        {
            private readonly List<string> _target;
            private readonly string _value;
            private readonly JobSignal? _started;

            internal RefContainingRecordJob(List<string> target, string value, JobSignal? started = null)
            {
                _target = target;
                _value = value;
                _started = started;
            }

            public void Execute()
            {
                _started?.Set();
                _target.Add(_value);
            }
        }

        internal struct StrictDiagnosticJob : IJob
        {
            public void Execute()
            {
            }
        }

        internal struct ParentCreatesScopeResourceAndChildUsesIt : IJob
        {
            private readonly ResourceData _data;

            internal ParentCreatesScopeResourceAndChildUsesIt(ResourceData data) => _data = data;

            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-child");
                _data.LastScopeResource = resource;
                JobSystem.Schedule(new ScopeChildJob(_data, resource), JobResourceAccess.Read(resource));
            }
        }

        internal struct ParentCreatesScopeResourceAndGrandchildUsesIt : IJob
        {
            private readonly ResourceData _data;

            internal ParentCreatesScopeResourceAndGrandchildUsesIt(ResourceData data) => _data = data;

            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-grandchild");
                _data.LastScopeResource = resource;
                JobSystem.Schedule(new ScopeChildSchedulesGrandchildJob(_data, resource));
            }
        }

        internal struct ParentCreatesScopeResourceThenThrows : IJob
        {
            private readonly ResourceData _data;

            internal ParentCreatesScopeResourceThenThrows(ResourceData data) => _data = data;

            public void Execute()
            {
                _data.LastScopeResource = JobSystem.CreateScopeResource("scope-fault");
                throw new InvalidOperationException("scope failed");
            }
        }

        internal readonly struct ParentSchedulesBlockingChild : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal ParentSchedulesBlockingChild(
                ResourceData data, JobResource resource,
                JobSignal started,
                JobSignal gate)
            {
                _data = data;
                _resource = resource;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                JobSystem.Schedule(
                    new BlockingScopeChildJob(_data, _started, _gate),
                    JobResourceAccess.Read(_resource));
            }
        }

        internal readonly struct ParentSchedulesBlockingRangeChild : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal ParentSchedulesBlockingRangeChild(
                ResourceData data, JobResource resource,
                JobSignal started,
                JobSignal gate)
            {
                _data = data;
                _resource = resource;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                JobSystem.Schedule(
                    new BlockingScopeChildJob(_data, _started, _gate),
                    JobResourceAccess.Read(_resource, 0, 16));
            }
        }

        internal readonly struct ParentWithAccessSchedulesChildOnSameResource : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;

            internal ParentWithAccessSchedulesChildOnSameResource(ResourceData data, JobResource resource)
            {
                _data = data;
                _resource = resource;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ScopeChildJob(_data, _resource), JobResourceAccess.Read(_resource));
            }
        }

        internal readonly struct ParentWithAccessSchedulesReadThenWriteChildren : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;
            private readonly JobSignal _childrenScheduled;
            private readonly JobSignal _readerStarted;
            private readonly JobSignal _readerGate;
            private readonly JobSignal _writerStarted;
            private readonly JobSignal _writerGate;

            internal ParentWithAccessSchedulesReadThenWriteChildren(
                ResourceData data, JobResource resource,
                JobSignal childrenScheduled,
                JobSignal readerStarted,
                JobSignal readerGate,
                JobSignal writerStarted,
                JobSignal writerGate)
            {
                _data = data;
                _resource = resource;
                _childrenScheduled = childrenScheduled;
                _readerStarted = readerStarted;
                _readerGate = readerGate;
                _writerStarted = writerStarted;
                _writerGate = writerGate;
            }

            public void Execute()
            {
                JobSystem.Schedule(
                    new BlockingRecordJob(_data, 10, _readerStarted, _readerGate),
                    JobResourceAccess.Read(_resource));
                JobSystem.Schedule(
                    new BlockingRecordJob(_data, 11, _writerStarted, _writerGate),
                    JobResourceAccess.Write(_resource));
                _childrenScheduled.Set();
            }
        }

        internal readonly struct ParentSchedulesChildAfterGate : IJob
        {
            private readonly JobResource _childResource;
            private readonly JobSignal _parentStarted;
            private readonly JobSignal _allowChildSchedule;
            private readonly JobSignal _childScheduled;
            private readonly JobSignal _parentBodyReturning;
            private readonly JobSignal _childStarted;

            internal ParentSchedulesChildAfterGate(
                JobResource childResource,
                JobSignal parentStarted,
                JobSignal allowChildSchedule,
                JobSignal childScheduled,
                JobSignal parentBodyReturning,
                JobSignal childStarted)
            {
                _childResource = childResource;
                _parentStarted = parentStarted;
                _allowChildSchedule = allowChildSchedule;
                _childScheduled = childScheduled;
                _parentBodyReturning = parentBodyReturning;
                _childStarted = childStarted;
            }

            public void Execute()
            {
                _parentStarted.Set();
                _allowChildSchedule.Wait();
                JobSystem.Schedule(
                    new SignalJob(_childStarted),
                    JobResourceAccess.Write(_childResource));
                _childScheduled.Set();
                _parentBodyReturning.Set();
            }
        }

        internal readonly struct ParentSchedulesConflictingChildAfterGate : IJob
        {
            private readonly JobResource _childResource;
            private readonly JobSignal _parentStarted;
            private readonly JobSignal _allowChildSchedule;
            private readonly JobSignal _childScheduled;
            private readonly JobSignal _childStarted;
            private readonly JobSignal _childGate;

            internal ParentSchedulesConflictingChildAfterGate(
                JobResource childResource,
                JobSignal parentStarted,
                JobSignal allowChildSchedule,
                JobSignal childScheduled,
                JobSignal childStarted,
                JobSignal childGate)
            {
                _childResource = childResource;
                _parentStarted = parentStarted;
                _allowChildSchedule = allowChildSchedule;
                _childScheduled = childScheduled;
                _childStarted = childStarted;
                _childGate = childGate;
            }

            public void Execute()
            {
                _parentStarted.Set();
                _allowChildSchedule.Wait();
                JobSystem.Schedule(
                    new BlockingSignalJob(_childStarted, _childGate),
                    JobResourceAccess.Write(_childResource));
                _childScheduled.Set();
            }
        }

        internal readonly struct BlockingSignalJob : IJob
        {
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingSignalJob(JobSignal started, JobSignal gate)
            {
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
            }
        }

        internal readonly struct BlockingThrowJob : IJob
        {
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingThrowJob(JobSignal started, JobSignal gate)
            {
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                throw new InvalidOperationException("deferred dependency failed");
            }
        }

        internal readonly struct SignalJob : IJob
        {
            private readonly JobSignal _started;

            internal SignalJob(JobSignal started)
            {
                _started = started;
            }

            public void Execute()
            {
                _started.Set();
            }
        }

        internal readonly struct MarkIndexJob : IJobParallelFor
        {
            private readonly int[] _values;
            private readonly JobSignal? _started;

            internal MarkIndexJob(int[] values, JobSignal? started = null)
            {
                _values = values;
                _started = started;
            }

            public void Execute(int index)
            {
                _started?.Set();
                Interlocked.Increment(ref _values[index]);
            }
        }

        internal readonly struct WriteListValue : IJob
        {
            private readonly List<int> _values;
            private readonly int _value;

            internal WriteListValue(List<int> values, int value)
            {
                _values = values;
                _value = value;
            }

            public void Execute()
            {
                _values[0] = _value;
            }
        }

        internal readonly struct ReadListValue : IJob
        {
            private readonly ResourceData _data;

            private readonly List<int> _values;

            internal ReadListValue(ResourceData data, List<int> values)
            {
                _data = data;
                _values = values;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _values[0];
            }
        }

        internal readonly struct PushStackValue : IJob
        {
            private readonly Stack<int> _values;
            private readonly int _value;

            internal PushStackValue(Stack<int> values, int value)
            {
                _values = values;
                _value = value;
            }

            public void Execute()
            {
                _values.Push(_value);
            }
        }

        internal readonly struct ReadStackValue : IJob
        {
            private readonly ResourceData _data;

            private readonly Stack<int> _values;

            internal ReadStackValue(ResourceData data, Stack<int> values)
            {
                _data = data;
                _values = values;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _values.Peek();
            }
        }

        internal readonly struct WriteDictionaryValue : IJob
        {
            private readonly Dictionary<string, int> _values;
            private readonly string _key;
            private readonly int _value;

            internal WriteDictionaryValue(Dictionary<string, int> values, string key, int value)
            {
                _values = values;
                _key = key;
                _value = value;
            }

            public void Execute()
            {
                _values[_key] = _value;
            }
        }

        internal readonly struct ReadDictionaryValue : IJob
        {
            private readonly ResourceData _data;

            private readonly Dictionary<string, int> _values;
            private readonly string _key;

            internal ReadDictionaryValue(ResourceData data, Dictionary<string, int> values, string key)
            {
                _data = data;
                _values = values;
                _key = key;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _values[_key];
            }
        }

        internal readonly struct EnqueueQueueValue : IJob
        {
            private readonly Queue<int> _values;
            private readonly int _value;

            internal EnqueueQueueValue(Queue<int> values, int value)
            {
                _values = values;
                _value = value;
            }

            public void Execute()
            {
                _values.Enqueue(_value);
            }
        }

        internal readonly struct ReadQueueValue : IJob
        {
            private readonly ResourceData _data;

            private readonly Queue<int> _values;

            internal ReadQueueValue(ResourceData data, Queue<int> values)
            {
                _data = data;
                _values = values;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _values.Peek();
            }
        }

        internal readonly struct WriteArrayValue : IJob
        {
            private readonly int[] _values;
            private readonly int _index;
            private readonly int _value;

            internal WriteArrayValue(int[] values, int index, int value)
            {
                _values = values;
                _index = index;
                _value = value;
            }

            public void Execute()
            {
                _values[_index] = _value;
            }
        }

        internal readonly struct ReadArrayValue : IJob
        {
            private readonly ResourceData _data;

            private readonly int[] _values;
            private readonly int _index;

            internal ReadArrayValue(ResourceData data, int[] values, int index)
            {
                _data = data;
                _values = values;
                _index = index;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _values[_index];
            }
        }

        internal readonly struct BlockingWriteArrayValue : IJob
        {
            private readonly int[] _values;
            private readonly int _index;
            private readonly int _value;
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingWriteArrayValue(
                int[] values,
                int index,
                int value,
                JobSignal started,
                JobSignal gate)
            {
                _values = values;
                _index = index;
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                _values[_index] = _value;
            }
        }

        internal readonly struct CustomCounterContainer
        {
            internal readonly int[] Values;
            internal readonly JobResourceToken Token;

            internal CustomCounterContainer(int[] values, JobResourceToken token)
            {
                Values = values;
                Token = token;
            }
        }

        internal readonly struct CustomCounterProvider : IJobResourceProvider<CustomCounterContainer, WholeAccess>
        {
            public static JobResourceAccess Read(ref CustomCounterContainer container, WholeAccess access)
            {
                return JobResourceAccess.Read(container.Token);
            }

            public static JobResourceAccess Write(ref CustomCounterContainer container, WholeAccess access)
            {
                return JobResourceAccess.Write(container.Token);
            }

            public static JobResourceAccess Exclusive(ref CustomCounterContainer container, WholeAccess access)
            {
                return JobResourceAccess.Exclusive(container.Token);
            }
        }

        internal readonly struct WriteCustomCounter : IJob
        {
            private readonly CustomCounterContainer _container;
            private readonly int _value;

            internal WriteCustomCounter(CustomCounterContainer container, int value)
            {
                _container = container;
                _value = value;
            }

            public void Execute()
            {
                _container.Values[0] = _value;
            }
        }

        internal readonly struct ReadCustomCounter : IJob
        {
            private readonly ResourceData _data;

            private readonly CustomCounterContainer _container;

            internal ReadCustomCounter(ResourceData data, CustomCounterContainer container)
            {
                _data = data;
                _container = container;
            }

            public void Execute()
            {
                _data.ContainerObservedValue = _container.Values[0];
            }
        }

        private readonly struct ScopeChildJob : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;

            internal ScopeChildJob(ResourceData data, JobResource resource)
            {
                _data = data;
                _resource = resource;
            }

            public void Execute()
            {
                _ = _resource;
                _data.ChildUsedScopeResource = true;
            }
        }

        private readonly struct ScopeChildSchedulesGrandchildJob : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;

            internal ScopeChildSchedulesGrandchildJob(ResourceData data, JobResource resource)
            {
                _data = data;
                _resource = resource;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ScopeGrandchildJob(_data, _resource), JobResourceAccess.Read(_resource));
            }
        }

        private readonly struct ScopeGrandchildJob : IJob
        {
            private readonly ResourceData _data;

            private readonly JobResource _resource;

            internal ScopeGrandchildJob(ResourceData data, JobResource resource)
            {
                _data = data;
                _resource = resource;
            }

            public void Execute()
            {
                _ = _resource;
                _data.GrandchildUsedScopeResource = true;
            }
        }

        private readonly struct BlockingScopeChildJob : IJob
        {
            private readonly ResourceData _data;

            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingScopeChildJob(ResourceData data, JobSignal started, JobSignal gate)
            {
                _data = data;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                _data.ChildUsedScopeResource = true;
            }
        }
    }
}

namespace SomeEngine.Job.Tests;

public sealed class ResourceSafetyTests
{
    public ResourceSafetyTests()
    {
        JobSystem.ResetForTesting(workerCount: 4);
        JobSystem.SafetyMode = JobSafetyMode.Checked;
        ResourceJobs.Reset();
    }

    [Fact]
    public void CollectedContainerBindingRetriesTokenReleaseAfterActiveOwnerCompletes()
    {
        (JobResourceAccess access, WeakReference container) =
            CreateEphemeralContainerAccess();
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        JobHandle owner = JobSystem.Schedule(
            new ResourceJobs.BlockingSignalJob(started, gate),
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
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), access));
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

    private static void ForceFullCollection()
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

        JobSystem.Schedule(new ResourceJobs.NoOpJob(), JobResourceAccess.Read(resource)).Complete();
        JobSystem.ReleaseResource(resource);

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), JobResourceAccess.Read(resource)));
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
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), JobResourceAccess.Write(resource)).Complete();
            JobSystem.ReleaseResource(resource);
        }

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), JobResourceAccess.Read(stale)));
    }

    [Fact]
    public void AccessDeclarationsSupportReadWriteExclusiveAndDirectSchedule()
    {
        var resource = JobSystem.CreateResource("declared");

        JobSystem.Schedule(new ResourceJobs.CountJob()).Complete();
        JobSystem.Schedule(new ResourceJobs.CountJob(), JobResourceAccess.Read(resource)).Complete();
        JobSystem.Schedule(new ResourceJobs.CountJob(), JobResourceAccess.Write(resource)).Complete();
        JobSystem.Schedule(new ResourceJobs.CountJob(), JobResourceAccess.Exclusive(resource)).Complete();

        Assert.Equal(4, ResourceJobs.Counter);
    }

    [Fact]
    public void ExplicitDependencyAndAccessDependencyBothGateJob()
    {
        var resource = JobSystem.CreateResource("composed");
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var manualStarted = new ManualResetEventSlim();
        using var manualGate = new ManualResetEventSlim();
        using var dependentStarted = new ManualResetEventSlim();
        using var dependentGate = new ManualResetEventSlim();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var manual = JobSystem.Schedule(new ResourceJobs.BlockingRecordJob(2, manualStarted, manualGate));
        var dependent = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, dependentStarted, dependentGate),
            new[] { JobResourceAccess.Read(resource) },
            manual);

        Assert.True(writerStarted.Wait(1_000));
        Assert.True(manualStarted.Wait(1_000));
        AssertNotRecorded(3);

        manualGate.Set();
        manual.Complete();
        Assert.False(dependentStarted.Wait(100));

        writerGate.Set();
        Assert.True(dependentStarted.Wait(1_000));
        dependentGate.Set();
        dependent.Complete();
        writer.Complete();

        AssertRecorded(3);
    }

    [Fact]
    public void ReadReadAccessesCanRunTogether()
    {
        var resource = JobSystem.CreateResource("read-shared");
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, gate),
            JobResourceAccess.Read(resource));
        var second = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, secondStarted, gate),
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
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));

        writerGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        readerGate.Set();
        reader.Complete();
        writer.Complete();
    }

    [Fact]
    public void ReadWriteOrdersWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("read-write");
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(readerStarted.Wait(1_000));
        Assert.False(writerStarted.Wait(100));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();
        writer.Complete();
        reader.Complete();
    }

    [Fact]
    public void WriteWriteOrdersSecondWriterAfterFirstWriter()
    {
        var resource = JobSystem.CreateResource("write-write");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var second = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, secondStarted, secondGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(secondStarted.Wait(100));

        firstGate.Set();
        Assert.True(secondStarted.Wait(1_000));
        secondGate.Set();
        second.Complete();
        first.Complete();
    }

    [Fact]
    public void WritePendingReadThenWriteOrdersSecondWriterAfterReader()
    {
        var resource = JobSystem.CreateResource("write-read-write");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var secondWriterStarted = new ManualResetEventSlim();
        using var secondWriterGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var secondWriter = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, secondWriterStarted, secondWriterGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));
        Assert.False(secondWriterStarted.Wait(100));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        Assert.False(secondWriterStarted.Wait(100));

        readerGate.Set();
        Assert.True(secondWriterStarted.Wait(1_000));
        secondWriterGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, secondWriter }).Complete();
    }

    [Fact]
    public void ReleasedWriterRebuildsFrontierSoLaterWriterWaitsForActiveReader()
    {
        var resource = JobSystem.CreateResource("release-rebuild-read-frontier");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        first.Complete();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.False(writerStarted.Wait(100));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
    }

    [Fact]
    public void FailedPartialRegistrationRestoresClearedReaderFrontier()
    {
        var resource = JobSystem.CreateResource("rollback-reader-frontier");
        var stale = JobSystem.CreateResource("rollback-stale");
        JobSystem.ReleaseResource(stale);
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(readerStarted.Wait(1_000));

        JobResourceAccess[] accesses =
        [
            JobResourceAccess.Write(resource),
            JobResourceAccess.Read(stale)
        ];

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), accesses));

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.False(writerStarted.Wait(100));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
    }

    [Fact]
    public void UnrangedFrontierResumesAfterRangedAccessCompletes()
    {
        var resource = JobSystem.CreateResource("range-frontier-reentry");
        using var rangedStarted = new ManualResetEventSlim();
        using var rangedGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var ranged = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, rangedStarted, rangedGate),
            JobResourceAccess.Write(resource, 0, 16));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));

        Assert.True(rangedStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));

        rangedGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        ranged.Complete();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.False(writerStarted.Wait(100));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { reader, writer }).Complete();
    }

    [Fact]
    public void WriteThenMultiplePendingReadersThenWriteWaitsForAllReaders()
    {
        var resource = JobSystem.CreateResource("write-read-read-write");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var firstReaderStarted = new ManualResetEventSlim();
        using var firstReaderGate = new ManualResetEventSlim();
        using var secondReaderStarted = new ManualResetEventSlim();
        using var secondReaderGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var firstReader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, firstReaderStarted, firstReaderGate),
            JobResourceAccess.Read(resource));
        var secondReader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, secondReaderStarted, secondReaderGate),
            JobResourceAccess.Read(resource));
        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(4, writerStarted, writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(firstReaderStarted.Wait(100));
        Assert.False(secondReaderStarted.Wait(100));
        Assert.False(writerStarted.Wait(100));

        firstGate.Set();
        Assert.True(firstReaderStarted.Wait(1_000));
        Assert.True(secondReaderStarted.Wait(1_000));
        Assert.False(writerStarted.Wait(100));

        firstReaderGate.Set();
        Assert.False(writerStarted.Wait(100));

        secondReaderGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();

        JobSystem.CombineDependencies(new[] { first, firstReader, secondReader, writer }).Complete();
    }

    [Fact]
    public void WritePendingReadThenExclusiveOrdersExclusiveAfterReader()
    {
        var resource = JobSystem.CreateResource("write-read-exclusive");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var exclusiveStarted = new ManualResetEventSlim();
        using var exclusiveGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource));
        var exclusive = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, exclusiveStarted, exclusiveGate),
            JobResourceAccess.Exclusive(resource));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));
        Assert.False(exclusiveStarted.Wait(100));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        Assert.False(exclusiveStarted.Wait(100));

        readerGate.Set();
        Assert.True(exclusiveStarted.Wait(1_000));
        exclusiveGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, exclusive }).Complete();
    }

    [Fact]
    public void TokenWritePendingReadThenWriteOrdersSecondWriterAfterReader()
    {
        var token = JobSystem.CreateResourceToken("token-write-read-write");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var secondWriterStarted = new ManualResetEventSlim();
        using var secondWriterGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(token));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(token));
        var secondWriter = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, secondWriterStarted, secondWriterGate),
            JobResourceAccess.Write(token));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));
        Assert.False(secondWriterStarted.Wait(100));

        firstGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        Assert.False(secondWriterStarted.Wait(100));

        readerGate.Set();
        Assert.True(secondWriterStarted.Wait(1_000));
        secondWriterGate.Set();

        JobSystem.CombineDependencies(new[] { first, reader, secondWriter }).Complete();
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
                        new ResourceJobs.NonAtomicIncrementJob(),
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

        Assert.Equal(100, ResourceJobs.UnsafeCounter);
    }

    [Fact]
    public void ManagedTokenSerializesSameTokenAndDifferentTokensRunIndependently()
    {
        var firstToken = JobSystem.CreateResourceToken("managed-service-a");
        var secondToken = JobSystem.CreateResourceToken("managed-service-b");
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var sameTokenStarted = new ManualResetEventSlim();
        using var sameTokenGate = new ManualResetEventSlim();
        using var otherTokenStarted = new ManualResetEventSlim();
        using var otherTokenGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(firstToken));
        var sameToken = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, sameTokenStarted, sameTokenGate),
            JobResourceAccess.Write(firstToken));
        var otherToken = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(3, otherTokenStarted, otherTokenGate),
            JobResourceAccess.Write(secondToken));

        Assert.True(firstStarted.Wait(1_000));
        Assert.True(otherTokenStarted.Wait(1_000));
        Assert.False(sameTokenStarted.Wait(100));

        firstGate.Set();
        Assert.True(sameTokenStarted.Wait(1_000));
        sameTokenGate.Set();
        otherTokenGate.Set();
        JobSystem.CombineDependencies(new[] { first, sameToken, otherToken }).Complete();
    }

    [Fact]
    public void StaleTokenIsRejectedAndTokenComposesWithResourceAccess()
    {
        var staleToken = JobSystem.CreateResourceToken("stale-token");
        JobSystem.ReleaseResourceToken(staleToken);

        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(new ResourceJobs.NoOpJob(), JobResourceAccess.Read(staleToken)));

        var token = JobSystem.CreateResourceToken("live-token");
        var resource = JobSystem.CreateResource("live-resource");
        JobResourceAccess[] accesses =
        [
            JobResourceAccess.Write(token),
            JobResourceAccess.Read(resource)
        ];

        JobSystem.Schedule(new ResourceJobs.RefContainingRecordJob(ResourceJobs.ManagedLog, "ran"), accesses).Complete();

        Assert.Equal(["ran"], ResourceJobs.ManagedLog);
    }

    [Fact]
    public void ScopeOwnedResourceLivesThroughChildAndReleasesAfterParentCompletion()
    {
        JobSystem.Schedule(new ResourceJobs.ParentCreatesScopeResourceAndChildUsesIt()).Complete();

        Assert.True(ResourceJobs.ChildUsedScopeResource);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceJobs.NoOpJob(),
                JobResourceAccess.Read(ResourceJobs.LastScopeResource)));
    }

    [Fact]
    public void ScopeOwnedResourceLivesThroughGrandchild()
    {
        JobSystem.Schedule(new ResourceJobs.ParentCreatesScopeResourceAndGrandchildUsesIt()).Complete();

        Assert.True(ResourceJobs.GrandchildUsedScopeResource);
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceJobs.NoOpJob(),
                JobResourceAccess.Read(ResourceJobs.LastScopeResource)));
    }

    [Fact]
    public void ScopeOwnedResourceReleasesOnExceptionPath()
    {
        var handle = JobSystem.Schedule(new ResourceJobs.ParentCreatesScopeResourceThenThrows());

        Assert.Throws<InvalidOperationException>(() => handle.Complete());
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.Schedule(
                new ResourceJobs.NoOpJob(),
                JobResourceAccess.Read(ResourceJobs.LastScopeResource)));
    }

    [Fact]
    public void CheckedModeRejectsReleaseWhileResourceIsInUse()
    {
        var resource = JobSystem.CreateResource("in-use");
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var handle = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, started, gate),
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
                new ResourceJobs.StrictDiagnosticJob(),
                JobResourceAccess.Read(resource)));

        Assert.Equal(JobSafetyMode.Strict, ex.SafetyMode);
        Assert.EndsWith(nameof(ResourceJobs.StrictDiagnosticJob), ex.JobTypeName);
        Assert.Equal("strict-resource", ex.ResourceName);
        Assert.Equal(resource.Id, ex.ResourceId);
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
                new ResourceJobs.StrictDiagnosticJob(),
                JobResourceAccess.Read(stale)));

        Assert.Null(ex.ResourceName);
        Assert.Equal(stale.Id, reused.Id);
        Assert.Equal(stale.Id, ex.ResourceId);
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
                new ResourceJobs.StrictDiagnosticJob(),
                JobResourceAccess.Read(stale)));

        Assert.Null(ex.ResourceName);
        Assert.Equal(stale.Id, reused.Id);
        Assert.Equal(stale.Id, ex.ResourceId);
    }

    [Fact]
    public void FastModePermitsStaleUncheckedAccess()
    {
        JobSystem.SafetyMode = JobSafetyMode.Fast;
        var resource = JobSystem.CreateResource("fast-stale");
        JobSystem.ReleaseResource(resource);

        JobSystem.Schedule(new ResourceJobs.CountJob(), JobResourceAccess.Read(resource)).Complete();

        Assert.Equal(1, ResourceJobs.Counter);
    }

    [Fact]
    public void NonOverlappingWriteRangesRunTogether()
    {
        var resource = JobSystem.CreateResource("ranges");
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, gate),
            JobResourceAccess.Write(resource, 0, 16));
        var second = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, secondStarted, gate),
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
        using var firstStarted = new ManualResetEventSlim();
        using var firstGate = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var secondGate = new ManualResetEventSlim();

        var first = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, firstStarted, firstGate),
            JobResourceAccess.Write(resource, 0, 16));
        var second = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, secondStarted, secondGate),
            JobResourceAccess.Write(resource, 8, 16));

        Assert.True(firstStarted.Wait(1_000));
        Assert.False(secondStarted.Wait(100));

        firstGate.Set();
        Assert.True(secondStarted.Wait(1_000));
        secondGate.Set();
        second.Complete();
        first.Complete();
    }

    [Fact]
    public void ReadRangeAfterWriteRangeIsOrdered()
    {
        var resource = JobSystem.CreateResource("range-read");
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, writerStarted, writerGate),
            JobResourceAccess.Write(resource, 5, 10));
        var reader = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(2, readerStarted, readerGate),
            JobResourceAccess.Read(resource, 7, 2));

        Assert.True(writerStarted.Wait(1_000));
        Assert.False(readerStarted.Wait(100));

        writerGate.Set();
        Assert.True(readerStarted.Wait(1_000));
        readerGate.Set();
        reader.Complete();
        writer.Complete();
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
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var parallelStarted = new ManualResetEventSlim();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var parallel = JobSystem.ScheduleParallel(
            new ResourceJobs.MarkIndexJob(values, parallelStarted),
            values.Length,
            4,
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));
        Assert.False(parallelStarted.Wait(100));
        Assert.All(values, value => Assert.Equal(0, value));

        writerGate.Set();
        parallel.Complete();
        writer.Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ChildResourceAccessDelaysParentCompletion()
    {
        var resource = JobSystem.CreateResource("child-resource");
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var parent = JobSystem.Schedule(new ResourceJobs.ParentSchedulesBlockingChild(resource, started, gate));

        Assert.True(started.Wait(1_000));
        Assert.False(parent.IsCompleted);

        gate.Set();
        parent.Complete();

        Assert.True(ResourceJobs.ChildUsedScopeResource);
    }

    [Fact]
    public void RangeAccessFromChildScopeIsTracked()
    {
        var resource = JobSystem.CreateResource("child-range-resource");
        using var childStarted = new ManualResetEventSlim();
        using var childGate = new ManualResetEventSlim();

        var parent = JobSystem.Schedule(
            new ResourceJobs.ParentSchedulesBlockingRangeChild(resource, childStarted, childGate));

        Assert.True(childStarted.Wait(1_000));

        var overlappingWriter = JobSystem.Schedule(
            new ResourceJobs.RecordJob(42),
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
            new ResourceJobs.ParentWithAccessSchedulesChildOnSameResource(resource),
            JobResourceAccess.Write(resource)).Complete();

        Assert.True(ResourceJobs.ChildUsedScopeResource);
    }

    [Fact]
    public void ChildReadThenWriteOnParentDeclaredResourceOrdersWriterAfterChildReader()
    {
        var resource = JobSystem.CreateResource("parent-read-write-resource");
        using var childrenScheduled = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var readerGate = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();

        var parent = JobSystem.Schedule(
            new ResourceJobs.ParentWithAccessSchedulesReadThenWriteChildren(
                resource,
                childrenScheduled,
                readerStarted,
                readerGate,
                writerStarted,
                writerGate),
            JobResourceAccess.Write(resource));

        Assert.True(childrenScheduled.Wait(1_000));
        Assert.True(readerStarted.Wait(1_000));
        Assert.False(writerStarted.Wait(100));

        readerGate.Set();
        Assert.True(writerStarted.Wait(1_000));
        writerGate.Set();
        parent.Complete();
    }

    [Fact]
    public void ExternalSuccessorWaitsForParentBodyInsteadOfAttachedChildCompletion()
    {
        var parentResource = JobSystem.CreateResource("parent-work-release");
        var childResource = JobSystem.CreateResource("attached-child-work-release");
        using var parentStarted = new ManualResetEventSlim();
        using var allowChildSchedule = new ManualResetEventSlim();
        using var childScheduled = new ManualResetEventSlim();
        using var parentBodyReturning = new ManualResetEventSlim();
        using var childStarted = new ManualResetEventSlim();
        using var successorStarted = new ManualResetEventSlim();
        using var successorGate = new ManualResetEventSlim();

        var parent = JobSystem.Schedule(
            new ResourceJobs.ParentSchedulesChildAfterGate(
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
            new ResourceJobs.BlockingSignalJob(successorStarted, successorGate),
            successorAccesses);

        try
        {
            Assert.False(successorStarted.Wait(100));
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(parentBodyReturning.Wait(1_000));

            // The successor owns childResource before the parent creates its attached child.
            // The child therefore waits for the successor, while the successor waits only for
            // the parent's resource-owning work body -- not for the parent's attached child.
            Assert.True(successorStarted.Wait(1_000));
            Assert.False(childStarted.Wait(100));
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
    }

    [Fact]
    public void ExplicitResourceSuccessorReservesThenActivatesAfterLaterAttachedChild()
    {
        var parentResource = JobSystem.CreateResource("deferred-parent-resource");
        var childResource = JobSystem.CreateResource("deferred-child-resource");
        using var parentStarted = new ManualResetEventSlim();
        using var allowChildSchedule = new ManualResetEventSlim();
        using var childScheduled = new ManualResetEventSlim();
        using var childStarted = new ManualResetEventSlim();
        using var childGate = new ManualResetEventSlim();
        using var successorStarted = new ManualResetEventSlim();

        var parent = JobSystem.Schedule(
            new ResourceJobs.ParentSchedulesConflictingChildAfterGate(
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
            new ResourceJobs.SignalJob(successorStarted),
            successorAccesses,
            parent);

        // A deferred access is absent from the conflict frontier, but it still retains the
        // resource identity for the complete lifetime of the pending schedule.
        Assert.Throws<JobResourceSafetyException>(() =>
            JobSystem.ReleaseResource(childResource));

        try
        {
            Assert.False(successorStarted.Wait(100));
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(childStarted.Wait(1_000));
            Assert.False(successorStarted.Wait(100));
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
    }

    [Fact]
    public void ParallelExplicitResourceSuccessorActivatesAfterLaterAttachedChild()
    {
        var parentResource = JobSystem.CreateResource("parallel-deferred-parent");
        var childResource = JobSystem.CreateResource("parallel-deferred-child");
        using var parentStarted = new ManualResetEventSlim();
        using var allowChildSchedule = new ManualResetEventSlim();
        using var childScheduled = new ManualResetEventSlim();
        using var childStarted = new ManualResetEventSlim();
        using var childGate = new ManualResetEventSlim();
        using var successorStarted = new ManualResetEventSlim();
        var values = new int[8];

        var parent = JobSystem.Schedule(
            new ResourceJobs.ParentSchedulesConflictingChildAfterGate(
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
            new ResourceJobs.MarkIndexJob(values, successorStarted),
            values.Length,
            batchSize: 1,
            successorAccesses,
            parent);

        try
        {
            allowChildSchedule.Set();
            Assert.True(childScheduled.Wait(1_000));
            Assert.True(childStarted.Wait(1_000));
            Assert.False(successorStarted.Wait(100));

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
    }

    [Fact]
    public void FaultedExplicitDependencyCancelsDeferredResourceReservation()
    {
        var resource = JobSystem.CreateResource("faulted-deferred-reservation");
        using var dependencyStarted = new ManualResetEventSlim();
        using var dependencyGate = new ManualResetEventSlim();
        using var successorStarted = new ManualResetEventSlim();

        var dependency = JobSystem.Schedule(
            new ResourceJobs.BlockingThrowJob(dependencyStarted, dependencyGate));
        Assert.True(dependencyStarted.Wait(1_000));

        var successor = JobSystem.Schedule(
            new ResourceJobs.SignalJob(successorStarted),
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
            Assert.Equal("deferred dependency failed", successorFault.Message);
            Assert.False(successorStarted.IsSet);

            // Fault propagation cancels the reservation rather than leaking a permanent use.
            JobSystem.ReleaseResource(resource);
            var dependencyFault = Assert.Throws<InvalidOperationException>(() => dependency.Complete());
            Assert.Equal("deferred dependency failed", dependencyFault.Message);
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
        using var predecessorStarted = new ManualResetEventSlim();
        using var predecessorGate = new ManualResetEventSlim();
        using var successorStarted = new ManualResetEventSlim();
        JobHandle predecessor = JobSystem.Schedule(
            new ResourceJobs.BlockingThrowJob(predecessorStarted, predecessorGate),
            JobResourceAccess.Write(resource));
        Assert.True(predecessorStarted.Wait(TimeSpan.FromSeconds(5)));
        JobHandle successor = JobSystem.Schedule(
            new ResourceJobs.SignalJob(successorStarted),
            JobResourceAccess.Write(resource));

        try
        {
            Assert.False(successorStarted.Wait(TimeSpan.FromMilliseconds(100)));
            predecessorGate.Set();

            successor.Complete();
            Assert.True(successorStarted.IsSet);
            InvalidOperationException predecessorFault =
                Assert.Throws<InvalidOperationException>(() => predecessor.Complete());
            Assert.Equal("deferred dependency failed", predecessorFault.Message);
        }
        finally
        {
            predecessorGate.Set();
        }

        JobSystem.ReleaseResource(resource);
    }

    [Fact]
    public void MixedRefFreeAndRefContainingJobsShareResourceGraph()
    {
        var resource = JobSystem.CreateResource("mixed-lanes");
        using var writerStarted = new ManualResetEventSlim();
        using var writerGate = new ManualResetEventSlim();
        using var managedStarted = new ManualResetEventSlim();

        var writer = JobSystem.Schedule(
            new ResourceJobs.BlockingRecordJob(1, writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var managed = JobSystem.Schedule(
            new ResourceJobs.RefContainingRecordJob(ResourceJobs.ManagedLog, "managed", managedStarted),
            JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(1_000));
        Assert.False(managedStarted.Wait(100));
        Assert.Empty(ResourceJobs.ManagedLog);

        writerGate.Set();
        managed.Complete();
        writer.Complete();

        Assert.Equal(["managed"], ResourceJobs.ManagedLog);
    }

    [Fact]
    public void TypedListAccessesParticipateInResourceOrdering()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        ResourceJobs.Reset();
        var values = new List<int> { 0 };

        var writer = JobSystem.Schedule(
            new ResourceJobs.WriteListValue(values, 42),
            JobResourceAccess.Write(values));
        var reader = JobSystem.Schedule(
            new ResourceJobs.ReadListValue(values),
            JobResourceAccess.Read(values));

        reader.Complete();
        writer.Complete();

        Assert.Equal(42, ResourceJobs.ContainerObservedValue);
    }

    [Fact]
    public void TypedContainerAccessesSupportStackDictionaryQueueAndArrayRanges()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 2 });
        ResourceJobs.Reset();
        var stack = new Stack<int>();
        var dictionary = new Dictionary<string, int> { ["value"] = 0 };
        var queue = new Queue<int>();
        var array = new int[2];
        using var firstStarted = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var stackWriter = JobSystem.Schedule(
            new ResourceJobs.PushStackValue(stack, 7),
            JobResourceAccess.Write(stack));
        var stackReader = JobSystem.Schedule(
            new ResourceJobs.ReadStackValue(stack),
            JobResourceAccess.Read(stack));
        stackReader.Complete();
        stackWriter.Complete();

        var dictionaryWriter = JobSystem.Schedule(
            new ResourceJobs.WriteDictionaryValue(dictionary, "value", 17),
            JobResourceAccess.Write(dictionary));
        var dictionaryReader = JobSystem.Schedule(
            new ResourceJobs.ReadDictionaryValue(dictionary, "value"),
            JobResourceAccess.Read(dictionary));
        dictionaryReader.Complete();
        dictionaryWriter.Complete();

        var queueWriter = JobSystem.Schedule(
            new ResourceJobs.EnqueueQueueValue(queue, 23),
            JobResourceAccess.Write(queue));
        var queueReader = JobSystem.Schedule(
            new ResourceJobs.ReadQueueValue(queue),
            JobResourceAccess.Read(queue));
        queueReader.Complete();
        queueWriter.Complete();

        var firstArrayWriter = JobSystem.Schedule(
            new ResourceJobs.BlockingWriteArrayValue(array, index: 0, value: 11, firstStarted, gate),
            JobResourceAccess.Write(array, start: 0, length: 1));
        var secondArrayWriter = JobSystem.Schedule(
            new ResourceJobs.WriteArrayValue(array, index: 1, value: 13),
            JobResourceAccess.Write(array, start: 1, length: 1));

        Assert.True(firstStarted.Wait(1_000));
        secondArrayWriter.Complete();
        Assert.Equal(13, array[1]);

        gate.Set();
        firstArrayWriter.Complete();

        Assert.Equal(23, ResourceJobs.ContainerObservedValue);
        Assert.Equal(17, dictionary["value"]);
        Assert.Equal(11, array[0]);
    }

    [Fact]
    public void MemoryAccessesUseArrayBackedSliceRanges()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 2 });
        ResourceJobs.Reset();
        var array = new int[3];
        using var firstStarted = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        var firstWriter = JobSystem.Schedule(
            new ResourceJobs.BlockingWriteArrayValue(array, index: 0, value: 31, firstStarted, gate),
            JobResourceAccess.Write(array.AsMemory(0, 1)));
        var secondWriter = JobSystem.Schedule(
            new ResourceJobs.WriteArrayValue(array, index: 1, value: 37),
            JobResourceAccess.Write(array.AsMemory(1, 1)));

        Assert.True(firstStarted.Wait(1_000));
        secondWriter.Complete();
        Assert.Equal(37, array[1]);

        var readOnlyReader = JobSystem.Schedule(
            new ResourceJobs.ReadArrayValue(array, index: 0),
            JobResourceAccess.Read(new ReadOnlyMemory<int>(array, 0, 1)));

        Assert.False(readOnlyReader.IsCompleted);

        gate.Set();
        readOnlyReader.Complete();
        firstWriter.Complete();

        Assert.Equal(31, ResourceJobs.ContainerObservedValue);
    }

    [Fact]
    public void SpanAccessesUseExplicitArrayOwner()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 2 });
        ResourceJobs.Reset();
        var array = new int[2];
        var other = new int[1];
        using var firstStarted = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();

        Assert.Throws<ArgumentException>(() => JobResourceAccess.Read(array, other.AsSpan()));

        var firstWriter = JobSystem.Schedule(
            new ResourceJobs.BlockingWriteArrayValue(array, index: 0, value: 43, firstStarted, gate),
            JobResourceAccess.Write(array, array.AsSpan(0, 1)));
        var secondWriter = JobSystem.Schedule(
            new ResourceJobs.WriteArrayValue(array, index: 1, value: 47),
            JobResourceAccess.Write(array, array.AsSpan(1, 1)));

        Assert.True(firstStarted.Wait(1_000));
        secondWriter.Complete();
        Assert.Equal(47, array[1]);

        var reader = JobSystem.Schedule(
            new ResourceJobs.ReadArrayValue(array, index: 0),
            JobResourceAccess.Read(array, (ReadOnlySpan<int>)array.AsSpan(0, 1)));

        Assert.False(reader.IsCompleted);

        gate.Set();
        reader.Complete();
        firstWriter.Complete();

        Assert.Equal(43, ResourceJobs.ContainerObservedValue);
    }

    [Fact]
    public void CustomStructContainerProviderUsesTypedStaticAccess()
    {
        JobSystem.ResetForTesting(new JobRuntimeConfig { WorkerCount = 0 });
        ResourceJobs.Reset();
        var container = new ResourceJobs.CustomCounterContainer(
            [0],
            JobSystem.CreateResourceToken("custom-struct-container"));

        try
        {
            var writer = JobSystem.Schedule(
                new ResourceJobs.WriteCustomCounter(container, 41),
                JobResourceAccess.Write<ResourceJobs.CustomCounterContainer, ResourceJobs.CustomCounterProvider>(ref container));
            var reader = JobSystem.Schedule(
                new ResourceJobs.ReadCustomCounter(container),
                JobResourceAccess.Read<ResourceJobs.CustomCounterContainer, ResourceJobs.CustomCounterProvider>(ref container));

            reader.Complete();
            writer.Complete();
        }
        finally
        {
            JobSystem.ReleaseResourceToken(container.Token);
        }

        Assert.Equal(41, ResourceJobs.ContainerObservedValue);
    }

    private static void AssertRecorded(int value)
    {
        lock (ResourceJobs.LogLock)
        {
            Assert.Contains(value, ResourceJobs.Log);
        }
    }

    private static void AssertNotRecorded(int value)
    {
        lock (ResourceJobs.LogLock)
        {
            Assert.DoesNotContain(value, ResourceJobs.Log);
        }
    }

    private static class ResourceJobs
    {
        internal static readonly Lock LogLock = new();
        internal static readonly List<int> Log = [];
        internal static readonly List<string> ManagedLog = [];
        internal static int Counter;
        internal static int UnsafeCounter;
        internal static int ContainerObservedValue;
        internal static JobResource LastScopeResource;
        internal static bool ChildUsedScopeResource;
        internal static bool GrandchildUsedScopeResource;

        internal static void Reset()
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

        internal static void AddLog(int value)
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
            public void Execute()
            {
                Interlocked.Increment(ref Counter);
            }
        }

        internal readonly struct RecordJob : IJob
        {
            private readonly int _value;

            internal RecordJob(int value)
            {
                _value = value;
            }

            public void Execute()
            {
                AddLog(_value);
            }
        }

        internal readonly struct BlockingRecordJob : IJob
        {
            private readonly int _value;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingRecordJob(int value, ManualResetEventSlim started, ManualResetEventSlim gate)
            {
                _value = value;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                AddLog(_value);
            }
        }

        internal struct NonAtomicIncrementJob : IJob
        {
            public void Execute()
            {
                var value = UnsafeCounter;
                Thread.Sleep(1);
                UnsafeCounter = value + 1;
            }
        }

        internal readonly struct RefContainingRecordJob : IJob
        {
            private readonly List<string> _target;
            private readonly string _value;
            private readonly ManualResetEventSlim? _started;

            internal RefContainingRecordJob(List<string> target, string value, ManualResetEventSlim? started = null)
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
            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-child");
                LastScopeResource = resource;
                JobSystem.Schedule(new ScopeChildJob(resource), JobResourceAccess.Read(resource));
            }
        }

        internal struct ParentCreatesScopeResourceAndGrandchildUsesIt : IJob
        {
            public void Execute()
            {
                var resource = JobSystem.CreateScopeResource("scope-grandchild");
                LastScopeResource = resource;
                JobSystem.Schedule(new ScopeChildSchedulesGrandchildJob(resource));
            }
        }

        internal struct ParentCreatesScopeResourceThenThrows : IJob
        {
            public void Execute()
            {
                LastScopeResource = JobSystem.CreateScopeResource("scope-fault");
                throw new InvalidOperationException("scope failed");
            }
        }

        internal readonly struct ParentSchedulesBlockingChild : IJob
        {
            private readonly JobResource _resource;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal ParentSchedulesBlockingChild(
                JobResource resource,
                ManualResetEventSlim started,
                ManualResetEventSlim gate)
            {
                _resource = resource;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                JobSystem.Schedule(
                    new BlockingScopeChildJob(_started, _gate),
                    JobResourceAccess.Read(_resource));
            }
        }

        internal readonly struct ParentSchedulesBlockingRangeChild : IJob
        {
            private readonly JobResource _resource;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal ParentSchedulesBlockingRangeChild(
                JobResource resource,
                ManualResetEventSlim started,
                ManualResetEventSlim gate)
            {
                _resource = resource;
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                JobSystem.Schedule(
                    new BlockingScopeChildJob(_started, _gate),
                    JobResourceAccess.Read(_resource, 0, 16));
            }
        }

        internal readonly struct ParentWithAccessSchedulesChildOnSameResource : IJob
        {
            private readonly JobResource _resource;

            internal ParentWithAccessSchedulesChildOnSameResource(JobResource resource)
            {
                _resource = resource;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ScopeChildJob(_resource), JobResourceAccess.Read(_resource));
            }
        }

        internal readonly struct ParentWithAccessSchedulesReadThenWriteChildren : IJob
        {
            private readonly JobResource _resource;
            private readonly ManualResetEventSlim _childrenScheduled;
            private readonly ManualResetEventSlim _readerStarted;
            private readonly ManualResetEventSlim _readerGate;
            private readonly ManualResetEventSlim _writerStarted;
            private readonly ManualResetEventSlim _writerGate;

            internal ParentWithAccessSchedulesReadThenWriteChildren(
                JobResource resource,
                ManualResetEventSlim childrenScheduled,
                ManualResetEventSlim readerStarted,
                ManualResetEventSlim readerGate,
                ManualResetEventSlim writerStarted,
                ManualResetEventSlim writerGate)
            {
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
                    new BlockingRecordJob(10, _readerStarted, _readerGate),
                    JobResourceAccess.Read(_resource));
                JobSystem.Schedule(
                    new BlockingRecordJob(11, _writerStarted, _writerGate),
                    JobResourceAccess.Write(_resource));
                _childrenScheduled.Set();
            }
        }

        internal readonly struct ParentSchedulesChildAfterGate : IJob
        {
            private readonly JobResource _childResource;
            private readonly ManualResetEventSlim _parentStarted;
            private readonly ManualResetEventSlim _allowChildSchedule;
            private readonly ManualResetEventSlim _childScheduled;
            private readonly ManualResetEventSlim _parentBodyReturning;
            private readonly ManualResetEventSlim _childStarted;

            internal ParentSchedulesChildAfterGate(
                JobResource childResource,
                ManualResetEventSlim parentStarted,
                ManualResetEventSlim allowChildSchedule,
                ManualResetEventSlim childScheduled,
                ManualResetEventSlim parentBodyReturning,
                ManualResetEventSlim childStarted)
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
            private readonly ManualResetEventSlim _parentStarted;
            private readonly ManualResetEventSlim _allowChildSchedule;
            private readonly ManualResetEventSlim _childScheduled;
            private readonly ManualResetEventSlim _childStarted;
            private readonly ManualResetEventSlim _childGate;

            internal ParentSchedulesConflictingChildAfterGate(
                JobResource childResource,
                ManualResetEventSlim parentStarted,
                ManualResetEventSlim allowChildSchedule,
                ManualResetEventSlim childScheduled,
                ManualResetEventSlim childStarted,
                ManualResetEventSlim childGate)
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
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingSignalJob(ManualResetEventSlim started, ManualResetEventSlim gate)
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
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingThrowJob(ManualResetEventSlim started, ManualResetEventSlim gate)
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
            private readonly ManualResetEventSlim _started;

            internal SignalJob(ManualResetEventSlim started)
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
            private readonly ManualResetEventSlim? _started;

            internal MarkIndexJob(int[] values, ManualResetEventSlim? started = null)
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
            private readonly List<int> _values;

            internal ReadListValue(List<int> values)
            {
                _values = values;
            }

            public void Execute()
            {
                ContainerObservedValue = _values[0];
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
            private readonly Stack<int> _values;

            internal ReadStackValue(Stack<int> values)
            {
                _values = values;
            }

            public void Execute()
            {
                ContainerObservedValue = _values.Peek();
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
            private readonly Dictionary<string, int> _values;
            private readonly string _key;

            internal ReadDictionaryValue(Dictionary<string, int> values, string key)
            {
                _values = values;
                _key = key;
            }

            public void Execute()
            {
                ContainerObservedValue = _values[_key];
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
            private readonly Queue<int> _values;

            internal ReadQueueValue(Queue<int> values)
            {
                _values = values;
            }

            public void Execute()
            {
                ContainerObservedValue = _values.Peek();
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
            private readonly int[] _values;
            private readonly int _index;

            internal ReadArrayValue(int[] values, int index)
            {
                _values = values;
                _index = index;
            }

            public void Execute()
            {
                ContainerObservedValue = _values[_index];
            }
        }

        internal readonly struct BlockingWriteArrayValue : IJob
        {
            private readonly int[] _values;
            private readonly int _index;
            private readonly int _value;
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingWriteArrayValue(
                int[] values,
                int index,
                int value,
                ManualResetEventSlim started,
                ManualResetEventSlim gate)
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
            private readonly CustomCounterContainer _container;

            internal ReadCustomCounter(CustomCounterContainer container)
            {
                _container = container;
            }

            public void Execute()
            {
                ContainerObservedValue = _container.Values[0];
            }
        }

        private readonly struct ScopeChildJob : IJob
        {
            private readonly JobResource _resource;

            internal ScopeChildJob(JobResource resource)
            {
                _resource = resource;
            }

            public void Execute()
            {
                _ = _resource;
                ChildUsedScopeResource = true;
            }
        }

        private readonly struct ScopeChildSchedulesGrandchildJob : IJob
        {
            private readonly JobResource _resource;

            internal ScopeChildSchedulesGrandchildJob(JobResource resource)
            {
                _resource = resource;
            }

            public void Execute()
            {
                JobSystem.Schedule(new ScopeGrandchildJob(_resource), JobResourceAccess.Read(_resource));
            }
        }

        private readonly struct ScopeGrandchildJob : IJob
        {
            private readonly JobResource _resource;

            internal ScopeGrandchildJob(JobResource resource)
            {
                _resource = resource;
            }

            public void Execute()
            {
                _ = _resource;
                GrandchildUsedScopeResource = true;
            }
        }

        private readonly struct BlockingScopeChildJob : IJob
        {
            private readonly ManualResetEventSlim _started;
            private readonly ManualResetEventSlim _gate;

            internal BlockingScopeChildJob(ManualResetEventSlim started, ManualResetEventSlim gate)
            {
                _started = started;
                _gate = gate;
            }

            public void Execute()
            {
                _started.Set();
                _gate.Wait();
                ChildUsedScopeResource = true;
            }
        }
    }
}

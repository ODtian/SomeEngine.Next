using System.Collections.Concurrent;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class ChunkRequestSchedulerTests
{
    [Fact]
    public async Task ConcurrentRequestsDeduplicateOneLoadAndReturnIndependentPins()
    {
        byte[] payload = [2, 3, 5, 7, 11, 13, 17, 19];
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((41, payload));
        var loaderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            loaderEntered.TrySetResult();
            await releaseLoader.Task.WaitAsync(cancellationToken);
            return await document.AcquireChunkAsync(key, cancellationToken);
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 2);
        Task<ResidentChunkLease> firstTask = scheduler.AcquireAsync(41).AsTask();
        await loaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease> secondTask = scheduler.AcquireAsync(41).AsTask();
        releaseLoader.TrySetResult();
        ResidentChunkLease[] leases = await Task.WhenAll(firstTask, secondTask);

        Assert.Equal(1, Volatile.Read(ref loadCount));
        Assert.Equal(payload, leases[0].Memory.ToArray());
        Assert.Equal(payload, leases[1].Memory.ToArray());
        leases[0].Dispose();
        Assert.Throws<ObjectDisposedException>(() => leases[0].Memory);
        Assert.Equal(payload, leases[1].Memory.ToArray());

        ChunkStreamingSnapshot snapshot = scheduler.Metrics.Snapshot();
        Assert.Equal(2, snapshot.Requests);
        Assert.Equal(1, snapshot.CacheMisses);
        Assert.Equal(1, snapshot.DeduplicatedWaiters);
        Assert.Equal(payload.Length, snapshot.PinnedBytes);
        leases[1].Dispose();
        Assert.Equal(0, scheduler.Metrics.Snapshot().PinnedBytes);
    }

    [Fact]
    public async Task OneHundredConcurrentRequestsExecuteOneLoaderOperation()
    {
        byte[] payload = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((42, payload));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await document.AcquireChunkAsync(key, cancellationToken);
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            128,
            maxConcurrency: 4);
        Task<ResidentChunkLease>[] waiters = Enumerable.Range(0, 100)
            .Select(_ => scheduler.AcquireAsync(42).AsTask())
            .ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult();
        ResidentChunkLease[] leases = await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(1, Volatile.Read(ref loadCount));
            Assert.All(leases, lease => Assert.Equal(payload, lease.Memory.ToArray()));
        }
        finally
        {
            for (int index = 0; index < leases.Length; index++)
                leases[index].Dispose();
        }
    }

    [Fact]
    public async Task ResidentCacheHitPinPathAllocatesNoManagedBytes()
    {
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((43, [1, 2, 3, 4]));
        await using var scheduler = new ChunkRequestScheduler(
            (key, cancellationToken) => document.AcquireChunkAsync(key, cancellationToken),
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);
        ResidentChunkLease cold = await scheduler.AcquireAsync(43);
        cold.Dispose();
        Assert.True(scheduler.TryAcquireResident(43, out ResidentChunkLease warm));
        _ = warm.Memory.Length;
        warm.Dispose();

        long before = GC.GetAllocatedBytesForCurrentThread();
        if (!scheduler.TryAcquireResident(43, out ResidentChunkLease resident))
            throw new InvalidOperationException("The warmed chunk was not resident.");
        _ = resident.Memory.Length;
        resident.Dispose();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task CopiedResidentLeaseCannotReleaseTheSamePinTwiceOrEvictAnotherPin()
    {
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((45, [1, 2, 3, 4]));
        await using var scheduler = new ChunkRequestScheduler(
            (key, cancellationToken) => document.AcquireChunkAsync(key, cancellationToken),
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);

        ResidentChunkLease first = await scheduler.AcquireAsync(45);
        ResidentChunkLease second = await scheduler.AcquireAsync(45);
        ResidentChunkLease accidentalCopy = first;

        accidentalCopy.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = first.Memory.Length);
        first.Dispose(); // The duplicate token release is intentionally idempotent.

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, second.Memory.ToArray());
        Assert.Equal(0, scheduler.Trim());
        second.Dispose();
        Assert.Equal(1, scheduler.Trim());
        Assert.Equal(0, scheduler.Metrics.Snapshot().PinnedBytes);
    }

    [Fact]
    public async Task SchedulerAndUploadConsumersShareFourClassResidencyLedger()
    {
        var ledger = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            CompressedBytes = 32,
            DecodedCpuBytes = 8,
            UploadStagingBytes = 16,
            GpuBytes = 64,
        });
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((44, new byte[8]));
        await using var scheduler = new ChunkRequestScheduler(
            (key, cancellationToken) => document.AcquireChunkAsync(key, cancellationToken),
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 8,
            maxConcurrency: 1,
            residency: ledger);

        ResidentChunkLease decoded = await scheduler.AcquireAsync(44);
        using ResidencyReservation compressed = ledger.Reserve(ResidencyClass.Compressed, 4);
        using ResidencyReservation staging = ledger.Reserve(ResidencyClass.UploadStaging, 8);
        using ResidencyReservation gpu = ledger.Reserve(ResidencyClass.Gpu, 16);

        Assert.Equal(4, ledger.Used(ResidencyClass.Compressed));
        Assert.Equal(8, ledger.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(8, ledger.Used(ResidencyClass.UploadStaging));
        Assert.Equal(16, ledger.Used(ResidencyClass.Gpu));

        decoded.Dispose();
        Assert.Equal(1, scheduler.Trim());
        Assert.Equal(0, ledger.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task StoredAndDecodedBudgetsBackpressureConcurrentLoadsBeforeLoaderAllocation()
    {
        var ledger = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            CompressedBytes = 8,
            DecodedCpuBytes = 8,
            UploadStagingBytes = 16,
            GpuBytes = 16,
        });
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync(
            (46, new byte[8]),
            (47, new byte[8]));
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeLoaders = 0;
        int maximumActiveLoaders = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref activeLoaders);
            UpdateMaximum(ref maximumActiveLoaders, active);
            try
            {
                Assert.InRange(ledger.Used(ResidencyClass.Compressed), 0, 8);
                Assert.InRange(ledger.Used(ResidencyClass.DecodedCpu), 0, 8);
                if (key == 46)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondEntered.TrySetResult();
                }
                return await document.AcquireChunkAsync(key, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeLoaders);
            }
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 8,
            maxConcurrency: 2,
            residency: ledger);

        Task<ResidentChunkLease> firstTask = scheduler.AcquireAsync(46).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease> secondTask = scheduler.AcquireAsync(47).AsTask();
        Task prematureSecond = await Task.WhenAny(secondEntered.Task, Task.Delay(150));
        Assert.NotSame(secondEntered.Task, prematureSecond);
        Assert.Equal(1, Volatile.Read(ref maximumActiveLoaders));

        releaseFirst.TrySetResult();
        ResidentChunkLease first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
        first.Dispose();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ResidentChunkLease second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        second.Dispose();

        Assert.Equal(1, Volatile.Read(ref maximumActiveLoaders));
        Assert.InRange(ledger.Used(ResidencyClass.Compressed), 0, 8);
        Assert.InRange(ledger.Used(ResidencyClass.DecodedCpu), 0, 8);
    }

    [Fact]
    public async Task DocumentSchedulerReadsNearbyFaultsIntoIndependentFinalOwners()
    {
        byte[] firstPayload = Enumerable.Repeat((byte)0x31, 10).ToArray();
        byte[] secondPayload = Enumerable.Repeat((byte)0x52, 10).ToArray();
        byte[] unrelatedPayload = Enumerable.Repeat((byte)0x73, 10).ToArray();
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(101, firstPayload)
            .AddChunk(102, secondPayload)
            .AddChunk(900, unrelatedPayload)
            .BuildMapped();
        var countingSource = new CountingRangeSource(bytes);
        ResidencyBudgetLedger? ledger = null;
        long payloadFloor = long.MaxValue;
        long compressedAtPayloadRead = -1;
        long decodedAtPayloadRead = -1;
        var observingSource = new ObservingRangeSource(
            countingSource,
            (offset, _) =>
            {
                ResidencyBudgetLedger? currentLedger = ledger;
                if (currentLedger is null || offset < Volatile.Read(ref payloadFloor))
                    return;
                Interlocked.Exchange(
                    ref compressedAtPayloadRead,
                    currentLedger.Used(ResidencyClass.Compressed));
                Interlocked.Exchange(
                    ref decodedAtPayloadRead,
                    currentLedger.Used(ResidencyClass.DecodedCpu));
            });
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            observingSource,
            ownsSource: true);
        BinaryChunkEntry first = (await document.FindChunkAsync(101))!.Value;
        BinaryChunkEntry second = (await document.FindChunkAsync(102))!.Value;
        BinaryChunkEntry unrelated = (await document.FindChunkAsync(900))!.Value;
        long coalescedSpan = second.EndOffset - first.Offset;
        payloadFloor = first.Offset;
        ledger = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            CompressedBytes = first.StoredLength + second.StoredLength,
            DecodedCpuBytes = first.DecodedLength + second.DecodedLength,
            UploadStagingBytes = 1,
            GpuBytes = 1,
        });
        var metrics = new ChunkStreamingMetrics();
        countingSource.ResetOperations();

        await using var scheduler = ChunkRequestScheduler.CreateForDocument(
            document,
            decodedBudgetBytes: first.DecodedLength + second.DecodedLength,
            maxConcurrency: 2,
            metrics: metrics,
            residency: ledger);
        Task<ResidentChunkLease> firstTask = scheduler.AcquireAsync(101).AsTask();
        Task<ResidentChunkLease> secondTask = scheduler.AcquireAsync(102).AsTask();
        ResidentChunkLease[] leases = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(firstPayload, leases[0].Memory.ToArray());
            Assert.Equal(secondPayload, leases[1].Memory.ToArray());

            RangeOperation[] payloadReads = countingSource.Operations
                .Where(operation => operation.Offset >= first.Offset)
                .OrderBy(operation => operation.Offset)
                .ToArray();
            Assert.Collection(
                payloadReads,
                operation => Assert.Equal(
                    new RangeOperation(first.Offset, checked((int)first.StoredLength)),
                    operation),
                operation => Assert.Equal(
                    new RangeOperation(second.Offset, checked((int)second.StoredLength)),
                    operation));
            Assert.All(payloadReads, operation =>
                Assert.True(operation.Offset + operation.Length <= unrelated.Offset));
            Assert.InRange(
                Volatile.Read(ref compressedAtPayloadRead),
                Math.Min(first.StoredLength, second.StoredLength),
                first.StoredLength + second.StoredLength);
            Assert.InRange(
                Volatile.Read(ref decodedAtPayloadRead),
                Math.Min(first.DecodedLength, second.DecodedLength),
                first.DecodedLength + second.DecodedLength);

            ChunkStreamingSnapshot snapshot = metrics.Snapshot();
            Assert.Equal(first.StoredLength + second.StoredLength, snapshot.StoredBytesRead);
            Assert.Equal(firstPayload.Length + secondPayload.Length, snapshot.DecodedBytesLoaded);
            Assert.Equal(
                (first.StoredLength + second.StoredLength) / (double)(firstPayload.Length + secondPayload.Length),
                snapshot.ReadAmplification,
                precision: 10);
            Assert.Equal(0, ledger.Used(ResidencyClass.Compressed));
            Assert.Equal(firstPayload.Length + secondPayload.Length, ledger.Used(ResidencyClass.DecodedCpu));
        }
        finally
        {
            foreach (ResidentChunkLease lease in leases)
                lease.Dispose();
        }

        Assert.Equal(2, scheduler.Trim());
        Assert.Equal(0, ledger.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task CorruptIndependentReadFailsWithoutPoisoningItsNeighbor()
    {
        byte[] firstPayload = Enumerable.Repeat((byte)0x18, 16).ToArray();
        byte[] secondPayload = Enumerable.Repeat((byte)0x29, 16).ToArray();
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(201, firstPayload, compression: ChunkCompression.Brotli)
            .AddChunk(202, secondPayload, compression: ChunkCompression.Brotli)
            .BuildMapped();
        BinaryChunkEntry first;
        BinaryChunkEntry second;
        await using (BinaryDocument<TestRoot> metadataDocument = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true))
        {
            first = (await metadataDocument.FindChunkAsync(201))!.Value;
            second = (await metadataDocument.FindChunkAsync(202))!.Value;
        }
        long coalescedSpan = second.EndOffset - first.Offset;
        bytes[checked((int)first.Offset)] ^= 0xFF;
        TestFileRangeSource source = bytes.DetachToFileRangeSource();
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            source,
            ownsSource: true);
        source.ResetOperations();
        var ledger = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            CompressedBytes = coalescedSpan,
            DecodedCpuBytes = first.DecodedLength + second.DecodedLength,
            UploadStagingBytes = 1,
            GpuBytes = 1,
        });
        var metrics = new ChunkStreamingMetrics();

        await using var scheduler = ChunkRequestScheduler.CreateForDocument(
            document,
            decodedBudgetBytes: first.DecodedLength + second.DecodedLength,
            maxConcurrency: 2,
            metrics: metrics,
            residency: ledger);
        Task<ResidentChunkLease> corruptTask = scheduler.AcquireAsync(201).AsTask();
        Task<ResidentChunkLease> survivingTask = scheduler.AcquireAsync(202).AsTask();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await corruptTask);
        ResidentChunkLease surviving = await survivingTask.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(secondPayload, surviving.Memory.ToArray());
            RangeOperation[] payloadReads = source.Operations
                .Where(operation => operation.Offset >= first.Offset)
                .OrderBy(operation => operation.Offset)
                .ToArray();
            Assert.Collection(
                payloadReads,
                operation => Assert.Equal(
                    new RangeOperation(first.Offset, checked((int)first.StoredLength)),
                    operation),
                operation => Assert.Equal(
                    new RangeOperation(second.Offset, checked((int)second.StoredLength)),
                    operation));
            Assert.Equal(second.StoredLength, metrics.Snapshot().StoredBytesRead);
            Assert.Equal(secondPayload.Length, metrics.Snapshot().DecodedBytesLoaded);
            Assert.Equal(1, metrics.Snapshot().LoadsFailed);
            Assert.Equal(1, metrics.Snapshot().LoadsCompleted);
            Assert.Equal(0, ledger.Used(ResidencyClass.Compressed));
            Assert.Equal(secondPayload.Length, ledger.Used(ResidencyClass.DecodedCpu));
        }
        finally
        {
            surviving.Dispose();
        }

        Assert.Equal(1, scheduler.Trim());
        Assert.Equal(0, ledger.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task IndependentReadsDoNotRetainTheAlignmentGap()
    {
        byte[] firstPayload = Enumerable.Repeat((byte)0x41, 10).ToArray();
        byte[] secondPayload = Enumerable.Repeat((byte)0x62, 10).ToArray();
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(301, firstPayload)
            .AddChunk(302, secondPayload)
            .BuildMapped();
        var source = new CountingRangeSource(bytes);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            source,
            ownsSource: true);
        BinaryChunkEntry first = (await document.FindChunkAsync(301))!.Value;
        BinaryChunkEntry second = (await document.FindChunkAsync(302))!.Value;
        Assert.True(second.Offset > first.EndOffset);
        var ledger = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            // Both payloads fit concurrently, but their alignment gap deliberately does not.
            CompressedBytes = first.StoredLength + second.StoredLength,
            DecodedCpuBytes = first.DecodedLength + second.DecodedLength,
            UploadStagingBytes = 1,
            GpuBytes = 1,
        });
        var metrics = new ChunkStreamingMetrics();
        source.ResetOperations();

        await using var scheduler = ChunkRequestScheduler.CreateForDocument(
            document,
            decodedBudgetBytes: first.DecodedLength + second.DecodedLength,
            maxConcurrency: 2,
            metrics: metrics,
            residency: ledger);
        ResidentChunkLease[] leases = await Task.WhenAll(
                scheduler.AcquireAsync(301).AsTask(),
                scheduler.AcquireAsync(302).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            RangeOperation[] payloadReads = source.Operations
                .Where(operation => operation.Offset >= first.Offset)
                .OrderBy(operation => operation.Offset)
                .ToArray();
            Assert.Collection(
                payloadReads,
                operation =>
                {
                    Assert.Equal(first.Offset, operation.Offset);
                    Assert.Equal(first.StoredLength, operation.Length);
                },
                operation =>
                {
                    Assert.Equal(second.Offset, operation.Offset);
                    Assert.Equal(second.StoredLength, operation.Length);
                });
            Assert.Equal(
                first.StoredLength + second.StoredLength,
                metrics.Snapshot().StoredBytesRead);
            Assert.Equal(0, ledger.Used(ResidencyClass.Compressed));
        }
        finally
        {
            foreach (ResidentChunkLease lease in leases)
                lease.Dispose();
        }

        Assert.Equal(2, scheduler.Trim());
        Assert.Equal(0, ledger.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public void FirstRenderableMetricIsOneShotAndPreservesTheFirstTimestamp()
    {
        var metrics = new ChunkStreamingMetrics();
        Assert.False(metrics.Snapshot().HasFirstRenderable);

        Assert.True(metrics.TryMarkFirstRenderable());
        long first = metrics.Snapshot().TimeToFirstRenderableTicks;
        Assert.True(first > 0);
        Assert.False(metrics.TryMarkFirstRenderable());

        ChunkStreamingSnapshot snapshot = metrics.Snapshot();
        Assert.True(snapshot.HasFirstRenderable);
        Assert.Equal(first, snapshot.TimeToFirstRenderableTicks);
    }

    [Fact]
    public async Task CancelingOneWaiterDoesNotCancelSharedLoadOrOtherWaiters()
    {
        byte[] payload = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((51, payload));
        var loaderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;
        int loaderCancellationCount = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            loaderEntered.TrySetResult();
            try
            {
                await releaseLoader.Task.WaitAsync(cancellationToken);
                return await document.AcquireChunkAsync(key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref loaderCancellationCount);
                throw;
            }
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);
        Task<ResidentChunkLease> survivingWaiter = scheduler.AcquireAsync(51).AsTask();
        await loaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        Task<ResidentChunkLease> abandoningWaiter = scheduler.AcquireAsync(
            51,
            cancellationToken: canceled.Token).AsTask();

        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoningWaiter);
        releaseLoader.TrySetResult();
        using ResidentChunkLease lease = await survivingWaiter;

        Assert.Equal(payload, lease.Memory.ToArray());
        Assert.Equal(1, Volatile.Read(ref loadCount));
        Assert.Equal(0, Volatile.Read(ref loaderCancellationCount));
        ChunkStreamingSnapshot snapshot = scheduler.Metrics.Snapshot();
        Assert.Equal(1, snapshot.DeduplicatedWaiters);
        Assert.Equal(1, snapshot.Cancellations);
        Assert.Equal(1, snapshot.LoadsCompleted);
    }

    [Fact]
    public async Task DeadlineOnlyAbandonsThatWaiterWithoutCancelingSharedLoader()
    {
        byte[] payload = Enumerable.Range(0, 24).Select(static value => (byte)(value + 1)).ToArray();
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync((52, payload));
        var loaderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int loadCount = 0;
        int loaderCancellationCount = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            loaderEntered.TrySetResult();
            try
            {
                await releaseLoader.Task.WaitAsync(cancellationToken);
                return await document.AcquireChunkAsync(key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref loaderCancellationCount);
                throw;
            }
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);
        Task<ResidentChunkLease> expiringWaiter = scheduler.AcquireAsync(
            52,
            new ChunkRequestOptions(Deadline: DateTimeOffset.UtcNow.AddSeconds(1))).AsTask();
        await loaderEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease> survivingWaiter = scheduler.AcquireAsync(52).AsTask();

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => expiringWaiter);
            Assert.False(survivingWaiter.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref loaderCancellationCount));
        }
        finally
        {
            releaseLoader.TrySetResult();
        }

        using ResidentChunkLease lease = await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(payload, lease.Memory.ToArray());
        Assert.Equal(1, Volatile.Read(ref loadCount));
        Assert.Equal(0, Volatile.Read(ref loaderCancellationCount));
        Assert.Equal(1, scheduler.Metrics.Snapshot().Cancellations);
    }

    [Fact]
    public async Task MinimumIntegerPriorityRemainsLowerThanZeroPriority()
    {
        const ulong blockerKey = 71;
        const ulong minimumPriorityKey = 72;
        const ulong zeroPriorityKey = 73;
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync(
            (blockerKey, [1]),
            (minimumPriorityKey, [2]),
            (zeroPriorityKey, [3]));
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadOrder = new ConcurrentQueue<ulong>();

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            loadOrder.Enqueue(key);
            if (key == blockerKey)
            {
                blockerEntered.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            }
            return await document.AcquireChunkAsync(key, cancellationToken);
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);
        Task<ResidentChunkLease> blocker = scheduler.AcquireAsync(blockerKey).AsTask();
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease> minimum = scheduler.AcquireAsync(
            minimumPriorityKey,
            new ChunkRequestOptions(Priority: int.MinValue)).AsTask();
        Task<ResidentChunkLease> zero = scheduler.AcquireAsync(
            zeroPriorityKey,
            new ChunkRequestOptions(Priority: 0)).AsTask();

        releaseBlocker.TrySetResult();
        ResidentChunkLease[] leases = await Task.WhenAll(blocker, minimum, zero)
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(new[] { blockerKey, zeroPriorityKey, minimumPriorityKey }, loadOrder.ToArray());
        }
        finally
        {
            foreach (ResidentChunkLease lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public async Task HigherPriorityDuplicatePromotesQueuedSharedLoad()
    {
        const ulong blockerKey = 81;
        const ulong promotedKey = 82;
        const ulong normalKey = 83;
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync(
            (blockerKey, [1]),
            (promotedKey, [2]),
            (normalKey, [3]));
        var blockerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadOrder = new ConcurrentQueue<ulong>();

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            loadOrder.Enqueue(key);
            if (key == blockerKey)
            {
                blockerEntered.TrySetResult();
                await releaseBlocker.Task.WaitAsync(cancellationToken);
            }
            return await document.AcquireChunkAsync(key, cancellationToken);
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 64,
            maxConcurrency: 1);
        Task<ResidentChunkLease> blocker = scheduler.AcquireAsync(blockerKey).AsTask();
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease> initiallyLow = scheduler.AcquireAsync(
            promotedKey,
            new ChunkRequestOptions(Priority: -100)).AsTask();
        Task<ResidentChunkLease> normal = scheduler.AcquireAsync(
            normalKey,
            new ChunkRequestOptions(Priority: 0)).AsTask();
        Task<ResidentChunkLease> promotingWaiter = scheduler.AcquireAsync(
            promotedKey,
            new ChunkRequestOptions(Priority: 100)).AsTask();

        releaseBlocker.TrySetResult();
        ResidentChunkLease[] leases = await Task.WhenAll(blocker, initiallyLow, normal, promotingWaiter)
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(new[] { blockerKey, promotedKey, normalKey }, loadOrder.ToArray());
            Assert.Equal(3, loadOrder.Count);
            Assert.Equal(new byte[] { 2 }, leases[1].Memory.ToArray());
            Assert.Equal(new byte[] { 2 }, leases[3].Memory.ToArray());
        }
        finally
        {
            foreach (ResidentChunkLease lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public async Task ResidencyBudgetCannotEvictPinsThenEvictsLeastRecentUnpinnedEntries()
    {
        byte[] firstPayload = Enumerable.Repeat((byte)0x11, 8).ToArray();
        byte[] secondPayload = Enumerable.Repeat((byte)0x22, 8).ToArray();
        await using BinaryDocument<TestRoot> document = await OpenDocumentAsync(
            (61, firstPayload),
            (62, secondPayload));
        int loadCount = 0;

        async ValueTask<ChunkLease> LoadAsync(ulong key, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            return await document.AcquireChunkAsync(key, cancellationToken);
        }

        await using var scheduler = new ChunkRequestScheduler(
            LoadAsync,
            (key, cancellationToken) => EstimateAsync(document, key, cancellationToken),
            decodedBudgetBytes: 8,
            maxConcurrency: 1);
        ResidentChunkLease first = await scheduler.AcquireAsync(61);
        Task<ResidentChunkLease> waitingSecond = scheduler.AcquireAsync(62).AsTask();
        await Task.Delay(100);
        Assert.False(waitingSecond.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref loadCount));
        Assert.Equal(firstPayload, first.Memory.ToArray());
        Assert.Equal(8, scheduler.ResidentBytes);
        Assert.Equal(1, scheduler.ResidentCount);
        Assert.Equal(0, scheduler.Trim());

        first.Dispose();
        ResidentChunkLease second = await waitingSecond.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(secondPayload, second.Memory.ToArray());
        Assert.Equal(8, scheduler.ResidentBytes);
        Assert.Equal(1, scheduler.ResidentCount);
        Assert.Equal(0, scheduler.Trim());

        second.Dispose();
        Assert.Equal(1, scheduler.Trim());
        Assert.Equal(0, scheduler.ResidentBytes);
        Assert.Equal(0, scheduler.ResidentCount);
        Assert.Equal(2, Volatile.Read(ref loadCount));

        ChunkStreamingSnapshot snapshot = scheduler.Metrics.Snapshot();
        Assert.Equal(2, snapshot.Evictions);
        Assert.Equal(0, snapshot.LoadsFailed);
        Assert.Equal(0, snapshot.PinnedBytes);
        Assert.Equal(0, snapshot.ResidentBytes);
    }

    private static async ValueTask<BinaryDocument<TestRoot>> OpenDocumentAsync(
        params (ulong Key, byte[] Payload)[] chunks)
    {
        var builder = BinaryDocumentWriter.Create(TestRoots.Canonical());
        foreach ((ulong key, byte[] payload) in chunks)
            builder.AddChunk(key, payload);
        MappedTestDocument bytes = builder.BuildMapped();
        var source = new MemoryRangeSource(bytes, bytes.Length);
        try
        {
            return await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<ChunkLoadEstimate> EstimateAsync(
        BinaryDocument<TestRoot> document,
        ulong key,
        CancellationToken cancellationToken)
    {
        BinaryChunkEntry descriptor = await document.FindChunkAsync(key, cancellationToken)
            ?? throw new KeyNotFoundException($"Chunk 0x{key:X16} was not found.");
        return new ChunkLoadEstimate(descriptor.StoredLength, descriptor.DecodedLength);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed = Volatile.Read(ref target);
        while (observed < value)
        {
            int prior = Interlocked.CompareExchange(ref target, value, observed);
            if (prior == observed)
                return;
            observed = prior;
        }
    }

    private sealed class ObservingRangeSource(
        IRangeSource inner,
        Action<long, int> onAcquire) : IRangeSource
    {
        public long Length => inner.Length;
        public string Generation => inner.Generation;
        public bool LeasesAreImmutable => inner.LeasesAreImmutable;

        public ValueTask ReadExactlyAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
            => inner.ReadExactlyAsync(offset, destination, cancellationToken);

        public ValueTask<RangeLease> AcquireAsync(
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            onAcquire(offset, length);
            return inner.AcquireAsync(offset, length, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

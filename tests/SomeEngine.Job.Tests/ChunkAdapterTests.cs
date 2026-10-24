namespace SomeEngine.Job.Tests;

public sealed class ChunkAdapterTests
{
    private readonly ChunkAdapterData _data = new();

    public ChunkAdapterTests()
    {
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        _data.Reset();
    }

    [Fact]
    public void ChunkAdapterSchedulesAllChunks()
    {
        var values = new int[12];
        var source = ChunkAdapterData.ArrayChunkSource.Create(
            new JobChunk(0, 0, 3),
            new JobChunk(1, 3, 4),
            new JobChunk(2, 7, 5));

        new ChunkAdapterData.FillChunkJob(values).Schedule(source, batchSize: 1).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ChunkAdapterPreservesExplicitDependencyOrdering()
    {
        using var dependencyStarted = _data.Events.CreateSignal();
        using var dependencyGate = _data.Events.CreateSignal();
        using var chunkStarted = _data.Events.CreateSignal();
        var source = ChunkAdapterData.ArrayChunkSource.Create(new JobChunk(0, 0, 1));

        var dependency = JobSystem.Schedule(
            new ChunkAdapterData.BlockingJob(dependencyStarted, dependencyGate));
        var chunk = new ChunkAdapterData.SignalChunkJob(chunkStarted).Schedule(source, 1, dependency);

        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(2)));

        dependencyGate.Set();
        chunk.Complete();

        Assert.True(chunkStarted.IsSet);
        _data.Events.AssertBefore(dependencyGate, chunkStarted);
    }

    [Fact]
    public void ChunkAdapterUsesParallelDispatchOverChunkIndices()
    {
        var values = new int[16];
        var source = ChunkAdapterData.ArrayChunkSource.Create(
            new JobChunk(0, 0, 2),
            new JobChunk(1, 2, 2),
            new JobChunk(2, 4, 2),
            new JobChunk(3, 6, 2),
            new JobChunk(4, 8, 2),
            new JobChunk(5, 10, 2),
            new JobChunk(6, 12, 2),
            new JobChunk(7, 14, 2));

        new ChunkAdapterData.FillChunkJob(values).Schedule(source, batchSize: 3).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void EmptyChunkSourceReturnsCompletedHandleForDefaultDependency()
    {
        var source = ChunkAdapterData.ArrayChunkSource.Create();

        var handle = new ChunkAdapterData.CountChunkJob(_data).Schedule(source, batchSize: 1);

        Assert.True(handle.IsCompleted);
        handle.Complete();
        Assert.Equal(0, _data.ChunkExecutions);
    }

    [Fact]
    public void ChunkAdapterAcceptsClassBackedChunkSource()
    {
        var values = new int[4];
        var source = new ChunkAdapterData.ClassChunkSource(
            new JobChunk(0, 0, 2),
            new JobChunk(1, 2, 2));

        new ChunkAdapterData.FillChunkJob(values).Schedule(source, batchSize: 1).Complete();

        Assert.All(values, value => Assert.Equal(1, value));
    }

    [Fact]
    public void ChunkAdapterComposesWithResourceAccessInference()
    {
        using var writerStarted = _data.Events.CreateSignal();
        using var writerGate = _data.Events.CreateSignal();
        using var chunkStarted = _data.Events.CreateSignal();
        var resource = JobSystem.CreateResource("chunk-resource");
        var source = ChunkAdapterData.ArrayChunkSource.Create(new JobChunk(0, 0, 1));

        var writer = JobSystem.Schedule(
            new ChunkAdapterData.BlockingJob(writerStarted, writerGate),
            JobResourceAccess.Write(resource));
        var chunk = new ChunkAdapterData.SignalChunkJob(chunkStarted)
            .Schedule(source, 1, JobResourceAccess.Read(resource));

        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(2)));

        writerGate.Set();
        chunk.Complete();

        Assert.True(chunkStarted.IsSet);
        writer.Complete();
        _data.Events.AssertBefore(writerGate, chunkStarted);
    }

    [Fact]
    public void CoreRuntimeDoesNotReferenceAdapterOrEcsPackages()
    {
        var referencedAssemblies = typeof(JobSystem).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.DoesNotContain("SomeEngine.Job", referencedAssemblies);
        Assert.DoesNotContain(referencedAssemblies, name => name is not null && name.Contains("Unity", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedAssemblies, name => name is not null && name.Contains("Entities", StringComparison.Ordinal));
    }

    private sealed class ChunkAdapterData
    {
        internal readonly ExecutionEvents Events = new();

        internal int ChunkExecutions;

        internal void Reset()
        {
            ChunkExecutions = 0;
        }

        internal readonly struct ArrayChunkSource : IJobChunkSource
        {
            private readonly JobChunk[] _chunks;

            private ArrayChunkSource(JobChunk[] chunks)
            {
                _chunks = chunks;
            }

            public int ChunkCount => _chunks?.Length ?? 0;

            public static ArrayChunkSource Create(params JobChunk[] chunks)
            {
                return new ArrayChunkSource(chunks);
            }

            public JobChunk GetChunk(int index)
            {
                return _chunks[index];
            }
        }

        internal sealed class ClassChunkSource : IJobChunkSource
        {
            private readonly JobChunk[] _chunks;

            internal ClassChunkSource(params JobChunk[] chunks)
            {
                _chunks = chunks;
            }

            public int ChunkCount => _chunks.Length;

            public JobChunk GetChunk(int index)
            {
                return _chunks[index];
            }
        }

        internal readonly struct FillChunkJob : IJobChunk
        {
            private readonly int[] _values;

            internal FillChunkJob(int[] values)
            {
                _values = values;
            }

            public void Execute(JobChunk chunk)
            {
                for (var i = chunk.Start; i < chunk.Start + chunk.Length; i++)
                {
                    Interlocked.Increment(ref _values[i]);
                }
            }
        }

        internal struct CountChunkJob : IJobChunk
        {
            private readonly ChunkAdapterData _data;

            internal CountChunkJob(ChunkAdapterData data) => _data = data;

            public void Execute(JobChunk chunk)
            {
                _ = chunk;
                Interlocked.Increment(ref _data.ChunkExecutions);
            }
        }

        internal readonly struct SignalChunkJob : IJobChunk
        {
            private readonly JobSignal _started;

            internal SignalChunkJob(JobSignal started)
            {
                _started = started;
            }

            public void Execute(JobChunk chunk)
            {
                _ = chunk;
                _started.Set();
            }
        }

        internal readonly struct BlockingJob : IJob
        {
            private readonly JobSignal _started;
            private readonly JobSignal _gate;

            internal BlockingJob(JobSignal started, JobSignal gate)
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
    }
}

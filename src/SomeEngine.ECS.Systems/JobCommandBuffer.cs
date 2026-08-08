using System.Runtime.ExceptionServices;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

/// <summary>A serial Job callback that records into one producer-private command segment.</summary>
public interface IJobCommandProducer
{
    void Execute(ref JobCommandWriter commands);
}

/// <summary>
/// A parallel Job callback whose logical index is also its stable command merge key. Every index
/// owns a different command segment and may therefore record without a shared lock.
/// </summary>
public interface IJobParallelCommandProducer
{
    void Execute(int producerIndex, ref JobCommandWriter commands);
}

/// <summary>
/// Callback-scoped structural command capability. It never exposes the underlying CommandBuffer;
/// the segment is sealed when the callback returns and can only be replayed by its owning batch.
/// </summary>
public ref struct JobCommandWriter
{
    private readonly CommandBuffer _segment;

    internal JobCommandWriter(CommandBuffer segment)
    {
        _segment = segment;
    }

    public DeferredEntity CreateEntity() => _segment.CreateEntity();

    public void DestroyEntity(Entity entity) => _segment.DestroyEntity(entity);

    public void DestroyEntity(DeferredEntity entity) => _segment.DestroyEntity(entity);

    public void Add<T>(Entity entity, in T value)
        where T : struct, IComponent =>
        _segment.Add(entity, in value);

    public void Add<T>(DeferredEntity entity, in T value)
        where T : struct, IComponent =>
        _segment.Add(entity, in value);

    public void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent =>
        _segment.Replace(entity, in value);

    public void Replace<T>(DeferredEntity entity, in T value)
        where T : struct, IComponent =>
        _segment.Replace(entity, in value);

    public void Remove<T>(Entity entity)
        where T : struct, IComponent =>
        _segment.Remove<T>(entity);

    public void Remove<T>(DeferredEntity entity)
        where T : struct, IComponent =>
        _segment.Remove<T>(entity);

    public void AddTag<T>(Entity entity)
        where T : struct, ITag =>
        _segment.AddTag<T>(entity);

    public void AddTag<T>(DeferredEntity entity)
        where T : struct, ITag =>
        _segment.AddTag<T>(entity);

    public void RemoveTag<T>(Entity entity)
        where T : struct, ITag =>
        _segment.RemoveTag<T>(entity);

    public void RemoveTag<T>(DeferredEntity entity)
        where T : struct, ITag =>
        _segment.RemoveTag<T>(entity);

    public void AddBuffer<T>(Entity entity, scoped ReadOnlySpan<T> values = default)
        where T : struct, IBufferElement =>
        _segment.AddBuffer(entity, values);

    public void AddBuffer<T>(DeferredEntity entity, scoped ReadOnlySpan<T> values = default)
        where T : struct, IBufferElement =>
        _segment.AddBuffer(entity, values);

    public void ReplaceBuffer<T>(Entity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement =>
        _segment.ReplaceBuffer(entity, values);

    public void ReplaceBuffer<T>(DeferredEntity entity, scoped ReadOnlySpan<T> values)
        where T : struct, IBufferElement =>
        _segment.ReplaceBuffer(entity, values);

    public void RemoveBuffer<T>(Entity entity)
        where T : struct, IBufferElement =>
        _segment.RemoveBuffer<T>(entity);

    public void RemoveBuffer<T>(DeferredEntity entity)
        where T : struct, IBufferElement =>
        _segment.RemoveBuffer<T>(entity);

    public HierarchyCommandWriter<TDomain> Hierarchy<TDomain>()
        where TDomain : IHierarchyDomain =>
        _segment.Hierarchy<TDomain>();

    public HierarchyCommandWriter<DefaultHierarchyDomain> Hierarchy() =>
        _segment.Hierarchy();

    public RelationCommandWriter<T> Relations<T>()
        where T : struct, IComponent =>
        _segment.Relations<T>();
}

/// <summary>
/// Owns a fixed set of producer-private command segments. Producer callbacks may run concurrently;
/// playback merges their FIFO streams by ascending producer key and publishes the complete image in
/// one structural transaction. Producer exceptions are captured and rethrown by the batch finalizer
/// so a failed producer can never publish a partial command image.
/// </summary>
public sealed class JobCommandBuffer : IDisposable
{
    private const int SegmentCreated = 0;
    private const int SegmentScheduled = 1;
    private const int SegmentRunning = 2;
    private const int SegmentSucceeded = 3;
    private const int SegmentFaulted = 4;

    private const int LifecycleRecording = 0;
    private const int LifecyclePlaybackOwned = 1;
    private const int LifecycleCompleted = 2;
    private const int LifecycleDisposed = 3;

    private readonly World _world;
    private readonly ProducerSegment[] _segments;
    private readonly object _lifecycleGate = new();
    private JobHandle _emptyProducerDependency;
    private bool _emptyProducerScheduled;
    private int _lifecycle;

    public JobCommandBuffer(World world, int producerCount)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfNegative(producerCount);
        world.ThrowIfUnavailable();

        _world = world;
        _segments = new ProducerSegment[producerCount];
        for (int i = 0; i < _segments.Length; i++)
        {
            _segments[i] = new ProducerSegment(
                new CommandBuffer(
                    world,
                    worldOwned: false,
                    jobProducerOwned: true,
                    commandGate: new object()));
        }
    }

    public int ProducerCount => _segments.Length;

    /// <summary>Schedules one producer for a unique stable producer key.</summary>
    public JobHandle Schedule<TProducer>(
        int producerKey,
        in TProducer producer,
        JobHandle dependency = default)
        where TProducer : struct, IJobCommandProducer =>
        Schedule(producerKey, in producer, JobScheduleOptions.Default, dependency);

    /// <summary>Schedules one producer for a unique stable producer key.</summary>
    public JobHandle Schedule<TProducer>(
        int producerKey,
        in TProducer producer,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TProducer : struct, IJobCommandProducer
    {
        using JobSubmissionScope submissions = _world.EnterJobSubmissionScope();
        _world.ThrowIfJobCommandProducerControlPlane();
        lock (_lifecycleGate)
        {
            RequireRecordingLifecycle();
            ProducerSegment segment = Segment(producerKey);
            if (Interlocked.CompareExchange(
                    ref segment.State,
                    SegmentScheduled,
                    SegmentCreated) != SegmentCreated)
            {
                throw new InvalidOperationException(
                    $"Job command producer key {producerKey} has already been scheduled.");
            }

            try
            {
                var adapter = new SerialProducerAdapter<TProducer>(this, producerKey, in producer);
                JobHandle handle = JobSystem.Schedule(adapter, options, dependency);
                segment.Handle = handle;
                return handle;
            }
            catch
            {
                Interlocked.CompareExchange(
                    ref segment.State,
                    SegmentCreated,
                    SegmentScheduled);
                throw;
            }
        }
    }

    /// <summary>
    /// Schedules exactly one work item for every stable producer key. The Job scheduler may batch
    /// work internally, but merge order remains the logical index order rather than completion order.
    /// </summary>
    public JobHandle ScheduleParallel<TProducer>(
        in TProducer producer,
        int batchSize,
        JobHandle dependency = default)
        where TProducer : struct, IJobParallelCommandProducer =>
        ScheduleParallel(
            in producer,
            batchSize,
            ReadOnlySpan<JobResourceAccess>.Empty,
            JobScheduleOptions.Default,
            dependency);

    /// <summary>Schedules exactly one work item for every stable producer key.</summary>
    public JobHandle ScheduleParallel<TProducer>(
        in TProducer producer,
        int batchSize,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TProducer : struct, IJobParallelCommandProducer =>
        ScheduleParallel(
            in producer,
            batchSize,
            ReadOnlySpan<JobResourceAccess>.Empty,
            options,
            dependency);

    /// <summary>
    /// Schedules producer-private command segments with explicit read/write declarations for
    /// external arrays or resources inspected while commands are recorded.
    /// </summary>
    public JobHandle ScheduleParallel<TProducer>(
        in TProducer producer,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobHandle dependency = default)
        where TProducer : struct, IJobParallelCommandProducer =>
        ScheduleParallel(
            in producer,
            batchSize,
            accesses,
            JobScheduleOptions.Default,
            dependency);

    public JobHandle ScheduleParallel<TProducer>(
        in TProducer producer,
        int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses,
        JobScheduleOptions options,
        JobHandle dependency = default)
        where TProducer : struct, IJobParallelCommandProducer
    {
        using JobSubmissionScope submissions = _world.EnterJobSubmissionScope();
        _world.ThrowIfJobCommandProducerControlPlane();
        lock (_lifecycleGate)
        {
            RequireRecordingLifecycle();
            if (_segments.Length == 0)
            {
                if (_emptyProducerScheduled)
                {
                    throw new InvalidOperationException(
                        "The empty Job command producer set has already been scheduled.");
                }

                _emptyProducerScheduled = true;
                try
                {
                    _emptyProducerDependency = JobSystem.ScheduleParallel(
                        new ParallelProducerAdapter<TProducer>(this, in producer),
                        0,
                        batchSize,
                        accesses,
                        options,
                        dependency);
                    return _emptyProducerDependency;
                }
                catch
                {
                    _emptyProducerDependency = default;
                    _emptyProducerScheduled = false;
                    throw;
                }
            }

            int prepared = 0;
            try
            {
                for (; prepared < _segments.Length; prepared++)
                {
                    if (Interlocked.CompareExchange(
                            ref _segments[prepared].State,
                            SegmentScheduled,
                            SegmentCreated) != SegmentCreated)
                    {
                        throw new InvalidOperationException(
                            $"Job command producer key {prepared} has already been scheduled.");
                    }
                }

                var adapter = new ParallelProducerAdapter<TProducer>(this, in producer);
                JobHandle handle = JobSystem.ScheduleParallel(
                    adapter,
                    _segments.Length,
                    batchSize,
                    accesses,
                    options,
                    dependency);
                for (int i = 0; i < _segments.Length; i++)
                    _segments[i].Handle = handle;
                return handle;
            }
            catch
            {
                for (int i = 0; i < prepared; i++)
                {
                    Interlocked.CompareExchange(
                        ref _segments[i].State,
                        SegmentCreated,
                        SegmentScheduled);
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Schedules serial publication after all producers, followed by a mandatory lifecycle
    /// finalizer. The returned handle reports producer, dependency, or playback faults only after
    /// every unpublished segment has been invalidated.
    /// </summary>
    public JobHandle SchedulePlayback(JobScheduleOptions options = default)
    {
        using JobSubmissionScope submissions = _world.EnterJobSubmissionScope();
        _world.ThrowIfJobCommandProducerControlPlane();
        JobHandle publication;
        Exception? finalizerScheduleFault = null;
        lock (_lifecycleGate)
        {
            OwnPlaybackLifecycle();
            try
            {
                JobHandle dependency = CombineProducerHandles();
                JobResourceAccess topology = RelationshipJobAccess.TopologyWrite(_world);
                publication = JobSystem.Schedule(
                    new PublicationAdapter(this),
                    topology,
                    options,
                    dependency);
            }
            catch
            {
                Volatile.Write(ref _lifecycle, LifecycleRecording);
                throw;
            }

            try
            {
                return JobSystem.ScheduleFinally(
                    new CompletionAdapter(this, publication),
                    options,
                    publication);
            }
            catch (Exception exception)
            {
                // Publication is already owned by the scheduler. Recover outside the control-plane
                // lock so a producer that attempts an invalid reentrant operation cannot deadlock
                // the exceptional cleanup path.
                finalizerScheduleFault = exception;
            }
        }

        RecoverMissingCompletion(publication, finalizerScheduleFault!);
        throw new InvalidOperationException("Unreachable JobCommandBuffer recovery path.");
    }

    /// <summary>Completes all producers and publishes the batch synchronously.</summary>
    public void Playback()
    {
        _world.ThrowIfJobCommandProducerControlPlane();
        JobHandle dependency;
        lock (_lifecycleGate)
        {
            OwnPlaybackLifecycle();
            try
            {
                dependency = CombineProducerHandles();
            }
            catch
            {
                Volatile.Write(ref _lifecycle, LifecycleRecording);
                throw;
            }
        }

        try
        {
            dependency.Complete();
            PublishCompletedProducers();
        }
        catch
        {
            AbortAll(playbackCompletedCount: 0);
            throw;
        }
        finally
        {
            Volatile.Write(ref _lifecycle, LifecycleCompleted);
        }
    }

    public void Dispose()
    {
        _world.ThrowIfJobCommandProducerControlPlane();
        lock (_lifecycleGate)
        {
            int lifecycle = Volatile.Read(ref _lifecycle);
            if (lifecycle == LifecycleDisposed)
                return;
            if (lifecycle == LifecyclePlaybackOwned)
            {
                throw new InvalidOperationException(
                    "Cannot dispose a JobCommandBuffer while its playback finalizer is active.");
            }
            if (lifecycle == LifecycleRecording)
            {
                for (int i = 0; i < _segments.Length; i++)
                {
                    int state = Volatile.Read(ref _segments[i].State);
                    if (state is SegmentScheduled or SegmentRunning)
                    {
                        throw new InvalidOperationException(
                            "Cannot dispose a JobCommandBuffer while a producer is active.");
                    }
                }
            }

            Volatile.Write(ref _lifecycle, LifecycleDisposed);
        }

        for (int i = 0; i < _segments.Length; i++)
            _segments[i].Buffer.AbortJobProducerSegment(playbackCompleted: false);
    }

    private void ExecuteSerial<TProducer>(int producerKey, ref TProducer producer)
        where TProducer : struct, IJobCommandProducer
    {
        ProducerSegment segment = BeginProducer(producerKey);
        try
        {
            using JobCommandProducerScope scope = _world.EnterJobCommandProducer(segment.Buffer);
            var writer = new JobCommandWriter(segment.Buffer);
            producer.Execute(ref writer);
            CompleteProducer(segment, exception: null);
        }
        catch (Exception exception)
        {
            CompleteProducer(segment, exception);
        }
    }

    private void ExecuteParallel<TProducer>(int producerKey, ref TProducer producer)
        where TProducer : struct, IJobParallelCommandProducer
    {
        ProducerSegment segment = BeginProducer(producerKey);
        try
        {
            using JobCommandProducerScope scope = _world.EnterJobCommandProducer(segment.Buffer);
            var writer = new JobCommandWriter(segment.Buffer);
            producer.Execute(producerKey, ref writer);
            CompleteProducer(segment, exception: null);
        }
        catch (Exception exception)
        {
            CompleteProducer(segment, exception);
        }
    }

    private static void CompleteProducer(ProducerSegment segment, Exception? exception)
    {
        try
        {
            segment.Buffer.SealJobProducerSegment();
        }
        catch (Exception sealException)
        {
            exception ??= sealException;
        }

        if (exception is null)
        {
            Volatile.Write(ref segment.State, SegmentSucceeded);
            return;
        }

        segment.Fault = ExceptionDispatchInfo.Capture(exception);
        Volatile.Write(ref segment.State, SegmentFaulted);
    }

    private ProducerSegment BeginProducer(int producerKey)
    {
        ProducerSegment segment = Segment(producerKey);
        if (Interlocked.CompareExchange(
                ref segment.State,
                SegmentRunning,
                SegmentScheduled) != SegmentScheduled)
        {
            throw new InvalidOperationException(
                $"Job command producer key {producerKey} did not have one scheduled owner.");
        }
        return segment;
    }

    private void PublishCompletedProducers()
    {
        int visited = 0;
        try
        {
            List<Exception>? faults = null;
            int commandCount = 0;
            for (int i = 0; i < _segments.Length; i++)
            {
                ProducerSegment segment = _segments[i];
                int state = Volatile.Read(ref segment.State);
                if (state == SegmentFaulted)
                {
                    (faults ??= new List<Exception>()).Add(
                        segment.Fault?.SourceException ??
                        new InvalidOperationException($"Producer {i} faulted without an exception."));
                }
                else if (state != SegmentSucceeded)
                {
                    throw new InvalidOperationException(
                        $"Job command producer key {i} has not completed.");
                }

                commandCount = checked(commandCount + segment.Buffer.JobProducerCommandCount);
            }

            if (faults is not null)
            {
                AbortAll(visited);
                throw new AggregateException("One or more Job command producers failed.", faults);
            }

            if (commandCount == 0)
            {
                CompleteAll();
                return;
            }

            using WorldJobAdmissionScope admission = _world.EnterJobTopologyWrite();
            using StructuralMutationScope mutation = _world.BeginStructuralMutation();
            using CommandBuffer.JobProducerPlaybackBatch relationBatch =
                CommandBuffer.BeginJobProducerPlaybackBatch(_world);
            for (; visited < _segments.Length; visited++)
            {
                _segments[visited].Buffer.PlaybackJobProducerSegment(
                    mutation.PublicationEpoch,
                    relationBatch);
            }
            relationBatch.Complete();
            mutation.Commit();
            CompleteAll();
        }
        catch
        {
            AbortAll(visited);
            throw;
        }
    }

    private void CompleteScheduledPlayback(JobHandle publication)
    {
        try
        {
            publication.Complete();
        }
        catch
        {
            // Publication can be canceled before its callback runs by a failed producer or
            // explicit dependency. Resource predecessors only order work and never propagate
            // their fault. Invalidating again is harmless when PublishCompletedProducers already
            // handled a callback/playback failure.
            AbortAll(playbackCompletedCount: 0);
            throw;
        }
        finally
        {
            Volatile.Write(ref _lifecycle, LifecycleCompleted);
        }
    }

    private void RecoverMissingCompletion(
        JobHandle publication,
        Exception finalizerScheduleFault)
    {
        Exception? publicationFault = null;
        try
        {
            publication.Complete();
        }
        catch (Exception exception)
        {
            publicationFault = exception;
            AbortAll(playbackCompletedCount: 0);
        }
        finally
        {
            Volatile.Write(ref _lifecycle, LifecycleCompleted);
        }

        if (publicationFault is not null)
        {
            throw new AggregateException(
                "JobCommandBuffer publication and lifecycle-finalizer scheduling both failed.",
                finalizerScheduleFault,
                publicationFault);
        }

        ExceptionDispatchInfo.Capture(finalizerScheduleFault).Throw();
    }

    private void CompleteAll()
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            _segments[i].Buffer.CompleteJobProducerSegment();
            _segments[i].Fault = null;
        }
    }

    private void AbortAll(int playbackCompletedCount)
    {
        for (int i = 0; i < _segments.Length; i++)
        {
            _segments[i].Buffer.AbortJobProducerSegment(
                playbackCompleted: i < playbackCompletedCount);
            _segments[i].Fault = null;
        }
    }

    private JobHandle CombineProducerHandles()
    {
        if (_segments.Length == 0)
            return _emptyProducerScheduled ? _emptyProducerDependency : default;

        var handles = new JobHandle[_segments.Length];
        for (int i = 0; i < _segments.Length; i++)
        {
            int state = Volatile.Read(ref _segments[i].State);
            if (state == SegmentCreated)
            {
                throw new InvalidOperationException(
                    $"Job command producer key {i} was not scheduled.");
            }
            handles[i] = _segments[i].Handle;
        }
        return JobSystem.CombineDependencies(handles);
    }

    private void OwnPlaybackLifecycle()
    {
        if (Interlocked.CompareExchange(
                ref _lifecycle,
                LifecyclePlaybackOwned,
                LifecycleRecording) != LifecycleRecording)
        {
            throw new InvalidOperationException(
                "JobCommandBuffer playback may only be owned once.");
        }
    }

    private void RequireRecordingLifecycle()
    {
        if (Volatile.Read(ref _lifecycle) != LifecycleRecording)
        {
            throw new InvalidOperationException(
                "JobCommandBuffer no longer accepts producers after playback is owned.");
        }
    }

    private ProducerSegment Segment(int producerKey)
    {
        if ((uint)producerKey >= (uint)_segments.Length)
            throw new ArgumentOutOfRangeException(nameof(producerKey));
        return _segments[producerKey];
    }

    private sealed class ProducerSegment
    {
        internal ProducerSegment(CommandBuffer buffer)
        {
            Buffer = buffer;
        }

        internal readonly CommandBuffer Buffer;
        internal int State;
        internal JobHandle Handle;
        internal ExceptionDispatchInfo? Fault;
    }

    private struct SerialProducerAdapter<TProducer> : IJob
        where TProducer : struct, IJobCommandProducer
    {
        private readonly JobCommandBuffer _owner;
        private readonly int _producerKey;
        private TProducer _producer;

        internal SerialProducerAdapter(
            JobCommandBuffer owner,
            int producerKey,
            in TProducer producer)
        {
            _owner = owner;
            _producerKey = producerKey;
            _producer = producer;
        }

        public void Execute() => _owner.ExecuteSerial(_producerKey, ref _producer);
    }

    private struct ParallelProducerAdapter<TProducer> : IJobParallelFor
        where TProducer : struct, IJobParallelCommandProducer
    {
        private readonly JobCommandBuffer _owner;
        private TProducer _producer;

        internal ParallelProducerAdapter(JobCommandBuffer owner, in TProducer producer)
        {
            _owner = owner;
            _producer = producer;
        }

        public void Execute(int index) => _owner.ExecuteParallel(index, ref _producer);
    }

    private readonly struct PublicationAdapter : IJob
    {
        private readonly JobCommandBuffer _owner;

        internal PublicationAdapter(JobCommandBuffer owner)
        {
            _owner = owner;
        }

        public void Execute() => _owner.PublishCompletedProducers();
    }

    private readonly struct CompletionAdapter : IJob
    {
        private readonly JobCommandBuffer _owner;
        private readonly JobHandle _publication;

        internal CompletionAdapter(JobCommandBuffer owner, JobHandle publication)
        {
            _owner = owner;
            _publication = publication;
        }

        public void Execute() => _owner.CompleteScheduledPlayback(_publication);
    }
}

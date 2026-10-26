using SlangShaderSharp;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using SomeEngine.Job;

namespace SomeEngine.Graphics.Vulkan;

internal sealed partial class VulkanBackend
{
    [ThreadStatic]
    private static VulkanPipelineCache? t_pipelineWorkerCache;

    private sealed partial class VulkanDevice
    {
        private readonly VulkanPipelineWorker _pipelineWorker;

        internal VulkanPipelineWorker PipelineWorker => _pipelineWorker;
    }

    private sealed class VulkanPipelineWorker
    {
        private const int MaximumQueuedJobs = 256;
        private const int MaximumConcurrentRequests = 4;
        private enum QueueState { Accepting, Ending, Ended }
        private static readonly ConditionalWeakTable<Scheduler, JobCategory> Categories = new();
        private readonly object _gate = new();
        private readonly Channel<VulkanPipelineRequest> _requests;
        private readonly LinkedList<VulkanPipelineRequest> _dispatchedRequests = new();
        private readonly int _maximumDispatched;
        private int _dispatched;
        private int _running;
        private int _writing;
        private QueueState _queueState;
        private Exception? _terminalException;

        internal VulkanPipelineWorker()
        {
            _requests = Channel.CreateBounded<VulkanPipelineRequest>(
                new BoundedChannelOptions(MaximumQueuedJobs)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = false,
                    AllowSynchronousContinuations = false,
                });
            _maximumDispatched = Math.Clamp(
                (Environment.ProcessorCount + 3) / 4,
                1,
                MaximumConcurrentRequests);
        }

        internal Task<Pipeline> Enqueue(VulkanPipelineRequest request)
        {
            request.Scheduler = JobSystem.Current;
            Exception? rejection = null;
            lock (_gate)
            {
                if (_queueState != QueueState.Accepting) rejection = CreateTerminalException();
                else _writing++;
            }
            if (rejection is not null)
            {
                request.FailBeforeStart(rejection);
                return request.Task;
            }
            ValueTask write = _requests.Writer.WriteAsync(request);
            if (write.IsCompletedSuccessfully)
            {
                write.GetAwaiter().GetResult();
                FinishWrite();
                StartRequests();
            }
            else _ = CompleteEnqueueAsync(write, request);
            return request.Task;
        }

        internal void StopAccepting(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            VulkanPipelineRequest? detached = null;
            lock (_gate)
            {
                if (_queueState != QueueState.Accepting) return;
                Volatile.Write(ref _terminalException, exception);
                _queueState = QueueState.Ending;
                _requests.Writer.TryComplete();
                while (_dispatchedRequests.First is { } node)
                {
                    VulkanPipelineRequest request = node.Value;
                    _dispatchedRequests.RemoveFirst();
                    request.QueueNode = null;
                    request.QueueNext = detached;
                    detached = request;
                }
            }
            try
            {
                while (_requests.Reader.TryRead(out VulkanPipelineRequest? request))
                    request.FailBeforeStart(exception);
                while (detached is not null)
                {
                    VulkanPipelineRequest request = detached;
                    detached = request.QueueNext;
                    request.QueueNext = null;
                    request.FailBeforeStart(exception);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _queueState = QueueState.Ended;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        internal void StopAndJoin(Exception exception)
        {
            StopAccepting(exception);
            lock (_gate)
                while (_queueState != QueueState.Ended || _running != 0 || _writing != 0)
                    Monitor.Wait(_gate);
        }

        private async Task CompleteEnqueueAsync(
            ValueTask write,
            VulkanPipelineRequest request)
        {
            try
            {
                await write.ConfigureAwait(false);
                StartRequests();
            }
            catch
            {
                request.FailBeforeStart(CreateTerminalException());
            }
            finally { FinishWrite(); }
        }

        private void FinishWrite()
        {
            lock (_gate)
            {
                _writing--;
                Monitor.PulseAll(_gate);
            }
        }

        private static JobCategory GetCategory(Scheduler scheduler)
        {
            lock (Categories)
                return Categories.GetValue(scheduler, static executor => executor.CreateCategory(new()
                {
                    Name = "Vulkan pipeline",
                    MaxInFlight = MaximumQueuedJobs,
                    MaxExecuting = Math.Clamp(executor.WorkerCount, 1, MaximumConcurrentRequests),
                    ServiceShare = 1,
                }));
        }

        private void StartRequests()
        {
            while (true)
            {
                VulkanPipelineRequest request;
                lock (_gate)
                {
                    if (_queueState != QueueState.Accepting || _dispatched == _maximumDispatched ||
                        !_requests.Reader.TryRead(out request!)) return;
                    request.QueueNode = _dispatchedRequests.AddLast(request);
                    _dispatched++;
                }
                _ = SubmitAsync(request);
            }
        }

        private async Task SubmitAsync(VulkanPipelineRequest request)
        {
            try
            {
                Scheduler scheduler = request.Scheduler;
                JobHandle completed = await scheduler.ScheduleAsync(new CreatePipeline(this, request),
                    new() { Category = GetCategory(scheduler) }).ConfigureAwait(false);
                await completed.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (TryStart(request))
                {
                    try { request.FailBeforeStart(exception); }
                    finally { FinishRequest(); }
                }
            }
            finally
            {
                lock (_gate) _dispatched--;
                StartRequests();
            }
        }

        private bool TryStart(VulkanPipelineRequest request)
        {
            lock (_gate)
            {
                if (request.QueueNode is not { } node) return false;
                _dispatchedRequests.Remove(node);
                request.QueueNode = null;
                _running++;
                return true;
            }
        }

        private void FinishRequest()
        {
            lock (_gate)
            {
                _running--;
                Monitor.PulseAll(_gate);
            }
        }

        private void Execute(VulkanPipelineRequest request)
        {
            if (!TryStart(request)) return;
            try { request.Execute(); }
            finally { FinishRequest(); }
        }

        private readonly record struct CreatePipeline(VulkanPipelineWorker Worker,
            VulkanPipelineRequest Request) : IJob
        {
            public void Execute() => Worker.Execute(Request);
        }

        private Exception CreateTerminalException() =>
            Volatile.Read(ref _terminalException) ??
            new ObjectDisposedException(nameof(VulkanPipelineWorker));
    }

    private sealed class VulkanPipelineRequest
    {
        private readonly TaskCompletionSource<Pipeline> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly VulkanDevice _device;
        private SlangShaderData? _slangCode;
        private object? _code;
        private VulkanPipelineCache? _cache;
        private Func<Pipeline>? _create;
        private int _state;

        internal VulkanPipelineRequest(
            VulkanDevice device,
            SlangShaderData slangCode,
            object code,
            VulkanPipelineCache? cache,
            Func<Pipeline> create)
        {
            _device = device;
            _slangCode = slangCode;
            _code = code;
            _cache = cache;
            _create = create;
        }

        internal Task<Pipeline> Task => _completion.Task;
        internal Scheduler Scheduler { get; set; } = null!;
        internal LinkedListNode<VulkanPipelineRequest>? QueueNode { get; set; }
        internal VulkanPipelineRequest? QueueNext { get; set; }

        internal void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;
            VulkanPipelineCache? previousCache = t_pipelineWorkerCache;
            t_pipelineWorkerCache = _cache;
            try
            {
                _device.ThrowIfUnavailable();
                Pipeline pipeline = (_create ?? throw new ObjectDisposedException(
                    nameof(VulkanPipelineRequest)))();
                ReleaseCapturedState();
                Volatile.Write(ref _state, 2);
                _completion.TrySetResult(pipeline);
            }
            catch (Exception exception)
            {
                ReleaseCapturedState();
                Volatile.Write(ref _state, 2);
                _completion.TrySetException(_device.Loss ?? exception);
            }
            finally
            {
                t_pipelineWorkerCache = previousCache;
            }
        }

        internal void FailBeforeStart(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
                return;
            ReleaseCapturedState();
            _completion.TrySetException(exception);
        }

        private void ReleaseCapturedState()
        {
            Interlocked.Exchange(ref _create, null);
            _ = Interlocked.Exchange(ref _code, null);
            _ = Interlocked.Exchange(ref _slangCode, null);
            Interlocked.Exchange(ref _cache, null)?.ReleasePipelineCreationUse();
        }
    }

    private Task<Pipeline> EnqueuePipelineCreation(
        VulkanDevice device,
        VulkanPipelineCache? cache,
        SlangShaderData slangCode,
        object code,
        Func<Pipeline> create)
    {
        bool cacheRetained = false;
        try
        {
            if (cache is not null)
            {
                cache.RetainForPipelineCreation();
                cacheRetained = true;
            }
            return device.PipelineWorker.Enqueue(
                new VulkanPipelineRequest(device, slangCode, code, cache, create));
        }
        catch
        {
            if (cacheRetained)
                cache!.ReleasePipelineCreationUse();
            throw;
        }
    }
}

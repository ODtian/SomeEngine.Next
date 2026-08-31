using SlangShaderSharp;
using System.Threading.Channels;

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
        private const int MaximumWorkerCount = 4;

        private readonly Channel<VulkanPipelineJob> _jobs;
        private readonly Task[] _workers;
        private Exception? _terminalException;
        private int _accepting = 1;

        internal VulkanPipelineWorker()
        {
            _jobs = Channel.CreateBounded<VulkanPipelineJob>(
                new BoundedChannelOptions(MaximumQueuedJobs)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = false,
                    SingleReader = false,
                    AllowSynchronousContinuations = false,
                });
            int workerCount = Math.Clamp(
                (Environment.ProcessorCount + 3) / 4,
                1,
                MaximumWorkerCount);
            _workers = new Task[workerCount];
            for (int index = 0; index < workerCount; index++)
                _workers[index] = Task.Run(WorkerLoop);
        }

        internal Task<Pipeline> Enqueue(VulkanPipelineJob job)
        {
            if (Volatile.Read(ref _accepting) == 0)
            {
                job.FailBeforeStart(CreateTerminalException());
                return job.Task;
            }

            ValueTask write = _jobs.Writer.WriteAsync(job);
            if (!write.IsCompletedSuccessfully)
                _ = CompleteEnqueueAsync(write, job);
            return job.Task;
        }

        internal void StopAccepting(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.Exchange(ref _accepting, 0) == 0)
                return;
            Volatile.Write(ref _terminalException, exception);
            _jobs.Writer.TryComplete();
            while (_jobs.Reader.TryRead(out VulkanPipelineJob? job))
                job.FailBeforeStart(exception);
        }

        internal void StopAndJoin(Exception exception)
        {
            StopAccepting(exception);
            Task.WhenAll(_workers).GetAwaiter().GetResult();
        }

        private async Task CompleteEnqueueAsync(
            ValueTask write,
            VulkanPipelineJob job)
        {
            try
            {
                await write.ConfigureAwait(false);
            }
            catch
            {
                job.FailBeforeStart(CreateTerminalException());
            }
        }

        private async Task WorkerLoop()
        {
            try
            {
                while (await _jobs.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (_jobs.Reader.TryRead(out VulkanPipelineJob? job))
                    {
                        if (Volatile.Read(ref _accepting) == 0)
                            job.FailBeforeStart(CreateTerminalException());
                        else
                            job.Execute();
                    }
                }
            }
            catch (ChannelClosedException)
            {
            }
        }

        private Exception CreateTerminalException() =>
            Volatile.Read(ref _terminalException) ??
            new ObjectDisposedException(nameof(VulkanPipelineWorker));
    }

    private sealed class VulkanPipelineJob
    {
        private readonly TaskCompletionSource<Pipeline> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly VulkanDevice _device;
        private RetainedShaderProgram? _program;
        private VulkanPipelineCache? _cache;
        private Func<Pipeline>? _create;
        private int _state;

        internal VulkanPipelineJob(
            VulkanDevice device,
            RetainedShaderProgram program,
            VulkanPipelineCache? cache,
            Func<Pipeline> create)
        {
            _device = device;
            _program = program;
            _cache = cache;
            _create = create;
        }

        internal Task<Pipeline> Task => _completion.Task;

        internal void Execute()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;
            t_pipelineWorkerCache = _cache;
            try
            {
                _device.ThrowIfUnavailable();
                Pipeline pipeline = (_create ?? throw new ObjectDisposedException(
                    nameof(VulkanPipelineJob)))();
                _completion.TrySetResult(pipeline);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(_device.Loss ?? exception);
            }
            finally
            {
                t_pipelineWorkerCache = null;
                ReleaseCapturedState();
                Volatile.Write(ref _state, 2);
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
            Interlocked.Exchange(ref _program, null)?.Dispose();
            Interlocked.Exchange(ref _cache, null)?.ReleasePipelineCreationUse();
        }
    }

    private Task<Pipeline> EnqueuePipelineCreation(
        VulkanDevice device,
        VulkanPipelineCache? cache,
        RetainedShaderProgram program,
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
                new VulkanPipelineJob(device, program, cache, create));
        }
        catch
        {
            if (cacheRetained)
                cache!.ReleasePipelineCreationUse();
            program.Dispose();
            throw;
        }
    }

    private sealed class RetainedShaderProgram : IDisposable
    {
        private ShaderProgram? _program;

        private RetainedShaderProgram(ShaderProgram program) => _program = program;

        internal ShaderProgram Program => Volatile.Read(ref _program)
            ?? throw new ObjectDisposedException(nameof(RetainedShaderProgram));

        internal static RetainedShaderProgram Capture(ShaderProgram program)
        {
            ArgumentNullException.ThrowIfNull(program);
            return new RetainedShaderProgram(program);
        }

        public void Dispose() => Interlocked.Exchange(ref _program, null);
    }
}

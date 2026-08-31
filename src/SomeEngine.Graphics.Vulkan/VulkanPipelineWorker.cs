using SlangShaderSharp;
using System.Runtime.InteropServices.Marshalling;
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
        private RetainedSlangProgram? _program;
        private VulkanPipelineCache? _cache;
        private Func<Pipeline>? _create;
        private int _state;

        internal VulkanPipelineJob(
            VulkanDevice device,
            RetainedSlangProgram program,
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
        RetainedSlangProgram program,
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

    private sealed class RetainedSlangProgram : IDisposable
    {
        private IComponentType? _program;
        private ISession? _session;
        private IGlobalSession? _globalSession;

        private RetainedSlangProgram(
            IComponentType program,
            ISession session,
            IGlobalSession globalSession)
        {
            _program = program;
            _session = session;
            _globalSession = globalSession;
        }

        internal IComponentType Program => Volatile.Read(ref _program)
            ?? throw new ObjectDisposedException(nameof(RetainedSlangProgram));

        internal static RetainedSlangProgram Capture(IComponentType program)
        {
            ArgumentNullException.ThrowIfNull(program);
            IGlobalSession? globalReference = null;
            ISession? sessionReference = null;
            IComponentType? programReference = null;
            try
            {
                ISession session = program.GetSession();
                IGlobalSession globalSession = session.GetGlobalSession();
                globalReference = RetainComReference(globalSession);
                sessionReference = RetainComReference(session);
                programReference = RetainComReference(program);
                var retained = new RetainedSlangProgram(
                    programReference,
                    sessionReference,
                    globalReference);
                programReference = null;
                sessionReference = null;
                globalReference = null;
                return retained;
            }
            finally
            {
                ReleaseComReference(programReference);
                ReleaseComReference(sessionReference);
                ReleaseComReference(globalReference);
            }
        }

        public void Dispose()
        {
            ReleaseComReference(Interlocked.Exchange(ref _program, null));
            ReleaseComReference(Interlocked.Exchange(ref _session, null));
            ReleaseComReference(Interlocked.Exchange(ref _globalSession, null));
        }

        private static unsafe T RetainComReference<T>(T value)
            where T : class
        {
            try
            {
                void* pointer = ComInterfaceMarshaller<T>.ConvertToUnmanaged(value);
                try
                {
                    return UniqueComInterfaceMarshaller<T>.ConvertToManaged(pointer)
                        ?? throw new InvalidOperationException(
                            $"The {typeof(T).Name} COM reference could not be materialized.");
                }
                finally
                {
                    ComInterfaceMarshaller<T>.Free(pointer);
                }
            }
            catch (InvalidCastException)
            {
                return value;
            }
        }

        private static void ReleaseComReference<T>(T? value)
            where T : class
        {
            if (value is System.Runtime.InteropServices.Marshalling.ComObject wrapper)
                wrapper.FinalRelease();
        }
    }
}

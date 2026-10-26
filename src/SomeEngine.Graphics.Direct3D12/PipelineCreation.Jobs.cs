using System.Runtime.CompilerServices;
using SomeEngine.Job;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed partial class D3D12Backend
{
    private sealed partial class D3D12PipelineCompiler
    {
        private static readonly ConditionalWeakTable<Scheduler, JobCategory> Categories = new();

        private static JobCategory GetCategory(Scheduler scheduler)
        {
            lock (Categories)
                return Categories.GetValue(scheduler, static executor => executor.CreateCategory(new()
                {
                    Name = "D3D12 pipeline",
                    MaxInFlight = MaximumQueuedRequests,
                    MaxExecuting = Math.Clamp(executor.WorkerCount, 1, MaximumConcurrentRequests),
                    ServiceShare = 1,
                }));
        }

        private void StartRequests()
        {
            while (true)
            {
                PipelineCreationRequest request;
                lock (_gate)
                {
                    if (_stopping || _dispatched == _maximumDispatched) return;
                    LinkedListNode<PipelineCreationRequest>? node = _pending.First;
                    while (node is not null && node.Value.Dispatched) node = node.Next;
                    if (node is null) return;
                    request = node.Value;
                    request.Dispatched = true;
                    _dispatched++;
                }
                _ = SubmitAsync(request);
            }
        }

        private async Task SubmitAsync(PipelineCreationRequest request)
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
                bool removed;
                lock (_gate)
                {
                    removed = request.QueueNode is not null;
                    if (request.QueueNode is { } node)
                    {
                        _pending.Remove(node);
                        request.QueueNode = null;
                        _queueDepth--;
                    }
                }
                if (removed)
                {
                    request.ReleaseCapturedState();
                    RecordFailure(exception);
                    request.Fail(exception);
                }
            }
            finally
            {
                lock (_gate) _dispatched--;
                StartRequests();
            }
        }

        private readonly record struct CreatePipeline(D3D12PipelineCompiler Compiler,
            PipelineCreationRequest Request) : IJob
        {
            public void Execute() => Compiler.Execute(Request);
        }
    }
}

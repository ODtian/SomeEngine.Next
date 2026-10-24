using System.Diagnostics;
using System.Text.Json;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The sparse-range benchmark uses Direct3D 12.");

using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: false);
backend.TryEnumerateAdapters(options, [], out int adapterCount);
var adapters = new AdapterInfo[adapterCount];
backend.TryEnumerateAdapters(options, adapters, out _);
using Device device = backend.CreateDevice(new DeviceDesc(adapters[0].Id, [new DeviceQueueDesc(QueueType.Graphics)]));
Queue queue = backend.GetQueue(device, QueueType.Graphics);
List<object> results = [];
// Alternate the order to expose warmup/order effects rather than comparing only one pair.
for (int repeat = 0; repeat < 4; repeat++)
    foreach (int rangeCount in new[] { 1, 128 })
        foreach (bool shared in repeat % 2 == 0 ? new[] { false, true } : new[] { true, false })
            Measure(shared, rangeCount, repeat);
Console.WriteLine(JsonSerializer.Serialize(new { Adapter = adapters[0].Name, Method = "64 readers, 1 or 128 disjoint ranges, 16 warmup frames, 48 measured frames, four alternating repetitions. CPU execution includes submission and state commit; GPU wait is outside the measured interval. Allocation is on the calling thread only; validation is disabled.", Results = results }, new JsonSerializerOptions { WriteIndented = true }));

void Measure(bool shared, int rangeCount, int repeat)
{
    const int passCount = 64, warmup = 16, samples = 48;
    using var graph = new RenderGraph(backend, device, [queue], new RenderGraphDesc(MaximumFramesInFlight: 1));
    ulong size = (ulong)rangeCount * 32;
    var desc = new BufferDesc(size, BufferUsages.ShaderRead, "sparse resident data");
    using SomeEngine.Graphics.Buffer data = backend.CreateBuffer(device, desc, MemoryType.DeviceLocal);
    GraphBufferId buffer;
    var passes = new GraphPassId[passCount];
    using (RenderGraphEdit edit = graph.BeginEdit())
    {
        buffer = edit.DeclareExternalBuffer(desc, MemoryType.DeviceLocal);
        for (int i = 0; i < passCount; i++)
            passes[i] = edit.AddComputePass<uint, byte>("sparse reader", PassQueueSelection.Exact(queue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref uint _) => { },
                static (ref ComputePassCommandScope _, in uint _, in byte _) => { });
        edit.Commit();
    }
    BufferRange[] ranges = Enumerable.Range(0, rangeCount).Select(i => new BufferRange((ulong)i * 32, 16)).ToArray();
    var set = new BufferRangeSet(ranges);
    var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
    double[] registration = new double[samples], execution = new double[samples], allocation = new double[samples];
    for (int frameIndex = 0; frameIndex < warmup + samples; frameIndex++)
    {
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long begin = Stopwatch.GetTimestamp();
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalBuffer(buffer, data,
                [new BufferBoundaryState(new BufferRange(0, size), frameIndex == 0 ? data.InitialSync : PipelineSync.ComputeShading,
                    frameIndex == 0 ? data.InitialAccess : ResourceAccess.ShaderResource, ResourceContentState.Defined)]);
            foreach (GraphPassId id in passes)
            {
                PassDefinition pass = frame.GetPass(id);
                if (shared) pass.ReadRanges(buffer, set, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
                else foreach (BufferRange range in ranges) pass.Read(buffer, range, PipelineSync.ComputeShading, ResourceAccess.ShaderResource);
            }
            long execute = Stopwatch.GetTimestamp();
            frame.Execute(completions);
            long end = Stopwatch.GetTimestamp();
            if (frameIndex >= warmup)
            {
                int index = frameIndex - warmup;
                registration[index] = Stopwatch.GetElapsedTime(begin, execute).TotalMicroseconds;
                execution[index] = Stopwatch.GetElapsedTime(execute, end).TotalMicroseconds;
                allocation[index] = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            }
            foreach (QueueCompletion completion in completions)
                if (backend.WaitCpu(completion, TimeSpan.FromSeconds(10)) != WaitStatus.Completed) throw new TimeoutException();
        }
    }
    Array.Sort(registration); Array.Sort(execution); Array.Sort(allocation);
    results.Add(new { repeat, rangeCount, passCount, shared, samples,
        RegistrationMedianUs = registration[samples / 2], RegistrationP95Us = registration[(int)(samples * .95)],
        ExecutionMedianUs = execution[samples / 2], ExecutionP95Us = execution[(int)(samples * .95)],
        CallingThreadAllocationMedianBytes = allocation[samples / 2] });
    backend.CollectCompleted(device);
}

using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns one linked compute entry point and the RHI pipeline created from it.</summary>
public sealed class LinkedComputeKernel : IDisposable
{
    private bool _disposed;

    private LinkedComputeKernel(LiveShaderProgram program, GraphicsPipeline pipeline)
    {
        Program = program;
        Pipeline = pipeline;
    }

    public LiveShaderProgram Program { get; }
    public GraphicsPipeline Pipeline { get; }

    public static LinkedComputeKernel Create(
        IGraphicsBackend backend,
        Device device,
        AssetLoader assets,
        ComputeKernelRef? kernel,
        string label)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (kernel is null ||
            string.IsNullOrWhiteSpace(kernel.EntryPoint) ||
            !AssetGuid.TryParse(kernel.Shader?.ShaderGuid, out AssetGuid shaderGuid) ||
            shaderGuid.IsEmpty)
        {
            throw new InvalidDataException(
                $"Compute kernel '{label}' has no valid shader entry point.");
        }
        AssetHandle<Shader> handle = assets.Load(new AssetId<Shader>(shaderGuid));
        if (handle.LoadState != AssetLoadState.Ready)
            assets.WaitAsync(handle).AsTask().GetAwaiter().GetResult();
        using AssetRead<Shader> read = assets.Read(handle);
        return Create(backend, device, read.Value, kernel.EntryPoint, label);
    }

    public static LinkedComputeKernel Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        string entryPoint,
        string label)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        LiveShaderProgram? program = null;
        GraphicsPipeline? pipeline = null;
        try
        {
            program = LiveShaderProgram.Link(
                shader,
                [new LiveShaderEntry(entryPoint, LiveShaderStage.Compute)]);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    program.Program,
                    program.GetEntryPoint(0),
                    label));
            return new LinkedComputeKernel(program, pipeline);
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (pipeline is not null) TryDispose(pipeline, ref cleanupFailures);
            if (program is not null) TryDispose(program, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Compute kernel '{label}' construction failed and cleanup also reported failures.",
                    cleanupFailures);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        TryDispose(Pipeline, ref failures);
        TryDispose(Program, ref failures);
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static void TryDispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

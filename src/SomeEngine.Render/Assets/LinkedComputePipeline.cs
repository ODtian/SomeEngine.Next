using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns one RHI pipeline created from a cooked compute entry point.</summary>
public sealed class LinkedComputePipeline : IDisposable
{
    private bool _disposed;

    private LinkedComputePipeline(
        ShaderProgram program,
        ShaderEntryPoint entryPoint,
        GraphicsPipeline pipeline)
    {
        Program = program;
        EntryPoint = entryPoint;
        Pipeline = pipeline;
    }

    public ShaderProgram Program { get; }
    public ShaderEntryPoint EntryPoint { get; }
    public ShaderVariableLayout ParameterLayout => Program.Layout.GetGlobalParamsVarLayout();
    public GraphicsPipeline Pipeline { get; }

    public static LinkedComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        ShaderEntry? shader,
        string label)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (shader is null ||
            shader.Stage != ShaderStage.Compute ||
            string.IsNullOrWhiteSpace(shader.EntryPoint) || shader.Shader is null)
        {
            throw new InvalidDataException(
                $"Compute pipeline '{label}' has no valid shader entry.");
        }
        Shader loaded = shader.Shader;
        return Create(backend, device, loaded, shader.EntryPoint, label);
    }

    public static LinkedComputePipeline Create(
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
        GraphicsPipeline? pipeline = null;
        try
        {
            CookedShaderEntry cooked = shader.RequireEntry(
                device.Capabilities.ShaderTarget,
                entryPoint,
                ShaderStage.Compute);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    cooked.Program,
                    cooked.EntryPoint,
                    label));
            return new LinkedComputePipeline(cooked.Program, cooked.EntryPoint, pipeline);
        }
        catch (Exception primary)
        {
            List<Exception>? cleanupFailures = null;
            if (pipeline is not null) TryDispose(pipeline, ref cleanupFailures);
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, primary);
                throw new AggregateException(
                    $"Compute pipeline '{label}' construction failed and cleanup also reported failures.",
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

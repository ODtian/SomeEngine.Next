using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Assets;

/// <summary>Owns one RHI pipeline created from a direct Slang compute entry point.</summary>
public sealed class LinkedComputePipeline : IDisposable
{
    private bool _disposed;

    private LinkedComputePipeline(
        ShaderReflection reflection,
        EntryPointReflection entryPoint,
        GraphicsPipeline pipeline)
    {
        Reflection = reflection;
        EntryPoint = entryPoint;
        Pipeline = pipeline;
    }

    public ShaderReflection Reflection { get; }
    public EntryPointReflection EntryPoint { get; }
    public VariableLayoutReflection ParameterLayout =>
        Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
    public GraphicsPipeline Pipeline { get; }

    public static LinkedComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader? shader,
        EntryPointReflection entry,
        string label)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (entry == EntryPointReflection.Null || entry.Stage != SlangStage.Compute)
            throw new InvalidDataException($"Compute pipeline '{label}' has no compute entry.");
        int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
        return Create(
            backend,
            device,
            shader,
            entry,
            shader.GetSlangEntryBytes(targetIndex, entry),
            shader.GetSlangEntryBlob(targetIndex, entry),
            label);
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
            int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            EntryPointReflection entry = shader.RequireSlangEntry(
                targetIndex,
                entryPoint,
                SlangStage.Compute);
            return Create(
                backend,
                device,
                shader,
                entry,
                shader.GetSlangEntryBytes(targetIndex, entry),
                shader.GetSlangEntryBlob(targetIndex, entry),
                label);
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

    private static LinkedComputePipeline Create(
        IGraphicsBackend backend,
        Device device,
        Shader shader,
        EntryPointReflection entry,
        System.Buffers.ReadOnlySequence<byte> code,
        ISlangBlob? codeBlob,
        string label)
    {
        GraphicsPipeline? pipeline = null;
        try
        {
            int targetIndex = shader.RequireSlangTarget(device.Capabilities.CompileTarget);
            ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
            pipeline = backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    shader.SlangCode,
                    reflection,
                    entry,
                    code,
                    label,
                    CodeBlob: codeBlob));
            return new LinkedComputePipeline(reflection, entry, pipeline);
        }
        catch
        {
            pipeline?.Dispose();
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

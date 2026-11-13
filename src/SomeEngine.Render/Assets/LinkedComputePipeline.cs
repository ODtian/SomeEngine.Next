using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;

namespace SomeEngine.Render.Assets;

/// <summary>Pairs compute reflection with a pipeline retained by RenderPipelines.</summary>
public sealed class LinkedComputePipeline : IDisposable
{
    private LinkedComputePipeline(Shader shader, string label, ShaderReflection reflection, EntryPointReflection entryPoint, Pipeline pipeline)
    {
        Shader = shader;
        Label = label;
        Reflection = reflection;
        EntryPoint = entryPoint;
        Pipeline = pipeline;
    }

    public Shader Shader { get; private set; }
    public string Label { get; }
    public ShaderReflection Reflection { get; private set; }
    public EntryPointReflection EntryPoint { get; private set; }
    public VariableLayoutReflection ParameterLayout =>
        Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
    public Pipeline Pipeline { get; private set; }

    public static LinkedComputePipeline Create(
        RenderPipelines pipelines, Shader? shader, EntryPointReflection entry, string label)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (entry == EntryPointReflection.Null || entry.Stage != SlangStage.Compute)
            throw new InvalidDataException($"Compute pipeline '{label}' has no compute entry.");
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        Pipeline pipeline = pipelines.CreateCompute(new ComputePipelineDesc(
            shader.SlangCode, reflection, entry, shader.GetSlangEntryBytes(target, entry), label,
            CodeBlob: shader.GetSlangEntryBlob(target, entry))).GetAwaiter().GetResult();
        return new LinkedComputePipeline(shader, label, reflection, entry, pipeline);
    }

    public static LinkedComputePipeline Create(
        RenderPipelines pipelines, Shader shader, string entryPoint, string label)
    {
        int target = shader.RequireSlangTarget(pipelines.Device.Capabilities.CompileTarget);
        return Create(pipelines, shader,
            shader.RequireSlangEntry(target, entryPoint, SlangStage.Compute), label);
    }

    public bool ReplaceShader(RenderPipelines pipelines, Shader previous, Shader next)
    {
        if (!ReferenceEquals(Shader, previous)) return false;
        LinkedComputePipeline replacement = Create(pipelines, next, EntryPoint.Name, Label);
        Shader = next;
        Reflection = replacement.Reflection;
        EntryPoint = replacement.EntryPoint;
        Pipeline = replacement.Pipeline;
        return true;
    }

    public void Dispose() { }
}

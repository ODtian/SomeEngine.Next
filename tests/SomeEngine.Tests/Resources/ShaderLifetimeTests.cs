using System.Buffers;
using System.Runtime.CompilerServices;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources.Tests;

public sealed class ShaderLifetimeTests
{
    [Fact]
    public void PipelineDescriptionKeepsReflectionUsableAfterCompilerObjectsAreCollected()
    {
        for (int iteration = 0; iteration < 2; iteration++)
        {
            UsePipelineDescriptionAfterCollection();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UsePipelineDescriptionAfterCollection()
    {
        ComputePipelineDesc description = CreatePipelineDescription();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        ShaderReflection reflection = description.SlangCode.Code.GetLayout(0, out _);
        Assert.NotEqual(ShaderReflection.Null, reflection);
        Assert.Equal("Compute", reflection.GetEntryPointByIndex(0).Name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ComputePipelineDesc CreatePipelineDescription()
    {
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        TargetDesc[] targets = [new() { Format = SlangCompileTarget.Spirv,
            Profile = global.FindProfile("spirv_1_5"), Flags = SlangTargetFlags.GenerateSpirvDirectly }];
        Assert.True(global.CreateSession(new SessionDesc { Targets = targets }, out ISession session).Succeeded);
        const string text = "[shader(\"compute\")][numthreads(1,1,1)] void Compute() {}";
        IModule? source = session.LoadModuleFromSource("lifetime", "lifetime.slang", Slang.CreateBlob(text), out _);
        Assert.NotNull(source);
        Assert.True(source.GetDefinedEntryPoint(0, out IEntryPoint entry).Succeeded);
        Assert.True(session.CreateCompositeComponentType([source, entry], out IComponentType combined, out _).Succeeded);
        Assert.True(combined.Link(out IComponentType code, out _).Succeeded);
        Shader shader = Shader.CreateFromSlang("lifetime", global, session, [source], [entry], code,
            targets, generateCode: false);
        return new ComputePipelineDesc(shader.SlangCode, shader.GetSlangReflection(0),
            shader.GetSlangReflection(0).GetEntryPointByIndex(0), ReadOnlySequence<byte>.Empty);
    }
}

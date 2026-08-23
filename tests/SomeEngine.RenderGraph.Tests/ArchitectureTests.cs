using SomeEngine.Graphics;
using System.Runtime.CompilerServices;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void PublicSurfaceHasNoCompilerExecutorBackendOrCommandIr()
    {
        string[] forbidden =
        [
            "Compiler",
            "Executor",
            "Runtime",
            "Backend",
            "Adapter",
            "CommandPacket",
            "CommandOpcode",
            "ExecutionPlan",
            "CompiledGraph",
        ];
        Type[] publicTypes = typeof(RenderGraph).Assembly.GetExportedTypes();
        foreach (Type type in publicTypes)
            Assert.DoesNotContain(forbidden, value => type.Name.Contains(value, StringComparison.Ordinal));
    }

    [Fact]
    public void PublicSurfaceHasNoTransitionalOrUnimplementedConcepts()
    {
        string[] forbidden =
        [
            "RawPass",
            "PassAccess",
            "GraphStaticPlan",
            "GraphSamplerId",
            "GraphAccelerationStructureId",
            "GraphAccelerationStructureSrvId",
            "GraphSamplerFeedbackTextureId",
            "GraphSamplerFeedbackUavId",
            "GraphQueryAccessId",
            "GraphShaderTableAccessId",
            "RenderGraphExtension",
            "GraphPersistentBindingsId",
        ];
        HashSet<string> exported = typeof(RenderGraph).Assembly.GetExportedTypes()
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string name in forbidden)
            Assert.DoesNotContain(name, exported);
    }

    [Fact]
    public void DebugOptionsContainOnlyImplementedSwitches()
    {
        string[] expected =
        [
            nameof(RenderGraphDebugOptions.None),
            nameof(RenderGraphDebugOptions.DisableCulling),
            nameof(RenderGraphDebugOptions.DeclarationOrderScheduling),
            nameof(RenderGraphDebugOptions.DisableRasterMerging),
            nameof(RenderGraphDebugOptions.DisableSplitBarriers),
            nameof(RenderGraphDebugOptions.DisableParallelRecording),
        ];
        Assert.Equal(expected, Enum.GetNames<RenderGraphDebugOptions>());
    }

    [Fact]
    public void RuntimeIsNotAFriendAssembly()
    {
        string[] friends = typeof(RenderGraph).Assembly
            .GetCustomAttributes(typeof(InternalsVisibleToAttribute), inherit: false)
            .Cast<InternalsVisibleToAttribute>()
            .Select(static attribute => attribute.AssemblyName)
            .ToArray();
        Assert.DoesNotContain("SomeEngine.Runtime", friends);
    }

    [Fact]
    public void CommandScopesDoNotExposeContextBarrierOrSubmit()
    {
        Type[] scopes =
        [
            typeof(RasterPassCommandScope),
            typeof(ComputePassCommandScope),
            typeof(CopyPassCommandScope),
            typeof(GeneralPassCommandScope),
        ];
        foreach (Type scope in scopes)
        {
            Assert.DoesNotContain(scope.GetProperties(), property =>
                property.PropertyType == typeof(CommandContext) ||
                property.PropertyType == typeof(IGraphicsBackend));
            Assert.DoesNotContain(scope.GetMethods(), method =>
                method.Name is "Barrier" or "Submit");
        }
    }

    [Fact]
    public void PublicModesUseDomainNamesRatherThanImplementationShorthand()
    {
        Assert.Equal(
            [nameof(PassCullingMode.Cullable), nameof(PassCullingMode.NeverCull)],
            Enum.GetNames<PassCullingMode>());
        Assert.Equal(
            [
                nameof(PassSchedulingMode.Reorderable),
                nameof(PassSchedulingMode.PreserveDeclarationPosition),
            ],
            Enum.GetNames<PassSchedulingMode>());
        Assert.Equal(
            [nameof(PassRecordingMode.WorkerEligible), nameof(PassRecordingMode.CallingThread)],
            Enum.GetNames<PassRecordingMode>());
        Assert.Equal(
            [nameof(RasterPassMergeMode.Mergeable), nameof(RasterPassMergeMode.Isolated)],
            Enum.GetNames<RasterPassMergeMode>());
        Assert.Equal(
            [nameof(FrameSubmissionMode.Pipelined), nameof(FrameSubmissionMode.RecordAllThenSubmit)],
            Enum.GetNames<FrameSubmissionMode>());
        Assert.Equal(
            [
                nameof(RenderGraphResourceOwnership.GraphOwned),
                nameof(RenderGraphResourceOwnership.CallerOwned),
            ],
            Enum.GetNames<RenderGraphResourceOwnership>());
        Assert.Equal(
            [
                nameof(RenderGraphResourceLifetime.Persistent),
                nameof(RenderGraphResourceLifetime.PerFrame),
            ],
            Enum.GetNames<RenderGraphResourceLifetime>());
    }

    [Fact]
    public void ExternalResourceMethodsStateWhetherTheyDeclareRegisterOrBind()
    {
        string[] editMethods = typeof(RenderGraphEdit).GetMethods()
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(nameof(RenderGraphEdit.DeclareExternalBuffer), editMethods);
        Assert.Contains(nameof(RenderGraphEdit.DeclareExternalTexture), editMethods);
        Assert.Contains(nameof(RenderGraphEdit.RegisterExternalBuffer), editMethods);
        Assert.Contains(nameof(RenderGraphEdit.RegisterExternalTexture), editMethods);

        string[] frameMethods = typeof(RenderGraphFrame).GetMethods()
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(nameof(RenderGraphFrame.BindExternalBuffer), frameMethods);
        Assert.Contains(nameof(RenderGraphFrame.BindExternalTexture), frameMethods);
        Assert.DoesNotContain("Bind", frameMethods);
    }

    [Fact]
    public void SpecializedParameterBindingsNameTheirCompleteResourceFacts()
    {
        string[] factories = typeof(GraphParameterResourceBinding)
            .GetMethods(System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Static)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(nameof(GraphParameterResourceBinding.AccelerationStructure), factories);
        Assert.Contains(nameof(GraphParameterResourceBinding.SamplerFeedback), factories);
    }
}

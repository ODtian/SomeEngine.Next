namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

public sealed class VulkanReflectionProbeTests
{
    [Fact]
    public void TextureSamplerAndOutputShaderCreatesPipeline()
    {
        const string source = """
            RWTexture2D<float4> OutputTexture;
            struct Material
            {
                Texture2D<float4> Albedo;
                SamplerState Sampler;
            };
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID, uniform Material material)
            {
                OutputTexture[id.xy] = material.Albedo.SampleLevel(material.Sampler, float2(0, 0), 0);
            }
            """;
        VulkanTestShader shader = VulkanTestShader.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device, new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        Assert.Equal(PipelineType.Compute, pipeline.Type);
    }

    [Fact]
    public void Probe_global_uniform_layout()
    {
        const string source = """
            float4 Tint;
            Texture2D<float4> InputTexture;
            SamplerState InputSampler;
            RWStructuredBuffer<float4> OutputBuffer;
            [shader("vertex")]
            float4 vertexMain(uint id : SV_VertexID) : SV_Position
            {
                return float4(0, 0, 0, 1);
            }
            [shader("fragment")]
            float4 pixelMain() : SV_Target0
            {
                OutputBuffer[0] = Tint;
                return InputTexture.Sample(InputSampler, float2(0, 0)) * Tint;
            }
            """;
        VulkanTestShader shader = VulkanTestShader.Compile(
            source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        VariableLayoutReflection global = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection layout = global.TypeLayout.UnwrapArray();
        if (layout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            layout = layout.ElementTypeLayout.UnwrapArray();
        var report = new System.Text.StringBuilder();
        report.AppendLine($"global={global.Name} kind={global.TypeLayout.Kind} data={layout.Kind} uniform={layout.GetSize(SlangParameterCategory.Uniform)}");
        report.AppendLine($"bindings={layout.BindingRangeCount} sets={layout.DescriptorSetCount} categories={global.CategoryCount}");
        for (uint category = 0; category < global.CategoryCount; category++)
        {
            SlangParameterCategory value = global.GetCategoryByIndex(category);
            report.AppendLine($"category {value}: offset={global.GetOffset(value)} space={global.GetBindingSpace(value)}");
        }
        for (nint range = 0; range < layout.BindingRangeCount; range++)
        {
            report.AppendLine($"range {range}: type={layout.GetBindingRangeType(range)} count={layout.GetBindingRangeBindingCount(range)} descriptorRanges={layout.GetBindingRangeDescriptorRangeCount(range)} set={layout.GetBindingRangeDescriptorSetIndex(range)} first={layout.GetBindingRangeFirstDescriptorRangeIndex(range)}");
        }
        for (nint set = 0; set < layout.DescriptorSetCount; set++)
        {
            report.AppendLine($"set {set}: space={layout.GetDescriptorSetSpaceOffset(set)} ranges={layout.GetDescriptorSetDescriptorRangeCount(set)}");
            for (nint range = 0; range < layout.GetDescriptorSetDescriptorRangeCount(set); range++)
                report.AppendLine($"  descriptor {range}: type={layout.GetDescriptorSetDescriptorRangeType(set, range)} category={layout.GetDescriptorSetDescriptorRangeCategory(set, range)} index={layout.GetDescriptorSetDescriptorRangeIndexOffset(set, range)} count={layout.GetDescriptorSetDescriptorRangeDescriptorCount(set, range)}");
        }
        Assert.Equal(SlangTypeKind.ConstantBuffer, global.TypeLayout.Kind);
        Assert.Equal((nuint)16, layout.GetSize(SlangParameterCategory.Uniform));
        Assert.Equal((nint)3, layout.BindingRangeCount);
        Assert.Equal((nint)1, layout.DescriptorSetCount);
    }
}

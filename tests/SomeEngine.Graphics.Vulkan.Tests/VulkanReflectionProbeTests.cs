namespace SomeEngine.Graphics.Vulkan.Tests;

using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

public sealed class VulkanReflectionProbeTests
{
    [Fact]
    public void Entry_parameter_spirv_bindings_match_the_vulkan_register_class_abi()
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
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        byte[] code = VulkanBackend.NormalizeSpirvDescriptorBindings(
            shader.GetEntryPointCode(0),
            [
                new VulkanBackend.VulkanSpirvBindingTarget(0, 0),
                new VulkanBackend.VulkanSpirvBindingTarget(0, 65_536),
                new VulkanBackend.VulkanSpirvBindingTarget(0, 131_072),
            ],
            out VulkanBackend.VulkanSpirvBindingTarget[] activeTargets);
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        var bindings = new Dictionary<uint, uint>();
        for (int index = 5; index < words.Length;)
        {
            uint instruction = words[index];
            int wordCount = checked((int)(instruction >> 16));
            uint opcode = instruction & 0xffff;
            if (wordCount <= 0 || index > words.Length - wordCount)
                break;
            if (opcode == 71 && wordCount >= 4)
            {
                uint id = words[index + 1];
                uint decoration = words[index + 2];
                if (decoration == 33) bindings[id] = words[index + 3];
            }
            index += wordCount;
        }
        Assert.Equal([0u, 65_536u, 131_072u], bindings.Values.Order().ToArray());
        Assert.Equal(
            [0u, 65_536u, 131_072u],
            activeTargets.Select(static value => value.Binding).Order().ToArray());
        Assert.Equal(
            0u,
            VulkanBackend.NormalizeReflectedDescriptorBinding(
                0,
                SlangBindingType.MutableTexture,
                SlangParameterCategory.UnorderedAccess));
        Assert.Equal(
            65_536u,
            VulkanBackend.NormalizeReflectedDescriptorBinding(
                0,
                SlangBindingType.Texture,
                SlangParameterCategory.ShaderResource));
        Assert.Equal(
            131_072u,
            VulkanBackend.NormalizeReflectedDescriptorBinding(
                0,
                SlangBindingType.Sampler,
                SlangParameterCategory.SamplerState));
        for (nuint register = 0; register < 3; register++)
            Assert.True(shader.IsParameterLocationUsed(
                0,
                SlangParameterCategory.DescriptorTableSlot,
                0,
                register));
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
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
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

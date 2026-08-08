using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangMaterialInstancePropertyTests
{
    [Fact]
    public async Task Explicit_material_instance_property_round_trips_without_name_inference()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-instance-property-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "instance_property.slang");
        File.WriteAllText(sourcePath, ValidSource);
        try
        {
            Shader imported = SlangShaderImporter.ImportTransient(sourcePath);
            string cookedPath = Path.Combine(directory, "instance_property.shader.asset");
            AssetWriter.Write(imported, cookedPath);
            Shader cooked = await Shader.ReadAsync(cookedPath);

            ShaderMaterialInstanceProperty property = Assert.Single(
                cooked.Metadata!.MaterialInstanceProperties!);
            Assert.Equal("test.material.tint", property.CanonicalId);
            Assert.Equal("TestScalars", property.MaterialScalarLayoutName);
            Assert.Equal("ArbitraryColor", property.MaterialScalarName);
            Assert.Equal("LoadTestTint", property.Accessor);
            Assert.Equal(16u, property.Size);
            Assert.True(property.Alignment > 0);
            Assert.Equal(4u, Math.Max(1u, property.RowCount) * Math.Max(1u, property.ColumnCount));
            Assert.Equal((byte)SlangScalarType.Float32, property.ScalarType);
            Assert.True(property.DefaultValue!.Value.Span.SequenceEqual(new byte[16]));

            Assert.DoesNotContain(
                cooked.Metadata.MaterialInstanceProperties!,
                candidate => string.Equals(
                    candidate.MaterialScalarName,
                    "NotDeclared",
                    StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Invalid_canonical_id_fails_during_shader_cook()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-instance-property-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "invalid_instance_property.slang");
        File.WriteAllText(sourcePath, ValidSource.Replace("test.material.tint", "Test.material.tint", StringComparison.Ordinal));
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(
                () => SlangShaderImporter.ImportTransient(sourcePath));
            Assert.Contains("canonical id", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Canonical_property_keeps_stable_accessor_abi_across_layout_local_scalar_names()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-instance-layouts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "instance_layouts.slang");
        File.WriteAllText(sourcePath, MultiLayoutSource);
        try
        {
            Shader imported = SlangShaderImporter.ImportTransient(sourcePath);
            List<ShaderMaterialInstanceProperty> properties = imported.Metadata!
                .MaterialInstanceProperties!
                .OrderBy(static property => property.MaterialScalarLayoutName, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(2, properties.Count);
            Assert.All(properties, static property =>
            {
                Assert.Equal("test.material.color", property.CanonicalId);
                Assert.Equal("LoadTestColor", property.Accessor);
            });
            Assert.Equal("FirstScalars", properties[0].MaterialScalarLayoutName);
            Assert.Equal("FirstColor", properties[0].MaterialScalarName);
            Assert.Equal("SecondScalars", properties[1].MaterialScalarLayoutName);
            Assert.Equal("SecondColor", properties[1].MaterialScalarName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Canonical_property_may_use_layout_local_accessors()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-instance-layout-conflict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "instance_layout_conflict.slang");
        const string stableAccessor = "\"LoadTestColor\"";
        int accessorIndex = MultiLayoutSource.LastIndexOf(stableAccessor, StringComparison.Ordinal);
        Assert.True(accessorIndex >= 0);
        string conflictingSource = MultiLayoutSource[..accessorIndex]
            + "\"LoadOtherColor\""
            + MultiLayoutSource[(accessorIndex + stableAccessor.Length)..];
        File.WriteAllText(sourcePath, conflictingSource);
        try
        {
            Shader imported = SlangShaderImporter.ImportTransient(sourcePath);
            IList<ShaderMaterialInstanceProperty> properties =
                imported.Metadata!.MaterialInstanceProperties!;
            Assert.Contains(properties, static property =>
                string.Equals(property.Accessor, "LoadTestColor", StringComparison.Ordinal));
            Assert.Contains(properties, static property =>
                string.Equals(property.Accessor, "LoadOtherColor", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string ValidSource = """
        [__AttributeUsage(_AttributeTargets.Var)]
        struct InstancePropertyAttribute
        {
            string canonicalId;
            string accessor;
        };

        [MaterialScalars]
        struct TestScalars
        {
            [InstanceProperty("test.material.tint", "LoadTestTint")]
            float4 ArbitraryColor;
            float NotDeclared;
        };

        ConstantBuffer<TestScalars> Scalars;
        RWStructuredBuffer<float4> Output;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[0] = Scalars.ArbitraryColor + Scalars.NotDeclared;
        }
        """;

    private const string MultiLayoutSource = """
        [__AttributeUsage(_AttributeTargets.Var)]
        struct InstancePropertyAttribute
        {
            string canonicalId;
            string accessor;
        };

        [MaterialScalars]
        struct FirstScalars
        {
            [InstanceProperty("test.material.color", "LoadTestColor")]
            float4 FirstColor;
        };

        [MaterialScalars]
        struct SecondScalars
        {
            [InstanceProperty("test.material.color", "LoadTestColor")]
            float4 SecondColor;
        };

        ConstantBuffer<FirstScalars> First;
        ConstantBuffer<SecondScalars> Second;
        RWStructuredBuffer<float4> Output;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[0] = First.FirstColor + Second.SecondColor;
        }
        """;
}
